using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage;

namespace LiveDrop.Protocols
{
    internal static class ProtocolUtilities
    {
        internal const int MaxFrameLength = 5 * 1024 * 1024;

        internal static string NormalizeFileName(string name)
        {
            var source = string.IsNullOrWhiteSpace(name) ? "file" : name;
            var invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(source.Length);
            foreach (var c in source)
                builder.Append(invalid.Contains(c) || char.IsControl(c) ? '_' : c);
            var result = builder.ToString().Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(result) || result == "." || result == "..") result = "file";
            return result.Length > 240 ? result.Substring(0, 240) : result;
        }

        internal static byte[] Sha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(data ?? new byte[0]);
        }

        internal static byte[] Sha256(Stream stream)
        {
            using (var sha = SHA256.Create()) return sha.ComputeHash(stream);
        }

        internal static async System.Threading.Tasks.Task<StorageFile> CreateAtomicDestinationAsync(StorageFolder folder, string requestedName)
        {
            var safeName = NormalizeFileName(requestedName);
            return await folder.CreateFileAsync(safeName, CreationCollisionOption.GenerateUniqueName);
        }

        internal static string Base64Url(byte[] value)
        {
            return Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static bool IsLoopbackAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            var value = address.Trim();
            if (string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            if (value.Length > 2 && value[0] == '[' && value[value.Length - 1] == ']')
                value = value.Substring(1, value.Length - 2);
            IPAddress parsed;
            if (!IPAddress.TryParse(value, out parsed)) return false;
            if (IPAddress.IsLoopback(parsed)) return true;
            return parsed.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(parsed.MapToIPv4());
        }
    }
}
