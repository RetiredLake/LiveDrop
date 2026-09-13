using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveDrop.Models;
using LiveDrop.Protocols;
using LiveDrop.Services;
using LiveDrop.Transports;
using LiveDrop.Protocols.QuickShare;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LiveDrop.Protocols.Nearby
{
    internal sealed class CdpServerSession
    {
        private const byte ConnectionModeProximal = 1;
        private const byte ConnectRequest = 0;
        private const byte ConnectResponse = 1;
        private const byte DeviceAuthRequest = 2;
        private const byte DeviceAuthResponse = 3;
        private const byte UserDeviceAuthRequest = 4;
        private const byte UserDeviceAuthResponse = 5;
        private const byte AuthDoneRequest = 6;
        private const byte AuthDoneResponse = 7;
        private const uint MessageFragmentSize = 16384;
        private const uint BlobSize = 102400;

        private readonly SocketConnection _connection;
        private readonly CdpIdentity _identity;
        private readonly P256KeyAgreement _key = P256KeyAgreement.Create();
        private ulong _hostNonce;
        private ulong _clientNonce;
        private ulong _sessionId;
        private CdpCrypto _crypto;
        private uint _sequence;
        private ulong _nextChannelId;

        private CdpServerSession(SocketConnection connection, CdpIdentity identity)
        {
            _connection = connection;
            _identity = identity;
        }

        internal static async Task ReceiveAsync(SocketConnection connection, CdpIdentity identity, PeerDescriptor peer, Func<PeerDescriptor, ShareOffer, Task<bool>> consent, Action<string> status, CancellationToken cancellationToken)
        {
            var session = new CdpServerSession(connection, identity);
            await session.AuthenticateAsync(cancellationToken);
            await session.ReceiveFilesAsync(peer, consent, status, cancellationToken);
        }

        private async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            var requestPacket = await CdpConnection.ReadAsync(_connection, cancellationToken);
            var requestPayload = new MemoryStream(ReadConnectionPayload(requestPacket, ConnectRequest));
            var curve = requestPayload.ReadByte();
            var hmacSize = ReadUInt16(requestPayload);
            _clientNonce = ReadUInt64(requestPayload);
            var fragmentSize = ReadUInt32(requestPayload);
            var clientX = ReadBytesWithLength(requestPayload);
            var clientY = ReadBytesWithLength(requestPayload);
            if (curve != 0 || hmacSize != 32 || fragmentSize == 0 || clientX.Length != 32 || clientY.Length != 32)
                throw new ShareProtocolException("Microsoft Nearby sent invalid CDP connection parameters.");

            _hostNonce = RandomUInt64();
            _sessionId = ((ulong)(RandomUInt32() & 0x7FFFFFFF) << 32) | (requestPacket.Header.SessionId & 0xFFFFFFFFUL) | 0x80000000UL;
            var coordinates = _key.ExportPublicCoordinates();
            var responseHeader = CopyHeader(requestPacket.Header);
            responseHeader.Type = CdpHeader.Connect;
            responseHeader.Flags = 0;
            responseHeader.SequenceNumber = 0;
            responseHeader.RequestId = 0;
            responseHeader.FragmentIndex = 0;
            responseHeader.FragmentCount = 1;
            responseHeader.SessionId = _sessionId;
            responseHeader.ChannelId = 0;
            var response = new MemoryStream();
            WriteUInt16(response, ConnectionModeProximal);
            response.WriteByte(ConnectResponse);
            response.WriteByte(1); // Pending; authentication follows.
            WriteUInt16(response, 32);
            WriteUInt64(response, _hostNonce);
            WriteUInt32(response, MessageFragmentSize);
            WriteBytesWithLength(response, coordinates[0]);
            WriteBytesWithLength(response, coordinates[1]);
            await CdpConnection.WriteAsync(_connection, responseHeader, response.ToArray(), cancellationToken);

            _crypto = new CdpCrypto(_key.ComputeCdpSharedSecret(clientX, clientY));
            await ReceiveAuthenticationAsync(DeviceAuthRequest, DeviceAuthResponse, cancellationToken);
            await ReceiveAuthenticationAsync(UserDeviceAuthRequest, UserDeviceAuthResponse, cancellationToken);

            var donePacket = await CdpConnection.ReadAsync(_connection, cancellationToken);
            var done = ReadConnectionPayload(donePacket, AuthDoneRequest);
            if (done.Length != 1 || done[0] != 0) throw new ShareProtocolException("Microsoft Nearby sent an invalid CDP authentication completion.");
            await SendPlainConnectAsync(donePacket.Header, AuthDoneResponse, new byte[] { 0 }, cancellationToken);
        }

        private async Task ReceiveAuthenticationAsync(byte expectedRequest, byte responseType, CancellationToken cancellationToken)
        {
            var packet = await CdpConnection.ReadAsync(_connection, cancellationToken);
            var payload = new MemoryStream(ReadConnectionPayload(packet, expectedRequest));
            var certificate = ReadBytesWithLength(payload);
            var signature = ReadBytesWithLength(payload);
            if (!CdpIdentity.VerifyAuthentication(certificate, signature, _hostNonce, _clientNonce))
                throw new ShareProtocolException("Microsoft Nearby sent an invalid CDP certificate signature.");
            var responsePayload = new MemoryStream();
            WriteBytesWithLength(responsePayload, _identity.Certificate);
            WriteBytesWithLength(responsePayload, _identity.SignAuthentication(_hostNonce, _clientNonce));
            await SendPlainConnectAsync(packet.Header, responseType, responsePayload.ToArray(), cancellationToken);
        }

        private async Task ReceiveFilesAsync(PeerDescriptor peer, Func<PeerDescriptor, ShareOffer, Task<bool>> consent, Action<string> status, CancellationToken cancellationToken)
        {
            var handshakeChannel = await AcceptChannelAsync(cancellationToken);
            var handshake = await ReadValueSetAsync(handshakeChannel, cancellationToken);
            if (!handshake.Values.Values.ContainsKey("ControlMessage") || handshake.Values.GetUInt32("ControlMessage") != 6)
                throw new ShareProtocolException("Microsoft Nearby sent an invalid platform handshake.");
            await SendValueSetAsync(handshakeChannel, handshake.MessageId, new CdpValueSet().AddUInt32("SelectedPlatformVersion", 1).AddUInt32("VersionHandShakeResult", 1), cancellationToken);

            var transferChannel = await AcceptChannelAsync(cancellationToken);
            var transfer = await ReadValueSetAsync(transferChannel, cancellationToken);
            if (!transfer.Values.Values.ContainsKey("ControlMessage") || transfer.Values.GetUInt32("ControlMessage") != 1)
                throw new ShareProtocolException("Microsoft Nearby sent an invalid transfer request.");
            var fileNames = transfer.Values.GetStringArray("FileNames");
            var contentIds = transfer.Values.GetUInt32Array("ContentIds");
            var contentSizes = transfer.Values.GetUInt64Array("ContentSizes");
            var fileCount = transfer.Values.GetUInt32("FileCount");
            if (fileCount == 0 || fileCount > 128 || fileNames.Count != fileCount || contentIds.Count != fileCount || contentSizes.Count != fileCount)
                throw new ShareProtocolException("Microsoft Nearby sent invalid file metadata.");

            var descriptors = new List<ShareFileDescriptor>();
            for (var i = 0; i < (int)fileCount; i++)
            {
                if (contentIds[i] != (uint)i) throw new ShareProtocolException("Microsoft Nearby sent invalid content identifiers.");
                descriptors.Add(new ShareFileDescriptor(ProtocolUtilities.NormalizeFileName(fileNames[i]), "application/octet-stream", checked((long)contentSizes[i])));
            }
            var offer = new ShareOffer(descriptors, string.Empty);
            var accepted = consent != null && await consent(peer, offer);
            if (!accepted)
            {
                await SendValueSetAsync(transferChannel, transfer.MessageId, new CdpValueSet().AddUInt32("ControlMessage", 5), cancellationToken);
                return;
            }

            var received = new List<IncomingFile>();
            try
            {
                var folder = await TransferFileStore.GetReceiveFolderAsync(null);
                for (var i = 0; i < descriptors.Count; i++)
                {
                    var temporary = await folder.CreateFileAsync("." + ProtocolUtilities.NormalizeFileName(descriptors[i].Name) + ".livedrop-part", CreationCollisionOption.GenerateUniqueName);
                    var stream = await temporary.OpenAsync(FileAccessMode.ReadWrite);
                    received.Add(new IncomingFile { Metadata = descriptors[i], Temporary = temporary, Stream = stream, Writer = new DataWriter(stream) });
                }

                for (var i = 0; i < received.Count; i++)
                {
                    var file = received[i];
                    ulong position = 0;
                    while (position < (ulong)file.Metadata.Size || file.Metadata.Size == 0 && position == 0)
                    {
                        var requested = (uint)Math.Min(BlobSize, (ulong)file.Metadata.Size - position);
                        await SendValueSetAsync(transferChannel, transfer.MessageId, new CdpValueSet().AddUInt32("ControlMessage", 3).AddUInt64("BlobPosition", position).AddUInt32("BlobSize", requested).AddUInt32("ContentId", (uint)i), cancellationToken);
                        var response = await ReadValueSetAsync(transferChannel, cancellationToken);
                        if (!response.Values.Values.ContainsKey("ControlMessage") || response.Values.GetUInt32("ControlMessage") != 4)
                            throw new ShareProtocolException("Microsoft Nearby sent an unexpected file response.");
                        if (response.Values.GetUInt32("ContentId") != (uint)i || response.Values.GetUInt64("BlobPosition") != position)
                            throw new ShareProtocolException("Microsoft Nearby sent a file response for the wrong range.");
                        var body = response.Values.GetBytes("DataBlob");
                        if (body.Count != requested || position + (ulong)body.Count > (ulong)file.Metadata.Size)
                            throw new ShareProtocolException("Microsoft Nearby sent an invalid file range.");
                        if (body.Count > 0)
                        {
                            file.Writer.WriteBytes(body.ToArray());
                            await file.Writer.StoreAsync();
                        }
                        position += (ulong)body.Count;
                        status?.Invoke("Received " + file.Metadata.Name + " (" + position + "/" + file.Metadata.Size + " bytes).");
                        if (file.Metadata.Size == 0) break;
                    }
                    await file.Writer.FlushAsync();
                    file.Writer.DetachStream();
                    file.Writer.Dispose();
                    file.Stream.Dispose();
                    await file.Temporary.RenameAsync(ProtocolUtilities.NormalizeFileName(file.Metadata.Name), NameCollisionOption.GenerateUniqueName);
                    file.Completed = true;
                }
                await SendValueSetAsync(transferChannel, transfer.MessageId, new CdpValueSet().AddUInt32("ControlMessage", 2), cancellationToken);
                status?.Invoke("Microsoft Nearby transfer received. Files are saved in Downloads/LiveDrop.");
            }
            catch
            {
                foreach (var file in received)
                {
                    try { file.Writer?.Dispose(); } catch { }
                    try { file.Stream?.Dispose(); } catch { }
                    if (!file.Completed && file.Temporary != null) { try { await file.Temporary.DeleteAsync(); } catch { } }
                }
                throw;
            }
        }

        private async Task<ulong> AcceptChannelAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                var packet = await CdpConnection.ReadAsync(_connection, cancellationToken);
                if (packet.Header.Type != CdpHeader.Control) continue;
                var plain = _crypto.Decrypt(packet);
                if (plain.Length < 1 || plain[0] != 0) continue;
                var payload = new MemoryStream(plain, 1, plain.Length - 1, false);
                var appId = ReadUtf8WithLength(payload);
                var appName = ReadUtf8WithLength(payload);
                ReadUInt16(payload);
                ReadUtf8WithLength(payload);
                var channelId = ++_nextChannelId;
                var responseHeader = NewControlHeader(packet.Header, packet.Header.RequestId);
                responseHeader.AdditionalHeaders.Add(new CdpAdditionalHeader(1, UInt64Little(packet.Header.RequestId)));
                responseHeader.AdditionalHeaders.Add(new CdpAdditionalHeader(0x81, new byte[] { 0x30, 0, 0, 1 }));
                await WriteEncryptedAsync(responseHeader, new byte[] { 1, 0 }.Concat(UInt64Bytes(channelId)).ToArray(), cancellationToken);
                if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(appName)) throw new ShareProtocolException("Microsoft Nearby sent an invalid channel request.");
                return channelId;
            }
        }

        private async Task SendValueSetAsync(ulong channelId, uint messageId, CdpValueSet valueSet, CancellationToken cancellationToken)
        {
            var body = valueSet.Serialize();
            var payload = new byte[12 + body.Length];
            WriteUInt32(payload, 0, 1); WriteUInt32(payload, 4, 0); WriteUInt32(payload, 8, messageId);
            System.Buffer.BlockCopy(body, 0, payload, 12, body.Length);
            var count = (ushort)Math.Max(1, (payload.Length + 16383) / 16384);
            var sequence = ++_sequence;
            for (ushort index = 0; index < count; index++)
            {
                var start = index * 16384;
                var length = Math.Min(16384, payload.Length - start);
                var fragment = new byte[length];
                System.Buffer.BlockCopy(payload, start, fragment, 0, length);
                await WriteEncryptedAsync(NewSessionHeader(channelId, sequence, index, count), fragment, cancellationToken);
            }
        }

        private async Task<CdpSessionValueSet> ReadValueSetAsync(ulong channelId, CancellationToken cancellationToken)
        {
            var first = await ReadSessionPacketAsync(channelId, cancellationToken);
            if (first.Header.FragmentIndex != 0) throw new ShareProtocolException("Microsoft Nearby sent an invalid NearShare session fragment.");
            var all = new MemoryStream();
            all.Write(first.Payload, 0, first.Payload.Length);
            for (var index = 1; index < first.Header.FragmentCount; index++)
            {
                var next = await ReadSessionPacketAsync(channelId, cancellationToken);
                if (next.Header.SequenceNumber != first.Header.SequenceNumber || next.Header.FragmentIndex != index)
                    throw new ShareProtocolException("Microsoft Nearby sent out-of-order CDP fragments.");
                all.Write(next.Payload, 0, next.Payload.Length);
            }
            var data = all.ToArray();
            if (data.Length < 12) throw new ShareProtocolException("Microsoft Nearby sent a truncated NearShare message.");
            var valueSet = new byte[data.Length - 12];
            System.Buffer.BlockCopy(data, 12, valueSet, 0, valueSet.Length);
            return new CdpSessionValueSet(ReadUInt32(data, 8), CdpValueSet.Parse(valueSet));
        }

        private async Task<CdpPacket> ReadSessionPacketAsync(ulong channelId, CancellationToken cancellationToken)
        {
            while (true)
            {
                var packet = await CdpConnection.ReadAsync(_connection, cancellationToken);
                if (packet.Header.Type != CdpHeader.Session || packet.Header.ChannelId != channelId) continue;
                return new CdpPacket(packet.Header, _crypto.Decrypt(packet));
            }
        }

        private async Task WriteEncryptedAsync(CdpHeader header, byte[] payload, CancellationToken cancellationToken)
        {
            await _connection.WriteRawAsync(_crypto.Encrypt(header, payload), cancellationToken);
        }

        private CdpHeader NewSessionHeader(ulong channelId, uint sequence, ushort index, ushort count)
        {
            var header = new CdpHeader { Type = CdpHeader.Session, SessionId = _sessionId, ChannelId = channelId, SequenceNumber = sequence, FragmentIndex = index, FragmentCount = count };
            header.AdditionalHeaders.Add(new CdpAdditionalHeader(2, Encoding.ASCII.GetBytes("A.1")));
            return header;
        }

        private CdpHeader NewControlHeader(CdpHeader request, ulong requestId)
        {
            var header = CopyHeader(request);
            header.Type = CdpHeader.Control;
            header.Flags = 0;
            header.SequenceNumber = ++_sequence;
            header.RequestId = 0;
            header.FragmentIndex = 0;
            header.FragmentCount = 1;
            header.SessionId = _sessionId;
            header.ChannelId = 0;
            header.AdditionalHeaders.Clear();
            return header;
        }

        private async Task SendPlainConnectAsync(CdpHeader request, byte messageType, byte[] payload, CancellationToken cancellationToken)
        {
            var header = CopyHeader(request);
            header.Type = CdpHeader.Connect; header.Flags = 0; header.RequestId = 0; header.FragmentIndex = 0; header.FragmentCount = 1; header.SessionId = _sessionId; header.ChannelId = 0;
            var body = new MemoryStream(); WriteUInt16(body, ConnectionModeProximal); body.WriteByte(messageType); body.Write(payload, 0, payload.Length);
            await CdpConnection.WriteAsync(_connection, header, body.ToArray(), cancellationToken);
        }

        private static byte[] ReadConnectionPayload(CdpPacket packet, byte expectedType)
        {
            if (packet.Header.Type != CdpHeader.Connect) throw new ShareProtocolException("Microsoft Nearby sent a non-connection CDP message.");
            if ((packet.Header.Flags & CdpHeader.SessionEncrypted) != 0) throw new ShareProtocolException("Microsoft Nearby encrypted a CDP authentication message too early.");
            if (packet.Payload.Length < 3) throw new ShareProtocolException("Microsoft Nearby sent a truncated CDP connection message.");
            var mode = (ushort)((packet.Payload[0] << 8) | packet.Payload[1]);
            if (mode != ConnectionModeProximal || packet.Payload[2] != expectedType) throw new ShareProtocolException("Microsoft Nearby sent an unexpected CDP connection message.");
            var result = new byte[packet.Payload.Length - 3];
            System.Buffer.BlockCopy(packet.Payload, 3, result, 0, result.Length);
            return result;
        }

        private static CdpHeader CopyHeader(CdpHeader source)
        {
            var result = new CdpHeader { Type = source.Type, Flags = source.Flags, SequenceNumber = source.SequenceNumber, RequestId = source.RequestId, FragmentIndex = source.FragmentIndex, FragmentCount = source.FragmentCount, SessionId = source.SessionId, ChannelId = source.ChannelId };
            foreach (var header in source.AdditionalHeaders) result.AdditionalHeaders.Add(new CdpAdditionalHeader(header.Type, header.Value));
            return result;
        }

        private static byte[] UInt64Bytes(ulong value) { var result = new byte[8]; for (var i = 7; i >= 0; i--) result[7 - i] = (byte)(value >> (i * 8)); return result; }
        private static byte[] UInt64Little(ulong value) { var result = new byte[8]; for (var i = 0; i < 8; i++) result[i] = (byte)(value >> (i * 8)); return result; }
        private static byte[] ReadBytesWithLength(Stream stream) { var length = ReadUInt16(stream); var result = new byte[length]; ReadExactly(stream, result); return result; }
        private static string ReadUtf8WithLength(Stream stream) { return Encoding.UTF8.GetString(ReadBytesWithLength(stream)); }
        private static ulong ReadUInt64(Stream stream) { ulong result = 0; for (var i = 0; i < 8; i++) { var value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException(); result = (result << 8) | (byte)value; } return result; }
        private static uint ReadUInt32(Stream stream) { return ((uint)ReadUInt16(stream) << 16) | ReadUInt16(stream); }
        private static ushort ReadUInt16(Stream stream) { var first = stream.ReadByte(); var second = stream.ReadByte(); if (first < 0 || second < 0) throw new EndOfStreamException(); return (ushort)((first << 8) | second); }
        private static void ReadExactly(Stream stream, byte[] target) { var offset = 0; while (offset < target.Length) { var read = stream.Read(target, offset, target.Length - offset); if (read == 0) throw new EndOfStreamException(); offset += read; } }
        private static void WriteBytesWithLength(Stream stream, byte[] value) { WriteUInt16(stream, checked((ushort)value.Length)); stream.Write(value, 0, value.Length); }
        private static void WriteUInt16(Stream stream, ushort value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void WriteUInt32(Stream stream, uint value) { WriteUInt16(stream, (ushort)(value >> 16)); WriteUInt16(stream, (ushort)value); }
        private static void WriteUInt64(Stream stream, ulong value) { for (var i = 7; i >= 0; i--) stream.WriteByte((byte)(value >> (8 * i))); }
        private static void WriteUInt32(byte[] target, int offset, uint value) { target[offset] = (byte)(value >> 24); target[offset + 1] = (byte)(value >> 16); target[offset + 2] = (byte)(value >> 8); target[offset + 3] = (byte)value; }
        private static uint ReadUInt32(byte[] value, int offset) { return ((uint)value[offset] << 24) | ((uint)value[offset + 1] << 16) | ((uint)value[offset + 2] << 8) | value[offset + 3]; }
        private static ulong RandomUInt64() { var bytes = new byte[8]; using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes); return ReadUInt64(new MemoryStream(bytes)); }
        private static uint RandomUInt32() { return (uint)RandomUInt64(); }

        private sealed class CdpSessionValueSet
        {
            internal uint MessageId { get; private set; }
            internal CdpValueSet Values { get; private set; }
            internal CdpSessionValueSet(uint messageId, CdpValueSet values) { MessageId = messageId; Values = values; }
        }

        private sealed class IncomingFile
        {
            internal ShareFileDescriptor Metadata { get; set; }
            internal StorageFile Temporary { get; set; }
            internal IRandomAccessStream Stream { get; set; }
            internal DataWriter Writer { get; set; }
            internal bool Completed { get; set; }
        }
    }
}
