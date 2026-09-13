using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using LiveDrop.Models;
using LiveDrop.Protocols;
using LiveDrop.Services;
using LiveDrop.Transports;

namespace LiveDrop.Protocols.QuickShare
{
    internal static class QuickShareSession
    {
        private const int MaxControlPayloadSize = 64 * 1024 * 1024;
        private static long _payloadSequence = DateTime.UtcNow.Ticks;

        internal static Task SendAsync(SocketConnection connection, string displayName, string endpointId, byte[] endpointInfo, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            return SendAsync(connection, displayName, endpointId, endpointInfo, offer, progress, cancellationToken, null);
        }

        internal static async Task SendAsync(SocketConnection connection, string displayName, string endpointId, byte[] endpointInfo, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken, Action<string> status)
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
            status?.Invoke("Quick Share security PIN: " + crypto.PinCode(authKey) + ". Compare it with the other device.");
            using (var keepAliveSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                Task keepAlive = null;
                try
                {
                    await connection.WriteFrameAsync(clientFinish, cancellationToken);
                    await connection.WriteFrameAsync(QuickShareFrames.BuildConnectionAccept(), cancellationToken);
                    // Both peers send the plaintext connection response before
                    // reading the response from the other side. Waiting to read
                    // first deadlocks two LiveDrop instances and can make either
                    // peer close the socket before the introduction is sent.
                    var peerConnectionResponse = QuickShareFrames.ParseConnectionResponse(await connection.ReadFrameAsync(cancellationToken));
                    if (!peerConnectionResponse.Accepted) throw new ShareProtocolException("Quick Share rejected the connection.");
                    keepAlive = KeepAliveLoopAsync(connection, crypto, keepAliveSource.Token);

                    await ReadSharingFrameAsync(connection, crypto, QuickShareFrames.SharingPairedKeyEncryption, cancellationToken);
                    await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildPairedKeyEncryption(), cancellationToken);
                    await ReadSharingFrameAsync(connection, crypto, QuickShareFrames.SharingPairedKeyResult, cancellationToken);
                    await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildPairedKeyResult(), cancellationToken);
                    var payloadIds = CreatePayloadIds(offer.Files.Count);
                    await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildIntroduction(offer.Files, payloadIds), cancellationToken);
                    if (!QuickShareFrames.IsAcceptedSharingResponse(await ReadSharingFrameAsync(connection, crypto, QuickShareFrames.SharingResponse, cancellationToken))) throw new ShareProtocolException("Quick Share recipient rejected the transfer.");

                    for (var fileIndex = 0; fileIndex < offer.Files.Count; fileIndex++)
                    {
                        await SendFileAsync(connection, crypto, offer.Files[fileIndex], payloadIds[fileIndex], progress, cancellationToken);
                    }
                    if (peerConnectionResponse.SafeToDisconnectVersion >= 1)
                    {
                        await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildDisconnection(true, false), cancellationToken);
                        await AwaitSafeDisconnectAcknowledgementAsync(connection, crypto, cancellationToken);
                    }
                }
                finally
                {
                    keepAliveSource.Cancel();
                    if (keepAlive != null) { try { await keepAlive; } catch { } }
                }
            }
        }

        internal static async Task ReceiveAsync(StreamSocket socket, string displayName, Func<PeerDescriptor, ShareOffer, Task<bool>> consent, Action<string> status, Action<ShareProgress> progress, CancellationToken cancellationToken)
        {
            await ReceiveAsync(socket, displayName, consent, status, progress, cancellationToken, null);
        }

        // The receive-folder overload is used only by the protocol loopback
        // fixture. The app's adapters use the normal Downloads-backed overload
        // above, so a local test socket cannot become a user-facing route.
        internal static async Task ReceiveAsync(StreamSocket socket, string displayName, Func<PeerDescriptor, ShareOffer, Task<bool>> consent, Action<string> status, Action<ShareProgress> progress, CancellationToken cancellationToken, StorageFolder receiveFolder)
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
                status?.Invoke("Quick Share security PIN: " + crypto.PinCode(authKey) + ". Compare it with the other device.");
                using (var keepAliveSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    Task keepAlive = null;
                    try
                    {
                        await connection.WriteFrameAsync(QuickShareFrames.BuildConnectionAccept(), cancellationToken);
                        if (!QuickShareFrames.IsAcceptedConnection(await connection.ReadFrameAsync(cancellationToken))) throw new ShareProtocolException("Quick Share client rejected the connection.");
                        keepAlive = KeepAliveLoopAsync(connection, crypto, keepAliveSource.Token);
                        await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildPairedKeyEncryption(), cancellationToken);
                        await ReadSharingFrameAsync(connection, crypto, QuickShareFrames.SharingPairedKeyEncryption, cancellationToken);
                        await SendSharingFrameAsync(connection, crypto, QuickShareFrames.BuildPairedKeyResult(), cancellationToken);
                        await ReadSharingFrameAsync(connection, crypto, QuickShareFrames.SharingPairedKeyResult, cancellationToken);
                        var introduction = await ReadSharingFrameAsync(connection, crypto, QuickShareFrames.SharingIntroduction, cancellationToken);
                        var metadata = QuickShareFrames.ParseIntroduction(introduction);
                        ValidateMetadata(metadata);
                        var files = new List<ShareFileDescriptor>();
                        foreach (var item in metadata) files.Add(new ShareFileDescriptor(item.Name, item.MimeType, item.Size));
                        var offer = new ShareOffer(files, string.Empty);
                        var accepted = consent == null || await consent(peer, offer);
                        await SendSharingFrameAsync(connection, crypto, accepted ? QuickShareFrames.BuildAcceptTransfer() : QuickShareFrames.BuildRejectTransfer(), cancellationToken);
                        if (!accepted) return;
                        status?.Invoke("Quick Share security PIN: " + crypto.PinCode(authKey) + ". Compare it with the other device. Receiving files to Pictures/LiveDrop.");
                        await ReceiveFilesAsync(connection, crypto, metadata, status, progress, cancellationToken, receiveFolder);
                        await CompleteIncomingSessionAsync(connection, crypto, cancellationToken);
                        status?.Invoke("Quick Share transfer received. Files are saved in Pictures/LiveDrop.");
                    }
                    finally
                    {
                        keepAliveSource.Cancel();
                        if (keepAlive != null) { try { await keepAlive; } catch { } }
                    }
                }
            }
        }

        private static async Task SendFileAsync(SocketConnection connection, QuickShareCrypto crypto, ShareFileDescriptor file, long payloadId, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            if (file.SourceFile == null) throw new ShareProtocolException("The outgoing file has no local source.");
            using (var fileStream = await file.SourceFile.OpenReadAsync())
            using (var input = fileStream.GetInputStreamAt(0))
            using (var reader = new DataReader(input))
            {
                reader.InputStreamOptions = InputStreamOptions.Partial;
                if ((long)fileStream.Size != file.Size)
                    throw new ShareProtocolException("The outgoing file changed before Quick Share could send it.");
                long offset = 0;
                progress?.Report(new ShareProgress(file.Name, 0, file.Size));
                while (offset < file.Size)
                {
                    var remaining = file.Size - offset;
                    var requested = (uint)Math.Min(512 * 1024, remaining);
                    var count = await reader.LoadAsync(requested);
                    if (count == 0) throw new EndOfStreamException("The outgoing file ended early.");
                    var body = new byte[count]; reader.ReadBytes(body);
                    await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildFileChunk(payloadId, file.Size, offset, body, false, file.Name), cancellationToken);
                    offset += count;
                    progress?.Report(new ShareProgress(file.Name, offset, file.Size));
                }
                await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildFileChunk(payloadId, file.Size, offset, new byte[0], true, file.Name), cancellationToken);
            }
        }

        private static async Task ReceiveFilesAsync(SocketConnection connection, QuickShareCrypto crypto, IList<QuickShareFileMetadata> metadata, Action<string> status, Action<ShareProgress> progress, CancellationToken cancellationToken, StorageFolder receiveFolder)
        {
            var writers = new Dictionary<long, IncomingFile>();
            var bytePayloads = new Dictionary<long, MemoryStream>();
            var bytePayloadSizes = new Dictionary<long, long>();
            try
            {
                var folder = receiveFolder ?? await TransferFileStore.GetReceiveFolderAsync(null);
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
                    if (chunk.PacketType != 1) continue;
                    if (chunk.PayloadType == 1)
                    {
                        var controlBody = AppendBytePayloadChunk(bytePayloads, bytePayloadSizes, chunk);
                        if (controlBody != null && QuickShareFrames.IsCancelSharingFrame(controlBody))
                            throw new ShareProtocolException("Quick Share sender cancelled the transfer.");
                        // Quick Share uses BYTES for progress and other
                        // sharing-layer control frames. They may be sent
                        // before or between FILE chunks and are not files.
                        continue;
                    }
                    var file = ResolveIncomingFile(writers, chunk);
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
                    status?.Invoke("Received " + file.Metadata.Name + " (" + completed + "/" + metadata.Count + "). Saved in Pictures/LiveDrop.");
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
            finally
            {
                foreach (var payload in bytePayloads.Values) payload.Dispose();
            }
        }

        private static byte[] AppendBytePayloadChunk(Dictionary<long, MemoryStream> bytePayloads, Dictionary<long, long> bytePayloadSizes, QuickSharePayloadChunk chunk)
        {
            if (chunk == null) throw new ShareProtocolException("Quick Share sent an empty control payload.");
            if (chunk.TotalSize < 0 || chunk.TotalSize > MaxControlPayloadSize)
                throw new ShareProtocolException("Quick Share sent an oversized control payload.");
            if (chunk.Offset < 0 || chunk.Offset > MaxControlPayloadSize)
                throw new ShareProtocolException("Quick Share sent a control payload with an invalid offset.");

            MemoryStream buffer;
            if (!bytePayloads.TryGetValue(chunk.PayloadId, out buffer))
            {
                buffer = new MemoryStream();
                bytePayloads[chunk.PayloadId] = buffer;
                bytePayloadSizes[chunk.PayloadId] = chunk.TotalSize;
            }

            var expectedSize = bytePayloadSizes[chunk.PayloadId];
            if (expectedSize > 0 && chunk.TotalSize > 0 && chunk.TotalSize != expectedSize)
                throw new ShareProtocolException("Quick Share changed a control payload size mid-transfer.");
            if (expectedSize == 0 && chunk.TotalSize > 0)
            {
                expectedSize = chunk.TotalSize;
                bytePayloadSizes[chunk.PayloadId] = expectedSize;
            }
            if (chunk.Offset != buffer.Length)
                throw new ShareProtocolException("Quick Share sent a control payload at the wrong offset.");

            var body = chunk.Body ?? new byte[0];
            if (body.Length > MaxControlPayloadSize - buffer.Length)
                throw new ShareProtocolException("Quick Share sent an oversized control payload.");
            if (expectedSize > 0 && body.Length > expectedSize - buffer.Length)
                throw new ShareProtocolException("Quick Share control payload exceeded its declared size.");
            if (body.Length > 0) buffer.Write(body, 0, body.Length);
            if (!chunk.Last) return null;
            if (expectedSize > 0 && buffer.Length != expectedSize)
                throw new ShareProtocolException("Quick Share control payload size did not match its header.");

            var result = buffer.ToArray();
            bytePayloads.Remove(chunk.PayloadId);
            bytePayloadSizes.Remove(chunk.PayloadId);
            buffer.Dispose();
            return result;
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

        private static IncomingFile ResolveIncomingFile(Dictionary<long, IncomingFile> writers, QuickSharePayloadChunk chunk)
        {
            if (chunk == null) throw new ShareProtocolException("Quick Share sent an empty payload.");
            if (chunk.PayloadType != 2) throw new ShareProtocolException("Quick Share sent payload type " + chunk.PayloadType + "; expected a file payload.");

            IncomingFile file;
            if (writers.TryGetValue(chunk.PayloadId, out file) && !file.Completed) return file;

            // FileMetadata has both payload_id and id in current Quick Share
            // implementations. They identify the same attachment, although
            // some clients have used either value on the payload header.
            IncomingFile candidate = null;
            var matches = 0;
            foreach (var item in writers.Values)
            {
                if (item.Completed) continue;
                var idMatches = item.Metadata.AttachmentId != 0 && item.Metadata.AttachmentId == chunk.PayloadId;
                var nameMatches = !string.IsNullOrEmpty(chunk.FileName) &&
                    string.Equals(ProtocolUtilities.NormalizeFileName(chunk.FileName), item.Metadata.Name, StringComparison.OrdinalIgnoreCase);
                var sizeMatches = chunk.TotalSize == item.Metadata.Size;
                if (idMatches || nameMatches || sizeMatches)
                {
                    candidate = item;
                    matches++;
                }
            }
            if (matches == 1) return candidate;

            var expected = new StringBuilder();
            foreach (var item in writers.Values)
            {
                if (item.Completed) continue;
                if (expected.Length > 0) expected.Append(", ");
                expected.Append(item.Metadata.PayloadId);
                if (item.Metadata.AttachmentId != 0 && item.Metadata.AttachmentId != item.Metadata.PayloadId)
                {
                    expected.Append("/").Append(item.Metadata.AttachmentId);
                }
            }
            throw new ShareProtocolException("Quick Share sent an unknown file payload (id=" + chunk.PayloadId + ", type=" + chunk.PayloadType + ", size=" + chunk.TotalSize + ", name=" + (chunk.FileName ?? "") + "; expected " + expected + ").");
        }

        private static async Task SendSharingFrameAsync(SocketConnection connection, QuickShareCrypto crypto, byte[] frame, CancellationToken cancellationToken)
        {
            var id = Interlocked.Increment(ref _payloadSequence);
            var body = frame ?? new byte[0];
            // The final marker continues the same payload: its offset and
            // total size remain the size of the control message. Sending 0/0
            // here makes the receiver reject the marker after buffering the
            // first chunk, before the introduction or acceptance response.
            await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildPayloadChunk(id, 1, body.Length, 0, body, false), cancellationToken);
            await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildPayloadChunk(id, 1, body.Length, body.Length, new byte[0], true), cancellationToken);
        }

        private static async Task<byte[]> ReadSharingFrameAsync(SocketConnection connection, QuickShareCrypto crypto, int expectedType, CancellationToken cancellationToken)
        {
            var bytePayloads = new Dictionary<long, MemoryStream>();
            var bytePayloadSizes = new Dictionary<long, long>();
            try
            {
                while (true)
                {
                    var chunk = await ReadPayloadChunkAsync(connection, crypto, cancellationToken);
                    if (chunk.PayloadType != 1) continue;
                    if (chunk.PacketType != 1) continue;
                    var body = AppendBytePayloadChunk(bytePayloads, bytePayloadSizes, chunk);
                    if (body == null) continue;
                    int type;
                    try { type = QuickShareFrames.ReadOfflineFrameType(body); }
                    catch (Exception ex) { throw new ShareProtocolException("Quick Share sent an invalid sharing control frame.", ex); }
                    if (type == expectedType) return body;
                    if (type == QuickShareFrames.SharingProgressUpdate) continue;
                    if (type == QuickShareFrames.SharingCancel) throw new ShareProtocolException("Quick Share sender cancelled the transfer.");
                    throw new ShareProtocolException("Quick Share sent sharing frame type " + type + "; expected " + expectedType + ".");
                }
            }
            finally
            {
                foreach (var payload in bytePayloads.Values) payload.Dispose();
            }
        }

        private static async Task CompleteIncomingSessionAsync(SocketConnection connection, QuickShareCrypto crypto, CancellationToken cancellationToken)
        {
            // The final file marker only completes the payload. Keep the
            // socket alive long enough to consume the sender's terminal
            // disconnection frame, otherwise the sender can fail while
            // writing it after this receiver has already disposed the socket.
            using (var teardownSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                teardownSource.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    while (true)
                    {
                        var frame = crypto.DecryptOffline(await connection.ReadFrameAsync(teardownSource.Token));
                        if (QuickShareFrames.IsDisconnection(frame))
                        {
                            if (QuickShareFrames.IsSafeDisconnectRequest(frame))
                            {
                                await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildDisconnection(false, true), cancellationToken);
                            }
                            return;
                        }
                        var type = QuickShareFrames.ReadOfflineFrameType(frame);
                        if (type == QuickShareFrames.NearbyKeepAlive)
                        {
                            if (!QuickShareFrames.IsKeepAliveAcknowledgement(frame))
                                await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildKeepAlive(true, QuickShareFrames.ReadKeepAliveSequence(frame)), cancellationToken);
                            continue;
                        }
                        if (type == QuickShareFrames.NearbyBandwidthUpgradeNegotiation ||
                            type == QuickShareFrames.NearbyBandwidthUpgradeRetry) continue;
                    }
                }
                catch (OperationCanceledException) { }
                catch (System.IO.EndOfStreamException) { }
            }
        }

        private static async Task AwaitSafeDisconnectAcknowledgementAsync(SocketConnection connection, QuickShareCrypto crypto, CancellationToken cancellationToken)
        {
            using (var teardownSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                teardownSource.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    while (true)
                    {
                        var frame = crypto.DecryptOffline(await connection.ReadFrameAsync(teardownSource.Token));
                        if (QuickShareFrames.IsDisconnection(frame))
                        {
                            if (QuickShareFrames.IsSafeDisconnectAcknowledgement(frame)) return;
                            if (QuickShareFrames.IsSafeDisconnectRequest(frame))
                            {
                                await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildDisconnection(false, true), cancellationToken);
                                return;
                            }
                            return;
                        }
                        var type = QuickShareFrames.ReadOfflineFrameType(frame);
                        if (type == QuickShareFrames.NearbyKeepAlive)
                        {
                            if (!QuickShareFrames.IsKeepAliveAcknowledgement(frame))
                                await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildKeepAlive(true, QuickShareFrames.ReadKeepAliveSequence(frame)), cancellationToken);
                            continue;
                        }
                        if (type == QuickShareFrames.NearbyBandwidthUpgradeNegotiation ||
                            type == QuickShareFrames.NearbyBandwidthUpgradeRetry) continue;
                    }
                }
                catch (OperationCanceledException) { }
                catch (System.IO.EndOfStreamException) { }
            }
        }

        private static async Task<QuickSharePayloadChunk> ReadPayloadChunkAsync(SocketConnection connection, QuickShareCrypto crypto, CancellationToken cancellationToken)
        {
            while (true)
            {
                var frame = crypto.DecryptOffline(await connection.ReadFrameAsync(cancellationToken));
                var chunk = QuickShareFrames.ParsePayloadChunk(frame);
                if (chunk != null && chunk.PacketType == 1) return chunk;
                if (chunk != null) continue;
                var type = QuickShareFrames.ReadOfflineFrameType(frame);
                if (type == QuickShareFrames.NearbyKeepAlive)
                {
                    if (!QuickShareFrames.IsKeepAliveAcknowledgement(frame))
                        await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildKeepAlive(true, QuickShareFrames.ReadKeepAliveSequence(frame)), cancellationToken);
                    continue;
                }
                // A peer may advertise a bandwidth upgrade even though this
                // session is already using the LAN socket. It is valid control
                // traffic, not a payload, so leave the current channel alone.
                if (type == QuickShareFrames.NearbyBandwidthUpgradeNegotiation ||
                    type == QuickShareFrames.NearbyBandwidthUpgradeRetry) continue;
                if (type == QuickShareFrames.NearbyDisconnection)
                    throw new ShareProtocolException("Quick Share peer disconnected during the transfer.");
                throw new ShareProtocolException("Quick Share sent an unexpected encrypted control frame (type=" + type + ").");
            }
        }

        private static async Task KeepAliveLoopAsync(SocketConnection connection, QuickShareCrypto crypto, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                await crypto.SendOfflineAsync(connection, QuickShareFrames.BuildKeepAlive(), cancellationToken);
            }
        }

        private static List<long> CreatePayloadIds(int count)
        {
            var ids = new List<long>();
            var used = new HashSet<long>();
            while (ids.Count < count)
            {
                var id = BitConverter.ToInt64(RandomBytes(8), 0) & long.MaxValue;
                if (id != 0 && used.Add(id)) ids.Add(id);
            }
            return ids;
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
