using System;
using System.Threading.Tasks;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using LiveDrop.Protocols;

namespace LiveDrop.Services
{
    internal static class TransferFileStore
    {
        internal static async Task<StorageFile> CopyToAtomicAsync(StorageFile source, StorageFolder destination, string requestedName)
        {
            if (source == null || destination == null) throw new ArgumentNullException();
            var safeName = ProtocolUtilities.NormalizeFileName(requestedName);
            var temporary = await destination.CreateFileAsync("." + safeName + ".livedrop-part", CreationCollisionOption.GenerateUniqueName);
            try
            {
                using (var input = await source.OpenReadAsync())
                using (var output = await temporary.OpenAsync(FileAccessMode.ReadWrite))
                {
                    await RandomAccessStream.CopyAsync(input, output);
                    await output.FlushAsync();
                }
                var finalName = ProtocolUtilities.NormalizeFileName(requestedName);
                await temporary.RenameAsync(finalName, NameCollisionOption.GenerateUniqueName);
                return await destination.GetFileAsync(temporary.Name);
            }
            catch
            {
                try { await temporary.DeleteAsync(); } catch { }
                throw;
            }
        }

        internal static async Task<byte[]> Sha256Async(StorageFile file)
        {
            var provider = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Sha256);
            var hash = provider.CreateHash();
            using (var input = await file.OpenReadAsync())
            using (var reader = new DataReader(input))
            {
                while (input.Position < input.Size)
                {
                    var remaining = (long)(input.Size - input.Position);
                    var count = await reader.LoadAsync((uint)Math.Min(64 * 1024, remaining));
                    if (count == 0) break;
                    var buffer = new byte[count];
                    reader.ReadBytes(buffer);
                    hash.Append(CryptographicBuffer.CreateFromByteArray(buffer));
                }
            }
            return CryptographicBuffer.CopyToByteArray(hash.GetValueAndReset());
        }
    }
}
