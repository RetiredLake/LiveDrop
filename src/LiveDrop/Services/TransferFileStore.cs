using System;
using System.IO;
using System.Threading;
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
        internal static async Task<StorageFolder> GetReceiveFolderAsync(string mimeType)
        {
            // Received files intentionally share one user-facing destination.
            // The MIME type is kept in the signature for protocol callers that
            // already pass it, but it must never choose Pictures/Videos/Documents.
            var folder = await TryCreateReceiveFolderAsync(GetPublicUserFolderPath());
            if (folder != null) return folder;

            try
            {
                var documentsPath = KnownFolders.DocumentsLibrary.Path;
                // DocumentsLibrary is user-scoped in UWP. Its parent is the
                // user's profile on desktop Windows, which gives the normal
                // user Downloads folder when Public is unavailable.
                folder = await TryCreateReceiveFolderAsync(Path.GetDirectoryName(documentsPath));
                if (folder != null) return folder;
            }
            catch
            {
            }

            try
            {
                var downloads = await KnownFolders.DocumentsLibrary.CreateFolderAsync("Downloads", CreationCollisionOption.OpenIfExists);
                return await downloads.CreateFolderAsync("LiveDrop", CreationCollisionOption.OpenIfExists);
            }
            catch
            {
                // Some Windows 10 Mobile builds do not expose public folders to
                // a sideloaded package. Keep the transfer usable in that case,
                // while retaining the same Downloads/LiveDrop layout.
                var downloads = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Downloads", CreationCollisionOption.OpenIfExists);
                return await downloads.CreateFolderAsync("LiveDrop", CreationCollisionOption.OpenIfExists);
            }
        }

        private static string GetPublicUserFolderPath()
        {
            try
            {
                var localPath = ApplicationData.Current.LocalFolder.Path;
                var root = Path.GetPathRoot(localPath);
                if (!string.IsNullOrWhiteSpace(root))
                    return Path.Combine(root, "Users", "Public");
            }
            catch
            {
            }
            return null;
        }

        private static async Task<StorageFolder> TryCreateReceiveFolderAsync(string parentPath)
        {
            if (string.IsNullOrWhiteSpace(parentPath)) return null;
            try
            {
                var parent = await StorageFolder.GetFolderFromPathAsync(parentPath);
                var downloads = await parent.CreateFolderAsync("Downloads", CreationCollisionOption.OpenIfExists);
                return await downloads.CreateFolderAsync("LiveDrop", CreationCollisionOption.OpenIfExists);
            }
            catch
            {
                return null;
            }
        }

        internal static async Task<byte[]> ReadChunkAsync(StorageFile file, ulong position, uint length, CancellationToken cancellationToken)
        {
            if (file == null) throw new ShareProtocolException("The selected file is unavailable.");
            if (length == 0) return new byte[0];

            using (var fileStream = await file.OpenReadAsync())
            {
                if (position > fileStream.Size || (ulong)length > fileStream.Size - position)
                    throw new EndOfStreamException("The selected file ended before the requested range.");

                using (var input = fileStream.GetInputStreamAt(position))
                using (var reader = new DataReader(input))
                {
                    reader.InputStreamOptions = InputStreamOptions.Partial;
                    var result = new byte[length];
                    var offset = 0;
                    while (offset < result.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var loaded = await reader.LoadAsync((uint)(result.Length - offset));
                        if (loaded == 0) break;
                        var buffer = new byte[loaded];
                        reader.ReadBytes(buffer);
                        System.Buffer.BlockCopy(buffer, 0, result, offset, buffer.Length);
                        offset += buffer.Length;
                    }
                    if (offset != result.Length)
                        throw new EndOfStreamException("The selected file ended before the requested range.");
                    return result;
                }
            }
        }

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
            byte[] result;
            CryptographicBuffer.CopyToByteArray(hash.GetValueAndReset(), out result);
            return result;
        }
    }
}
