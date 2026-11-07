using System;
using System.Globalization;
using System.Linq;

namespace System.Net
{
    // Only address parsing/serialization used by discovery; no DNS resolution.
    internal sealed class IPAddress
    {
        private readonly byte[] _bytes;
        internal IPAddress(byte[] bytes)
        {
            if (bytes == null || (bytes.Length != 4 && bytes.Length != 16)) throw new ArgumentException("Invalid IP address.");
            _bytes = (byte[])bytes.Clone();
        }
        internal static IPAddress Loopback { get { return new IPAddress(new byte[] { 127, 0, 0, 1 }); } }
        internal byte[] GetAddressBytes() { return (byte[])_bytes.Clone(); }
        internal bool IsIPv4MappedToIPv6 { get { return _bytes.Length == 16 && _bytes.Take(10).All(b => b == 0) && _bytes[10] == 255 && _bytes[11] == 255; } }
        internal IPAddress MapToIPv4() { return new IPAddress(_bytes.Skip(_bytes.Length - 4).ToArray()); }
        internal static bool IsLoopback(IPAddress address)
        {
            return address._bytes.Length == 4 ? address._bytes[0] == 127 : address._bytes.Take(15).All(b => b == 0) && address._bytes[15] == 1;
        }
        internal static IPAddress Parse(string value)
        {
            IPAddress result; if (!TryParse(value, out result)) throw new FormatException("Invalid IP address."); return result;
        }
        internal static bool TryParse(string value, out IPAddress result)
        {
            result = null;
            if (string.IsNullOrWhiteSpace(value)) return false;
            if (value.IndexOf(':') < 0)
            {
                var parts = value.Split('.'); if (parts.Length != 4) return false;
                var bytes = new byte[4];
                for (int i = 0; i < 4; i++) if (!byte.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out bytes[i])) return false;
                result = new IPAddress(bytes); return true;
            }
            var zone = value.IndexOf('%'); if (zone >= 0) value = value.Substring(0, zone);
            if (value.IndexOf('.') >= 0)
            {
                var colon = value.LastIndexOf(':'); IPAddress ipv4;
                if (!TryParse(value.Substring(colon + 1), out ipv4) || ipv4._bytes.Length != 4) return false;
                value = value.Substring(0, colon + 1) + ((ipv4._bytes[0] << 8) | ipv4._bytes[1]).ToString("x") + ":" + ((ipv4._bytes[2] << 8) | ipv4._bytes[3]).ToString("x");
            }
            var halves = value.Split(new[] { "::" }, StringSplitOptions.None); if (halves.Length > 2) return false;
            var left = halves[0].Length == 0 ? new string[0] : halves[0].Split(':');
            var right = halves.Length == 1 || halves[1].Length == 0 ? new string[0] : halves[1].Split(':');
            if (halves.Length == 1 ? left.Length != 8 : left.Length + right.Length >= 8) return false;
            var words = new ushort[8];
            for (int i = 0; i < left.Length; i++) if (left[i].Length > 4 || !ushort.TryParse(left[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out words[i])) return false;
            for (int i = 0; i < right.Length; i++) if (right[i].Length > 4 || !ushort.TryParse(right[i], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out words[8 - right.Length + i])) return false;
            var data = new byte[16]; for (int i = 0; i < 8; i++) { data[i * 2] = (byte)(words[i] >> 8); data[i * 2 + 1] = (byte)words[i]; }
            result = new IPAddress(data); return true;
        }
        public override string ToString()
        {
            return _bytes.Length == 4 ? string.Join(".", _bytes.Select(b => b.ToString(CultureInfo.InvariantCulture))) : string.Join(":", Enumerable.Range(0, 8).Select(i => ((_bytes[i * 2] << 8) | _bytes[i * 2 + 1]).ToString("x")));
        }
    }
}
