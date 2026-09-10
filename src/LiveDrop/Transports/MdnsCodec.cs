using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using LiveDrop.Protocols;

namespace LiveDrop.Transports
{
    internal sealed class MdnsServiceRecord
    {
        internal string InstanceName { get; set; }
        internal string HostName { get; set; }
        internal string Address { get; set; }
        internal int Port { get; set; }
        internal string DisplayName { get; set; }
        internal byte[] EndpointInfo { get; set; }
    }

    internal static class MdnsCodec
    {
        internal const string QuickShareService = "_FC9F5ED42C8A._tcp.local.";

        internal static byte[] BuildQuery(string serviceType)
        {
            var stream = new MemoryStream();
            WriteUInt16(stream, (ushort)new Random().Next(1, ushort.MaxValue));
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 1);
            WriteUInt16(stream, 0); WriteUInt16(stream, 0); WriteUInt16(stream, 0);
            WriteName(stream, serviceType);
            WriteUInt16(stream, 12);
            WriteUInt16(stream, 1);
            return stream.ToArray();
        }

        internal static byte[] BuildAnnouncement(string serviceType, string instanceName, string hostName, string address, int port, byte[] endpointInfo)
        {
            var stream = new MemoryStream();
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 0x8400);
            WriteUInt16(stream, 4);
            WriteUInt16(stream, 0); WriteUInt16(stream, 0); WriteUInt16(stream, 0);

            WriteRecord(stream, serviceType, 12, 4500, BuildNameRdata(instanceName));
            var service = instanceName;
            var host = string.IsNullOrWhiteSpace(hostName) ? "livedrop.local." : hostName;
            WriteRecord(stream, service, 33, 120, BuildSrvRdata(port, host));
            WriteRecord(stream, service, 16, 120, BuildTxtRdata(endpointInfo));
            WriteRecord(stream, host, 1, 120, BuildAddressRdata(address));
            return stream.ToArray();
        }

        internal static IList<MdnsServiceRecord> Parse(byte[] data, string serviceType)
        {
            var results = new Dictionary<string, MdnsServiceRecord>(StringComparer.OrdinalIgnoreCase);
            if (data == null || data.Length < 12) return results.Values.ToList();
            var offset = 0;
            ReadUInt16(data, ref offset); ReadUInt16(data, ref offset);
            var questions = ReadUInt16(data, ref offset);
            var answers = ReadUInt16(data, ref offset);
            var authority = ReadUInt16(data, ref offset);
            var additional = ReadUInt16(data, ref offset);
            for (var i = 0; i < questions; i++)
            {
                ReadName(data, ref offset); offset += 4;
                if (offset > data.Length) return results.Values.ToList();
            }
            for (var i = 0; i < answers + authority + additional; i++)
            {
                string name;
                try { name = ReadName(data, ref offset); }
                catch { break; }
                if (offset + 10 > data.Length) break;
                var type = ReadUInt16(data, ref offset);
                ReadUInt16(data, ref offset);
                ReadUInt32(data, ref offset);
                var length = ReadUInt16(data, ref offset);
                if (offset + length > data.Length) break;
                var rdataOffset = offset;
                if (type == 12)
                {
                    var ptrOffset = offset;
                    var instance = ReadName(data, ref ptrOffset);
                    if (string.Equals(name, serviceType, StringComparison.OrdinalIgnoreCase))
                        Get(results, instance).InstanceName = instance;
                }
                else if (type == 33 && length >= 6)
                {
                    offset += 4;
                    var record = Get(results, name);
                    record.Port = ReadUInt16(data, ref offset);
                    record.HostName = ReadName(data, ref offset);
                }
                else if (type == 16)
                {
                    var record = Get(results, name);
                    ParseTxt(data, rdataOffset, length, record);
                    offset += length;
                }
                else if (type == 1 && length == 4)
                {
                    var record = results.Values.FirstOrDefault(x => string.Equals(x.HostName, name, StringComparison.OrdinalIgnoreCase));
                    if (record != null) record.Address = new IPAddress(data.Skip(offset).Take(4).ToArray()).ToString();
                    offset += length;
                }
                else offset += length;
            }
            return results.Values.Where(x => x.Port > 0 && !string.IsNullOrWhiteSpace(x.Address)).ToList();
        }

        private static MdnsServiceRecord Get(Dictionary<string, MdnsServiceRecord> records, string name)
        {
            MdnsServiceRecord value;
            if (!records.TryGetValue(name, out value))
            {
                value = new MdnsServiceRecord { InstanceName = name };
                records[name] = value;
            }
            return value;
        }

        private static void ParseTxt(byte[] data, int offset, int length, MdnsServiceRecord record)
        {
            var end = offset + length;
            while (offset < end)
            {
                var itemLength = data[offset++];
                if (itemLength > end - offset) break;
                var item = Encoding.UTF8.GetString(data, offset, itemLength);
                offset += itemLength;
                var split = item.IndexOf('=');
                if (split < 0) continue;
                if (string.Equals(item.Substring(0, split), "n", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var endpointInfo = Convert.FromBase64String(item.Substring(split + 1).Replace('-', '+').Replace('_', '/').PadRight(((item.Length - split - 1 + 3) / 4) * 4, '='));
                        record.EndpointInfo = endpointInfo;
                        record.DisplayName = ReadEndpointName(endpointInfo);
                    }
                    catch (FormatException) { }
                }
            }
        }

        private static string ReadEndpointName(byte[] endpointInfo)
        {
            if (endpointInfo == null || endpointInfo.Length < 18) return "Quick Share device";
            var length = endpointInfo[17];
            if (18 + length > endpointInfo.Length) return "Quick Share device";
            return Encoding.UTF8.GetString(endpointInfo, 18, length);
        }

        private static byte[] BuildNameRdata(string name)
        {
            var stream = new MemoryStream(); WriteName(stream, name); return stream.ToArray();
        }

        private static byte[] BuildSrvRdata(int port, string hostName)
        {
            var stream = new MemoryStream(); WriteUInt16(stream, 0); WriteUInt16(stream, 0); WriteUInt16(stream, (ushort)port); WriteName(stream, hostName); return stream.ToArray();
        }

        private static byte[] BuildTxtRdata(byte[] endpointInfo)
        {
            var text = Encoding.UTF8.GetBytes("n=" + ProtocolUtilities.Base64Url(endpointInfo ?? new byte[0]));
            var stream = new MemoryStream(); stream.WriteByte((byte)Math.Min(255, text.Length)); stream.Write(text, 0, Math.Min(255, text.Length)); return stream.ToArray();
        }

        private static byte[] BuildAddressRdata(string address)
        {
            IPAddress parsed;
            if (!IPAddress.TryParse(address, out parsed) || parsed.GetAddressBytes().Length != 4) parsed = IPAddress.Loopback;
            return parsed.GetAddressBytes();
        }

        private static void WriteRecord(MemoryStream stream, string name, ushort type, uint ttl, byte[] rdata)
        {
            WriteName(stream, name); WriteUInt16(stream, type); WriteUInt16(stream, 1); WriteUInt32(stream, ttl); WriteUInt16(stream, (ushort)rdata.Length); stream.Write(rdata, 0, rdata.Length);
        }

        private static void WriteName(MemoryStream stream, string name)
        {
            var labels = (name ?? string.Empty).TrimEnd('.').Split('.');
            foreach (var label in labels)
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                stream.WriteByte((byte)Math.Min(63, bytes.Length));
                stream.Write(bytes, 0, Math.Min(63, bytes.Length));
            }
            stream.WriteByte(0);
        }

        private static string ReadName(byte[] data, ref int offset)
        {
            var output = new List<string>();
            var cursor = offset;
            var jumped = false;
            var guard = 0;
            while (cursor < data.Length && guard++ < 64)
            {
                var length = data[cursor++];
                if (length == 0) { if (!jumped) offset = cursor; break; }
                if ((length & 0xC0) == 0xC0)
                {
                    if (cursor >= data.Length) throw new InvalidDataException("Invalid DNS pointer.");
                    var pointer = ((length & 0x3F) << 8) | data[cursor++];
                    if (!jumped) offset = cursor;
                    cursor = pointer; jumped = true; continue;
                }
                if (length > 63 || cursor + length > data.Length) throw new InvalidDataException("Invalid DNS label.");
                output.Add(Encoding.UTF8.GetString(data, cursor, length)); cursor += length;
            }
            return string.Join(".", output) + ".";
        }

        private static ushort ReadUInt16(byte[] data, ref int offset) { if (offset + 2 > data.Length) throw new InvalidDataException(); var value = (ushort)((data[offset] << 8) | data[offset + 1]); offset += 2; return value; }
        private static uint ReadUInt32(byte[] data, ref int offset) { if (offset + 4 > data.Length) throw new InvalidDataException(); var value = (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]); offset += 4; return value; }
        private static void WriteUInt16(Stream stream, ushort value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void WriteUInt32(Stream stream, uint value) { stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
    }
}
