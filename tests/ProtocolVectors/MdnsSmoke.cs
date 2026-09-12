using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using LiveDrop.Transports;

namespace LiveDrop.Protocols
{
    internal static class ProtocolUtilities
    {
        internal static string Base64Url(byte[] data) { return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }
    }
}

internal static class MdnsSmoke
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static byte[] Name(string value)
    {
        var data = new List<byte>();
        foreach (var label in value.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            data.Add((byte)bytes.Length); data.AddRange(bytes);
        }
        data.Add(0); return data.ToArray();
    }

    private static byte[] Response(string name, byte type, byte[] payload)
    {
        // Independent DNS fixture: one answer, no questions, IN class, 120-second TTL.
        return new byte[] { 0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0 }
            .Concat(Name(name)).Concat(new byte[] { 0, type, 0, 1, 0, 0, 0, 120, (byte)(payload.Length >> 8), (byte)payload.Length })
            .Concat(payload).ToArray();
    }

    private static void Main()
    {
        const string service = "_FC9F5ED42C8A._tcp.local.";
        const string instance = "I2FiY2T8n14AAA";
        var endpoint = new byte[22]; endpoint[17] = 4;
        Buffer.BlockCopy(Encoding.UTF8.GetBytes("Test"), 0, endpoint, 18, 4);
        var announcement = MdnsCodec.BuildAnnouncement(service, instance, "test.local.", "192.168.1.20", 12345, endpoint);
        Check(announcement[4] == 0 && announcement[5] == 0, "Announcement must have zero DNS questions");
        Check(announcement[6] == 0 && announcement[7] == 4, "Announcement must have four answers");
        var parsed = MdnsCodec.Parse(announcement, service);
        Check(parsed.Count == 1 && parsed[0].DisplayName == "Test" && parsed[0].Port == 12345 && parsed[0].Address == "192.168.1.20", "Announcement did not resolve");

        var records = new Dictionary<string, MdnsServiceRecord>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        MdnsCodec.Parse(Response("test.local.", 1, new byte[] { 192, 168, 1, 42 }), service, records, addresses);
        var srv = new byte[] { 0, 0, 0, 0, 0x30, 0x39 }.Concat(Name("test.local.")).ToArray();
        parsed = MdnsCodec.Parse(Response(instance + "." + service, 33, srv), service, records, addresses);
        Check(parsed.Count == 1 && parsed[0].Address == "192.168.1.42", "Separate A and SRV replies must resolve");
        parsed = MdnsCodec.Parse(Response("unrelated._spotify-connect._tcp.local.", 33, srv), service, records, addresses);
        Check(parsed.Count == 1, "Unrelated services must not appear");
        Console.WriteLine("PASS: DNS header, advertisement fields, split responses, unrelated-service filtering");
    }
}
