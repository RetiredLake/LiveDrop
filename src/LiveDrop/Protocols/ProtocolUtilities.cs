using System;
using System.IO;
using System.Linq;
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
    }
}

