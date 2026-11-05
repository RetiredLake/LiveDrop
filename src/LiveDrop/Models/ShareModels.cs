using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Storage;

namespace LiveDrop.Models
{
    public enum ShareTransport
    {
        MicrosoftNearby,
        GoogleQuickShare,
        Bluetooth,
        WiFiLan,
        WiFiDirect
    }

    public sealed class PeerDescriptor
    {
        public string StableId { get; private set; }
        public string DisplayName { get; private set; }
        public ShareTransport Transport { get; private set; }
        public string Address { get; private set; }
        public int Port { get; private set; }
        public string Capabilities { get; private set; }

        public PeerDescriptor(string stableId, string displayName, ShareTransport transport, string address, int port, string capabilities)
        {
            StableId = stableId ?? string.Empty;
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? AppText.Get("NearbyDevice") : displayName;
            Transport = transport;
            Address = address ?? string.Empty;
            Port = port;
            Capabilities = capabilities ?? string.Empty;
        }

        public override string ToString() { return DisplayName + " (" + Transport + ")"; }
    }

    public sealed class ShareFileDescriptor
    {
        public string Name { get; private set; }
        public long Size { get; private set; }
        public string MimeType { get; private set; }
        public StorageFile SourceFile { get; private set; }

        public ShareFileDescriptor(StorageFile sourceFile, string mimeType, long size)
        {
            SourceFile = sourceFile;
            Name = sourceFile == null ? "file" : sourceFile.Name;
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType;
            Size = size;
        }

        public ShareFileDescriptor(string name, string mimeType, long size)
        {
            SourceFile = null;
            Name = string.IsNullOrWhiteSpace(name) ? "file" : name;
            MimeType = string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType;
            Size = size;
        }
    }

    public sealed class ShareOffer
    {
        public IReadOnlyList<ShareFileDescriptor> Files { get; private set; }
        public string Text { get; private set; }
        public Uri WebLink { get; private set; }
        public bool HasContent { get { return Files.Count > 0 || WebLink != null; } }

        public ShareOffer(IReadOnlyList<ShareFileDescriptor> files, string text, Uri webLink = null)
        {
            Files = files ?? new List<ShareFileDescriptor>();
            Text = text ?? string.Empty;
            if (webLink != null && (Files.Count != 0 || !webLink.IsAbsoluteUri || (webLink.Scheme != "http" && webLink.Scheme != "https")))
                throw new ArgumentException(AppText.Get("AWebLinkOfferMustContainOneHTTP"));
            WebLink = webLink;
        }
    }

    public sealed class ShareProgress
    {
        public string SavedFilePath { get; private set; }
        public Uri ReceivedWebLink { get; private set; }
        public long BytesTransferred { get; private set; }
        public long TotalBytes { get; private set; }
        public string FileName { get; private set; }

        public ShareProgress(string fileName, long bytesTransferred, long totalBytes, string savedFilePath = null, Uri receivedWebLink = null)
        {
            FileName = fileName ?? string.Empty;
            BytesTransferred = bytesTransferred;
            TotalBytes = totalBytes;
            SavedFilePath = savedFilePath;
            ReceivedWebLink = receivedWebLink;
        }
    }

    public sealed class PeerDiscoveredEventArgs : EventArgs
    {
        public PeerDescriptor Peer { get; private set; }
        public PeerDiscoveredEventArgs(PeerDescriptor peer) { Peer = peer; }
    }

    public sealed class ShareOfferReceivedEventArgs : EventArgs
    {
        public PeerDescriptor Peer { get; private set; }
        public ShareOffer Offer { get; private set; }
        public string SecurityPin { get; private set; }
        public Func<bool, Task> CompleteAsync { get; private set; }
        private readonly Action _cancelTransfer;
        private int _cancelRequested;
        private int _completed;
        public bool IsCancellationRequested { get { return System.Threading.Volatile.Read(ref _cancelRequested) != 0; } }
        public event System.Action<ShareProgress> ProgressChanged;
        public event System.Action<bool, string> TransferCompleted;

        public ShareOfferReceivedEventArgs(PeerDescriptor peer, ShareOffer offer, Func<bool, Task> completeAsync, Action cancelTransfer = null, string securityPin = null)
        {
            Peer = peer;
            Offer = offer;
            CompleteAsync = completeAsync;
            _cancelTransfer = cancelTransfer;
            SecurityPin = securityPin ?? string.Empty;
        }

        public void CancelTransfer()
        {
            if (System.Threading.Volatile.Read(ref _completed) != 0 ||
                System.Threading.Interlocked.Exchange(ref _cancelRequested, 1) != 0) return;
            try { _cancelTransfer?.Invoke(); } catch (ObjectDisposedException) { }
        }

        internal void ReportProgress(ShareProgress progress)
        {
            var handler = ProgressChanged;
            if (handler != null) handler(progress);
        }

        internal void ReportCompletion(bool succeeded, string message)
        {
            if (System.Threading.Interlocked.Exchange(ref _completed, 1) == 0)
                TransferCompleted?.Invoke(succeeded, message);
        }
    }

    public sealed class StatusChangedEventArgs : EventArgs
    {
        public string Message { get; private set; }
        public StatusChangedEventArgs(string message) { Message = message ?? string.Empty; }
    }

    public static class ShareOfferFactory
    {
        public static async Task<ShareOffer> FromPickedFileAsync(StorageFile file)
        {
            return await FromPickedFilesAsync(file == null ? null : new[] { file });
        }

        public static async Task<ShareOffer> FromPickedFilesAsync(IReadOnlyList<StorageFile> sourceFiles)
        {
            return new ShareOffer(await StageFilesAsync(sourceFiles), string.Empty);
        }

        public static Task<ShareOffer> FromShareOperationAsync(ShareOperation operation)
        {
            return FromDataPackageViewAsync(operation == null ? null : operation.Data);
        }

        public static async Task<ShareOffer> FromDataPackageViewAsync(DataPackageView data)
        {
            var files = new List<ShareFileDescriptor>();
            var text = string.Empty;
            Uri webLink = null;
            if (data != null && data.Contains(StandardDataFormats.WebLink)) webLink = await data.GetWebLinkAsync();
#pragma warning disable 618
            else if (data != null && data.Contains(StandardDataFormats.Uri)) webLink = await data.GetUriAsync();
#pragma warning restore 618
            if (webLink != null && webLink.IsAbsoluteUri && (webLink.Scheme == "http" || webLink.Scheme == "https"))
                return new ShareOffer(files, webLink.AbsoluteUri, webLink);
            if (data != null && data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                var sourceFiles = new List<StorageFile>();
                foreach (var item in items)
                {
                    var sourceFile = item as StorageFile;
                    if (sourceFile != null) sourceFiles.Add(sourceFile);
                }
                files.AddRange(await StageFilesAsync(sourceFiles));
            }
            if (data != null && data.Contains(StandardDataFormats.Text)) text = await data.GetTextAsync();
            if (webLink == null && !string.IsNullOrWhiteSpace(text)) Uri.TryCreate(text.Trim(), UriKind.Absolute, out webLink);
            if (webLink != null && webLink.Scheme != "http" && webLink.Scheme != "https") webLink = null;
            if (files.Count == 0 && webLink != null) return new ShareOffer(files, webLink.AbsoluteUri, webLink);
            if (files.Count == 0 && !string.IsNullOrWhiteSpace(text))
            {
                var textFile = await ApplicationData.Current.LocalFolder.CreateFileAsync("SharedText.txt", CreationCollisionOption.GenerateUniqueName);
                await FileIO.WriteTextAsync(textFile, text);
                files.Add(new ShareFileDescriptor(textFile, "text/plain", checked((long)(await textFile.GetBasicPropertiesAsync()).Size)));
            }
            return new ShareOffer(files, text);
        }

        private static async Task<List<ShareFileDescriptor>> StageFilesAsync(IEnumerable<StorageFile> sources)
        {
            var result = new List<ShareFileDescriptor>();
            if (sources == null) return result;
            StorageFolder outgoingFolder = null;
            foreach (var source in sources)
            {
                if (source == null) continue;
                if (outgoingFolder == null) outgoingFolder = await GetOutgoingFolderAsync();
                // Picker and share-target files can be broker-backed. Keep a
                // local app-owned copy so the protocol can open every file
                // after discovery and authentication have completed.
                var stagedFile = await source.CopyAsync(outgoingFolder, source.Name, NameCollisionOption.GenerateUniqueName);
                var properties = await stagedFile.GetBasicPropertiesAsync();
                result.Add(new ShareFileDescriptor(stagedFile, GuessMimeType(stagedFile.FileType), checked((long)properties.Size)));
            }
            return result;
        }

        private static async Task<StorageFolder> GetOutgoingFolderAsync()
        {
            return await ApplicationData.Current.LocalFolder.CreateFolderAsync("Outgoing", CreationCollisionOption.OpenIfExists);
        }

        private static string GuessMimeType(string extension)
        {
            switch ((extension ?? string.Empty).ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".png": return "image/png";
                case ".gif": return "image/gif";
                case ".mp4": return "video/mp4";
                case ".txt": return "text/plain";
                case ".pdf": return "application/pdf";
                default: return "application/octet-stream";
            }
        }
    }
}
