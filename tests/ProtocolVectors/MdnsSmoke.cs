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
        TestResources.Initialize();
        const string service = "_FC9F5ED42C8A._tcp.local.";
        const string instance = "I2FiY2T8n14AAA";
        var endpoint = new byte[22]; endpoint[17] = 4;
        Buffer.BlockCopy(Encoding.UTF8.GetBytes("Test"), 0, endpoint, 18, 4);
        var announcement = MdnsCodec.BuildAnnouncement(service, instance, "test.local.", "192.168.1.20", 12345, endpoint);
        Check(MdnsCodec.IsQueryFor(MdnsCodec.BuildQuery(service), service, instance, "test.local."), "Service queries must trigger an announcement");
        Check(!MdnsCodec.IsQueryFor(announcement, service, instance, "test.local."), "Responses must not trigger response loops");
        Check(announcement[4] == 0 && announcement[5] == 0, "Announcement must have zero DNS questions");
        Check(announcement[6] == 0 && announcement[7] == 4, "Announcement must have four answers");
        var parsed = MdnsCodec.Parse(announcement, service);
        Check(parsed.Count == 1 && parsed[0].DisplayName == "Test" && parsed[0].Port == 12345 && parsed[0].Address == "192.168.1.20", "Announcement did not resolve");

        var records = new Dictionary<string, MdnsServiceRecord>(StringComparer.OrdinalIgnoreCase);
        var addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var knownAnswerQuery = MdnsCodec.BuildQuery(service).Concat(announcement.Skip(12)).ToArray();
        knownAnswerQuery[7] = 4;
        Check(MdnsCodec.Parse(knownAnswerQuery, service, records, addresses).Count == 0 && records.Count == 0,
            "Cached known answers in a query must not create live peers");
        var ghost = new MdnsServiceRecord { HostName = "livedrop-fbO4.local.", Address = "192.168.86.21", Port = 50566, DisplayName = "Windows phone" };
        var live = new MdnsServiceRecord { HostName = "livedrop-3j2l.local.", Address = "192.168.86.36", Port = 50606, DisplayName = "Windows phone" };
        Check(!MdnsCodec.IsUsablePeerAnnouncement(ghost, "192.168.86.36"), "A stale LiveDrop address from another interface must not appear");
        Check(MdnsCodec.IsUsablePeerAnnouncement(live, "192.168.86.36"), "The current direct LiveDrop endpoint must remain visible");
        Check(MdnsCodec.IsUsablePeerAnnouncement(ghost, "192.168.86.21"), "A genuinely different phone with the same name must remain visible");
        Check(MdnsCodec.IsUsablePeerAnnouncement(new MdnsServiceRecord { HostName = "android.local.", Address = "192.168.86.40" }, "192.168.86.1"),
            "Generic DNS-SD proxy responses must remain supported");
        MdnsCodec.Parse(Response("test.local.", 1, new byte[] { 192, 168, 1, 42 }), service, records, addresses);
        var srv = new byte[] { 0, 0, 0, 0, 0x30, 0x39 }.Concat(Name("test.local.")).ToArray();
        parsed = MdnsCodec.Parse(Response(instance + "." + service, 33, srv), service, records, addresses);
        Check(parsed.Count == 0, "A and SRV replies must wait for endpoint TXT metadata before publishing a peer");
        var txt = Encoding.UTF8.GetBytes("n=" + Convert.ToBase64String(endpoint));
        parsed = MdnsCodec.Parse(Response(instance + "." + service, 16, new byte[] { (byte)txt.Length }.Concat(txt).ToArray()), service, records, addresses);
        Check(parsed.Count == 1 && parsed[0].Address == "192.168.1.42" && parsed[0].DisplayName == "Test", "Split TXT must complete the peer with its actual name");
        parsed = MdnsCodec.Parse(Response("unrelated._spotify-connect._tcp.local.", 33, srv), service, records, addresses);
        Check(parsed.Count == 0, "Unrelated traffic must not refresh cached peers");
        Check(MdnsCodec.Parse(MdnsCodec.BuildQuery(service), service, records, addresses).Count == 0, "Queries must not refresh cached peers");
        Check(MdnsCodec.Parse(new byte[0], service, records, addresses).Count == 0, "Empty packets must not refresh cached peers");
        parsed = MdnsCodec.Parse(Response("test.local.", 1, new byte[] { 192, 168, 1, 43 }), service, records, addresses);
        Check(parsed.Count == 1 && parsed[0].Address == "192.168.1.43", "Address updates must refresh their associated peer");
        var newer = MdnsCodec.BuildAnnouncement(service, "new-instance", "new-host.local.", "192.168.1.43", 23456, endpoint);
        records.Values.First().EndpointSeenUtc = DateTime.UtcNow - TimeSpan.FromSeconds(1);
        parsed = MdnsCodec.Parse(newer, service, records, addresses);
        Check(parsed.Count == 1 && parsed[0].Port == 23456, "One IP must resolve to the latest advertised endpoint");
        parsed = MdnsCodec.Parse(Response("test.local.", 1, new byte[] { 192, 168, 1, 43 }), service, records, addresses);
        Check(parsed.Count == 0, "Old host-address replies must not replace a newer endpoint on the same IP");
        foreach (var record in records.Values) record.EndpointSeenUtc = DateTime.UtcNow - TimeSpan.FromSeconds(11);
        parsed = MdnsCodec.Parse(Response("new-host.local.", 1, new byte[] { 192, 168, 1, 43 }), service, records, addresses);
        Check(parsed.Count == 0, "Address traffic must not revive expired service endpoints");
        var goodbye = MdnsCodec.BuildAnnouncement(service, "new-instance", "new-host.local.", "192.168.1.43", 23456, endpoint, true);
        MdnsCodec.Parse(goodbye, service, records, addresses);
        Check(!records.ContainsKey("new-instance." + service), "Goodbye must remove its retired instance");
        Console.WriteLine("PASS: DNS header, advertisement fields, split responses, unrelated-service filtering");
    }
}
