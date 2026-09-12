using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using LiveDrop.Models;
using LiveDrop.Protocols;
using LiveDrop.Transports;

namespace LiveDrop.Protocols.QuickShare
{
    internal static class QuickShareSession
    {
        private static long _payloadSequence = DateTime.UtcNow.Ticks;

        internal static async Task SendAsync(SocketConnection connection, string displayName, string endpointId, byte[] endpointInfo, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            var clientKey = P256KeyAgreement.Create();
            var clientFinish = QuickShareFrames.BuildClientFinish(clientKey.ExportGenericPublicKey());
            var clientInit = QuickShareFrames.BuildClientInit(RandomBytes(32), Sha512(clientFinish));
            await connection.WriteFrameAsync(QuickShareFrames.BuildConnectionRequest(endpointId, displayName, endpointInfo), cancellationToken);
            await connection.WriteFrameAsync(clientInit, cancellationToken);

            var serverInit = await connection.ReadFrameAsync(cancellationToken);
            var serverMessage = QuickShareFrames.ParseUkey(serverInit);
            if (serverMessage.Type != QuickShareFrames.UkeyServerInit) throw new ShareProtocolException("Quick Share returned an unexpected UKEY2 message.");
            var sharedSecret = clientKey.ComputeSharedSecretHash(ParseServerPublicKey(serverMessage.Data));
            byte[] authKey;
            var crypto = QuickShareCrypto.Create(sharedSecret, clientInit, serverInit, false, out authKey);
            using (var keepAliveSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task keepAlive = null;
                try
                {
                    await connection.WriteFrameAsync(clientFinish, cancellationToken);
                    // The server sends the connection response and paired-key
                    // frame first. Official Quick Share clients wait for these
                    // frames before replying; sending them in the opposite
                    // order leaves the receiver and sender waiting on each
                    // other before the introduction is sent.
                    if (!QuickShareFrames.IsAcceptedConnection(await connection.ReadFrameAsync(cancellationToken))) throw new ShareProtocolException("Quick Share rejected the connection.");
                    await connection.WriteFrameAsync(QuickShareFrames.BuildConnectionAccept(), cancellationToken);
                    keepAlive = KeepAliveLoopAsync(connection, crypto, keepAliveSource.Token);

                    await ReadSharingFrameAsync(connection, crypto, cancellationToken);
                    await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildPairedKeyEncryption(), cancellationToken);
                    await ReadSharingFrameAsync(connection, crypto, cancellationToken);
                    await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildPairedKeyResult(), cancellationToken);
                    await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildIntroduction(offer.Files), cancellationToken);
                    if (!QuickShareFrames.IsAcceptedSharingResponse(await ReadSharingFrameAsync(connection, crypto, cancellationToken))) throw new ShareProtocolException("Quick Share recipient rejected the transfer.");

                    for (var fileIndex = 0; fileIndex < offer.Files.Count; fileIndex++)
                    {
                        await SendFileAsync(connection, crypto, offer.Files[fileIndex], 1000 + fileIndex, progress, cancellationToken);
                    }
                    await connection.WriteFrameAsync(crypto.EncryptOffline(QuickShareFrames.BuildDisconnection()), cancellationToken);
                }
                finally
                {
                    keepAliveSource.Cancel();
                    if (keepAlive != null) { try { await keepAlive; } catch (OperationCanceledException) { } }
                }
            }
        }

        internal static async Task ReceiveAsync(StreamSocket socket, string displayName, Func<PeerDescriptor, ShareOffer, Task<bool>> consent, Action<string> status, Action<ShareProgress> progress, CancellationToken cancellationToken)
        {
            using (var connection = new SocketConnection(socket))
            {
                var requestFrame = await connection.ReadFrameAsync(cancellationToken);
                if (QuickShareFrames.ReadOfflineFrameType(requestFrame) != QuickShareFrames.NearbyConnectionRequest) throw new ShareProtocolException("Quick Share connection request was invalid.");
                var request = QuickShareFrames.ParseConnectionRequest(requestFrame);
                var peer = new PeerDescriptor(request.EndpointId ?? "quickshare", request.EndpointName ?? "Android device", ShareTransport.GoogleQuickShare, socket.Information.RemoteAddress.RawName, 0, "Quick Share/UKEY2");
                var clientInit = await connection.ReadFrameAsync(cancellationToken);
                var clientMessage = QuickShareFrames.ParseUkey(clientInit);
                if (clientMessage.Type != QuickShareFrames.UkeyClientInit) throw new ShareProtocolException("Quick Share client did not send UKEY2 ClientInit.");

                var serverKey = P256KeyAgreement.Create();
                var serverInit = QuickShareFrames.BuildServerInit(RandomBytes(32), serverKey.ExportGenericPublicKey());
                await connection.WriteFrameAsync(serverInit, cancellationToken);
                var clientFinish = await connection.ReadFrameAsync(cancellationToken);
                var clientFinishMessage = QuickShareFrames.ParseUkey(clientFinish);
                if (clientFinishMessage.Type != QuickShareFrames.UkeyClientFinish) throw new ShareProtocolException("Quick Share client did not send UKEY2 ClientFinish.");
                if (!FixedEquals(Sha512(clientFinish), QuickShareFrames.ParseClientCommitment(clientMessage.Data))) throw new ShareProtocolException("Quick Share UKEY2 commitment did not verify.");
                var sharedSecret = serverKey.ComputeSharedSecretHash(QuickShareFrames.ParseClientFinishedPublicKey(clientFinishMessage.Data));
                byte[] authKey;
                var crypto = QuickShareCrypto.Create(sharedSecret, clientInit, serverInit, true, out authKey);
                using (var keepAliveSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    Task keepAlive = null;
                    try
                    {
                        if (!QuickShareFrames.IsAcceptedConnection(await connection.ReadFrameAsync(cancellationToken))) throw new ShareProtocolException("Quick Share client rejected the connection.");
                        await connection.WriteFrameAsync(QuickShareFrames.BuildConnectionAccept(), cancellationToken);
                        keepAlive = KeepAliveLoopAsync(connection, crypto, keepAliveSource.Token);
                        await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildPairedKeyEncryption(), cancellationToken);
                        await ReadSharingFrameAsync(connection, crypto, cancellationToken);
                        await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildPairedKeyResult(), cancellationToken);
                        await ReadSharingFrameAsync(connection, crypto, cancellationToken);
                        var introduction = await ReadSharingFrameAsync(connection, crypto, cancellationToken);
                        var metadata = QuickShareFrames.ParseIntroduction(introduction);
                        ValidateMetadata(metadata);
                        var files = new List<ShareFileDescriptor>();
                        foreach (var item in metadata) files.Add(new ShareFileDescriptor(item.Name, item.MimeType, item.Size));
                        var offer = new ShareOffer(files, string.Empty);
                        var accepted = consent == null || await consent(peer, offer);
                        await SendSharingFrameAsync(connection, crypto, accepted ? QuickShareFrames.BuildAcceptTransfer() : QuickShareFrames.BuildRejectTransfer(), cancellationToken);
                        if (!accepted) return;
                        await ReceiveFilesAsync(connection, crypto, metadata, status, progress, cancellationToken);
                        await ReadDisconnectionAsync(connection, crypto, cancellationToken);
                        status?.Invoke("Quick Share transfer received in the app's Received folder.");
                    }
                    finally
                    {
                        keepAliveSource.Cancel();
                        if (keepAlive != null) { try { await keepAlive; } catch (OperationCanceledException) { } }
                    }
                }
            }
        }

        private static async Task SendFileAsync(SocketConnection connection, QuickShareCrypto crypto, ShareFileDescriptor file, long payloadId, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            if (file.SourceFile == null) throw new ShareProtocolException("The outgoing file has no local source.");
            using (var input = await file.SourceFile.OpenReadAsync())
            using (var reader = new DataReader(input))
            {
                long offset = 0;
                progress?.Report(new ShareProgress(file.Name, 0, file.Size));
                while (offset < (long)input.Size)
                {
                    var remaining = (long)input.Size - offset;
                    var requested = (uint)Math.Min(512 * 1024, remaining);
                    var count = await reader.LoadAsync(requested);
                    if (count == 0) throw new EndOfStreamException("The outgoing file ended early.");
                    var body = new byte[count]; reader.ReadBytes(body);
                    await connection.WriteFrameAsync(crypto.EncryptOffline(QuickShareFrames.BuildFileChunk(payloadId, file.Size, offset, body, false)), cancellationToken);
                    offset += count;
                    progress?.Report(new ShareProgress(file.Name, offset, file.Size));
                }
                await connection.WriteFrameAsync(crypto.EncryptOffline(QuickShareFrames.BuildFileChunk(payloadId, file.Size, offset, new byte[0], true)), cancellationToken);
            }
        }

        private static async Task ReceiveFilesAsync(SocketConnection connection, QuickShareCrypto crypto, IList<QuickShareFileMetadata> metadata, Action<string> status, Action<ShareProgress> progress, CancellationToken cancellationToken)
        {
            var folder = await ApplicationData.Current.LocalFolder.CreateFolderAsync("Received", CreationCollisionOption.OpenIfExists);
            var writers = new Dictionary<long, IncomingFile>();
            try
            {
                foreach (var item in metadata)
                {
                    var temp = await folder.CreateFileAsync("." + ProtocolUtilities.NormalizeFileName(item.Name) + ".part", CreationCollisionOption.GenerateUniqueName);
                    var stream = await temp.OpenAsync(FileAccessMode.ReadWrite);
                    writers[item.PayloadId] = new IncomingFile { Metadata = item, Temporary = temp, Stream = stream, Writer = new DataWriter(stream) };
                    progress?.Invoke(new ShareProgress(item.Name, 0, item.Size));
                }
                var completed = 0;
                while (completed < metadata.Count)
                {
                    var chunk = await ReadPayloadChunkAsync(connection, crypto, cancellationToken);
                    IncomingFile file;
                    if (chunk.PayloadType != 2 || !writers.TryGetValue(chunk.PayloadId, out file)) throw new ShareProtocolException("Quick Share sent an unknown file payload.");
                    if (chunk.Offset != file.Offset) throw new ShareProtocolException("Quick Share sent a file chunk at the wrong offset.");
                    if (chunk.Body != null && chunk.Body.Length > 0) { file.Writer.WriteBytes(chunk.Body); await file.Writer.StoreAsync(); file.Offset += chunk.Body.Length; progress?.Invoke(new ShareProgress(file.Metadata.Name, file.Offset, file.Metadata.Size)); }
                    if (!chunk.Last) continue;
                    if (file.Offset != file.Metadata.Size) throw new ShareProtocolException("Quick Share file size did not match its introduction metadata.");
                    await file.Writer.FlushAsync();
                    CloseIncomingFile(file);
                    await file.Temporary.RenameAsync(ProtocolUtilities.NormalizeFileName(file.Metadata.Name), NameCollisionOption.GenerateUniqueName);
                    file.Completed = true;
                    completed++;
                    progress?.Invoke(new ShareProgress(file.Metadata.Name, file.Offset, file.Metadata.Size));
                    status?.Invoke("Received " + file.Metadata.Name + " (" + completed + "/" + metadata.Count + ").");
                }
            }
            catch
            {
                foreach (var file in writers.Values)
                {
                    CloseIncomingFile(file);
                    if (!file.Completed) { try { await file.Temporary.DeleteAsync(); } catch { } }
                }
                throw;
            }
        }

        private static async Task ReadDisconnectionAsync(SocketConnection connection, QuickShareCrypto crypto, CancellationToken cancellationToken)
        {
            while (true)
            {
                var frame = crypto.DecryptOffline(await connection.ReadFrameAsync(cancellationToken));
                var type = QuickShareFrames.ReadOfflineFrameType(frame);
                if (type == QuickShareFrames.NearbyKeepAlive) continue;
                if (type != 6) throw new ShareProtocolException("Quick Share did not send a transfer disconnection.");
                return;
            }
        }

        private static void CloseIncomingFile(IncomingFile file)
        {
            if (file == null) return;
            var writer = file.Writer;
            file.Writer = null;
            if (writer != null)
            {
                try { writer.DetachStream(); } catch { }
                try { writer.Dispose(); } catch { }
            }
            var stream = file.Stream;
            file.Stream = null;
            if (stream != null) { try { stream.Dispose(); } catch { } }
        }

        private static async Task SendSharingFrameAsync(SocketConnection connection, QuickShareCrypto crypto, byte[] frame, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _payloadSequence);
            await connection.WriteFrameAsync(crypto.EncryptOffline(QuickShareFrames.BuildBytesPayload(frame, id, false)), cancellationToken);
            await connection.WriteFrameAsync(crypto.EncryptOffline(QuickShareFrames.BuildBytesPayload(new byte[0], id, true)), cancellationToken);
        }

        private static async Task<byte[]> ReadSharingFrameAsync(SocketConnection connection, QuickShareCrypto crypto, CancellationToken cancellationToken)
        {
            using (var buffer = new MemoryStream())
            {
                while (true)
                {
                    var chunk = await ReadPayloadChunkAsync(connection, crypto, cancellationToken);
                    if (chunk.PayloadType != 1) continue;
                    if (chunk.Offset != buffer.Length) throw new ShareProtocolException("Quick Share setup payload offset was invalid.");
                    if (chunk.Body != null) buffer.Write(chunk.Body, 0, chunk.Body.Length);
                    if (chunk.Last) return buffer.ToArray();
                }
            }
        }

        private static async Task<QuickSharePayloadChunk> ReadPayloadChunkAsync(SocketConnection connection, QuickShareCrypto crypto, CancellationToken cancellationToken)
        {
            while (true)
            {
                var frame = crypto.DecryptOffline(await connection.ReadFrameAsync(cancellationToken));
                var chunk = QuickShareFrames.ParsePayloadChunk(frame);
                if (chunk != null) return chunk;
                if (QuickShareFrames.ReadOfflineFrameType(frame) == QuickShareFrames.NearbyKeepAlive) continue;
                throw new ShareProtocolException("Quick Share sent an unexpected encrypted control frame.");
            }
        }

        private static async Task KeepAliveLoopAsync(SocketConnection connection, QuickShareCrypto crypto, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                await connection.WriteFrameAsync(crypto.EncryptOffline(QuickShareFrames.BuildKeepAlive()), cancellationToken);
            }
        }

        private static void ValidateMetadata(IList<QuickShareFileMetadata> metadata)
        {
            if (metadata == null || metadata.Count == 0 || metadata.Count > 128)
                throw new ShareProtocolException("Quick Share sent an invalid file list.");
            var payloadIds = new HashSet<long>();
            foreach (var item in metadata)
            {
                if (item == null || item.Size < 0 || !payloadIds.Add(item.PayloadId))
                    throw new ShareProtocolException("Quick Share sent invalid file metadata.");
                item.Name = ProtocolUtilities.NormalizeFileName(item.Name);
            }
        }

        private static byte[] ParseServerPublicKey(byte[] serverInitBody)
        {
            var reader = new ProtoReader(serverInitBody);
            while (!reader.End)
            {
                var tag = reader.ReadTag();
                if ((tag >> 3) == 4 && (tag & 7) == 2) return reader.ReadBytes();
                reader.Skip(tag & 7);
            }
            return new byte[0];
        }

        private static byte[] RandomBytes(int count) { var value = new byte[count]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(value); return value; }
        private static byte[] Sha512(byte[] value) { using (var sha = SHA512.Create()) return sha.ComputeHash(value); }
        private static bool FixedEquals(byte[] left, byte[] right) { if (left == null || right == null || left.Length != right.Length) return false; var result = 0; for (var i = 0; i < left.Length; i++) result |= left[i] ^ right[i]; return result == 0; }

        private sealed class IncomingFile
        {
            internal QuickShareFileMetadata Metadata { get; set; }
            internal StorageFile Temporary { get; set; }
            internal IRandomAccessStream Stream { get; set; }
            internal DataWriter Writer { get; set; }
            internal long Offset { get; set; }
            internal bool Completed { get; set; }
        }
    }
}
