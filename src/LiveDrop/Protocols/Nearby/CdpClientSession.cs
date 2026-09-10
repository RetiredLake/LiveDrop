using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveDrop.Models;
using LiveDrop.Protocols.QuickShare;
using LiveDrop.Transports;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LiveDrop.Protocols.Nearby
{
    internal sealed class CdpClientSession
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

        private readonly SocketConnection _connection;
        private readonly CdpIdentity _identity;
        private readonly P256KeyAgreement _key = P256KeyAgreement.Create();
        private readonly ulong _clientNonce;
        private ulong _sessionId;
        private CdpCrypto _crypto;
        private ulong _hostNonce;
        private CdpHeader _replyHeaders;
        private uint _sequence;
        private ulong _requestId;

        private CdpClientSession(SocketConnection connection, CdpIdentity identity)
        {
            _connection = connection;
            _identity = identity;
            _clientNonce = RandomUInt64();
            _sessionId = RandomUInt32() & 0x7FFFFFFFUL;
        }

        internal static async Task<CdpClientSession> ConnectAsync(SocketConnection connection, CdpIdentity identity, CancellationToken cancellationToken)
        {
            var session = new CdpClientSession(connection, identity);
            await session.AuthenticateAsync(cancellationToken);
            return session;
        }

        private async Task AuthenticateAsync(CancellationToken cancellationToken)
        {
            var coordinates = _key.ExportPublicCoordinates();
            var request = new MemoryStream();
            WriteUInt16(request, ConnectionModeProximal); request.WriteByte(ConnectRequest);
            request.WriteByte(0); WriteUInt16(request, 32); WriteUInt64(request, _clientNonce); WriteUInt32(request, MessageFragmentSize);
            WriteBytesWithLength(request, coordinates[0]); WriteBytesWithLength(request, coordinates[1]);
            var connectHeader = NewConnectHeader();
            await CdpConnection.WriteAsync(_connection, connectHeader, request.ToArray(), cancellationToken);

            var response = await CdpConnection.ReadAsync(_connection, cancellationToken);
            var responsePayload = new MemoryStream(ReadConnectionPayload(response, ConnectResponse));
            var result = responsePayload.ReadByte();
            var hmacSize = ReadUInt16(responsePayload); var responseNonce = ReadUInt64(responsePayload); var fragmentSize = ReadUInt32(responsePayload);
            if (result != 1 && result != 0) throw new ShareProtocolException("Microsoft Nearby rejected the CDP connection.");
            if (hmacSize != 32 || fragmentSize == 0) throw new ShareProtocolException("Microsoft Nearby returned invalid CDP parameters.");
            var remoteX = ReadBytesWithLength(responsePayload); var remoteY = ReadBytesWithLength(responsePayload);
            _hostNonce = responseNonce;
            _crypto = new CdpCrypto(_key.ComputeCdpSharedSecret(remoteX, remoteY));
            // The host assigns the session namespace in its response. Client
            // messages use the same value with the host bit corrected, as
            // required by MS-CDP's client/host session-id convention.
            _sessionId = response.Header.SessionId ^ 0x80000000UL;
            _replyHeaders = CopyHeader(response.Header);

            await SendAuthenticationAsync(DeviceAuthRequest, _identity.Certificate, _identity.SignAuthentication(_hostNonce, _clientNonce), cancellationToken);
            await ReceiveAuthenticationAsync(DeviceAuthResponse, cancellationToken);
            await SendAuthenticationAsync(UserDeviceAuthRequest, _identity.Certificate, _identity.SignAuthentication(_hostNonce, _clientNonce), cancellationToken);
            await ReceiveAuthenticationAsync(UserDeviceAuthResponse, cancellationToken);
            await SendConnectMessageAsync(AuthDoneRequest, new byte[0], cancellationToken, false);
            var completed = await CdpConnection.ReadAsync(_connection, cancellationToken);
            var done = new MemoryStream(ReadConnectionPayload(completed, AuthDoneResponse));
            if (done.ReadByte() != 0) throw new ShareProtocolException("Microsoft Nearby CDP authentication did not complete.");
        }

        internal async Task SendFilesAsync(ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            if (offer == null || offer.Files == null || offer.Files.Count == 0) throw new ShareProtocolException("Microsoft Nearby requires at least one file.");
            var operationId = Guid.NewGuid();
            var handshakeChannel = await StartChannelAsync("0D472C30-80B5-4722-A279-0F3B97F0DCF2", "NearSharePlatform", cancellationToken);
            await SendValueSetAsync(handshakeChannel, 0, new CdpValueSet().AddUInt32("ControlMessage", 6).AddUInt32("MaxPlatformVersion", 1).AddUInt32("MinPlatformVersion", 1).AddGuid("OperationId", operationId), cancellationToken);
            var handshake = await ReadValueSetAsync(cancellationToken);
            if (!handshake.ContainsKey("VersionHandShakeResult") || handshake.GetUInt32("VersionHandShakeResult") != 1) throw new ShareProtocolException("Microsoft Nearby platform handshake failed.");
            var channel = await StartChannelAsync(operationId.ToString("D").ToUpperInvariant(), "NearSharePlatform", cancellationToken);
            var contentIds = new List<uint>(); var contentSizes = new List<ulong>(); var fileNames = new List<string>(); ulong total = 0;
            for (uint i = 0; i < offer.Files.Count; i++) { contentIds.Add(i); contentSizes.Add((ulong)Math.Max(0, offer.Files[(int)i].Size)); fileNames.Add(offer.Files[(int)i].Name); total += (ulong)Math.Max(0, offer.Files[(int)i].Size); }
            var transfer = new CdpValueSet().AddUInt32("ControlMessage", 1).AddUInt32("DataKind", 2).AddUInt64("BytesToSend", total).AddUInt32("FileCount", (uint)offer.Files.Count).AddUInt32Array("ContentIds", contentIds).AddUInt64Array("ContentSizes", contentSizes).AddStringArray("FileNames", fileNames);
            await SendValueSetAsync(channel, 10, transfer, cancellationToken);
            while (true)
            {
                var message = await ReadValueSetAsync(cancellationToken);
                var control = message.ContainsKey("ControlMessage") ? message.GetUInt32("ControlMessage") : 0;
                if (control == 3)
                {
                    var contentId = message.GetUInt32("ContentId"); var position = message.GetUInt64("BlobPosition"); var requested = message.GetUInt32("BlobSize");
                    if (contentId >= offer.Files.Count || position > (ulong)offer.Files[(int)contentId].Size || requested > 102400) throw new ShareProtocolException("Microsoft Nearby requested an invalid file range.");
                    var body = await ReadFileRangeAsync(offer.Files[(int)contentId].SourceFile, position, requested, cancellationToken);
                    var response = new CdpValueSet().AddUInt32("ControlMessage", 4).AddUInt32("ContentId", contentId).AddUInt64("BlobPosition", position).AddByteArray("DataBlob", body);
                    await SendValueSetAsync(channel, message.MessageId, response, cancellationToken);
                    progress?.Report(new ShareProgress(offer.Files[(int)contentId].Name, (long)Math.Min((ulong)offer.Files[(int)contentId].Size, position + requested), offer.Files[(int)contentId].Size));
                }
                else if (control == 2) return;
                else if (control == 5) throw new OperationCanceledException("Microsoft Nearby canceled the transfer.");
                else throw new ShareProtocolException("Microsoft Nearby sent an unexpected NearShare control message.");
            }
        }

        private async Task<ulong> StartChannelAsync(string appId, string appName, CancellationToken cancellationToken)
        {
            var requestId = ++_requestId;
            var payload = new MemoryStream(); payload.WriteByte(0); WriteUtf8WithLength(payload, appId); WriteUtf8WithLength(payload, appName); WriteUInt16(payload, 0); WriteUtf8WithLength(payload, string.Empty);
            var header = NewSessionHeader(CdpHeader.Control, 0, requestId, ++_sequence, 0, 1);
            await WriteEncryptedAsync(header, payload.ToArray(), cancellationToken);
            while (true)
            {
                var packet = await CdpConnection.ReadAsync(_connection, cancellationToken); var plain = _crypto.Decrypt(packet);
                if (packet.Header.Type != CdpHeader.Control || plain.Length < 10 || plain[0] != 1) continue;
                var reply = packet.Header.AdditionalHeaders.FirstOrDefault(x => x.Type == 1);
                if (reply == null || reply.AsUInt64Little() != requestId) continue;
                var result = plain[1]; if (result != 0) throw new ShareProtocolException("Microsoft Nearby refused the NearShare channel.");
                return ReadUInt64(plain, 2);
            }
        }

        private async Task SendValueSetAsync(ulong channelId, uint messageId, CdpValueSet valueSet, CancellationToken cancellationToken)
        {
            var body = valueSet.Serialize(); var payload = new byte[12 + body.Length]; WriteUInt32(payload, 0, 1); WriteUInt32(payload, 4, 0); WriteUInt32(payload, 8, messageId); System.Buffer.BlockCopy(body, 0, payload, 12, body.Length);
            await SendSessionPayloadAsync(channelId, payload, cancellationToken);
        }

        private async Task<CdpSessionValueSet> ReadValueSetAsync(CancellationToken cancellationToken)
        {
            var first = await CdpConnection.ReadAsync(_connection, cancellationToken);
            if (first.Header.Type != CdpHeader.Session || first.Header.FragmentIndex != 0)
                throw new ShareProtocolException("Microsoft Nearby sent an invalid NearShare session fragment.");
            var plain = _crypto.Decrypt(first);
            var all = new MemoryStream(); all.Write(plain, 0, plain.Length);
            for (var index = 1; index < first.Header.FragmentCount; index++)
            {
                var next = await CdpConnection.ReadAsync(_connection, cancellationToken);
                if (next.Header.Type != CdpHeader.Session || next.Header.SequenceNumber != first.Header.SequenceNumber || next.Header.FragmentIndex != index)
                    throw new ShareProtocolException("Microsoft Nearby sent out-of-order NearShare fragments.");
                var nextPlain = _crypto.Decrypt(next); all.Write(nextPlain, 0, nextPlain.Length);
            }
            var data = all.ToArray(); if (data.Length < 12) throw new ShareProtocolException("Microsoft Nearby sent a truncated NearShare message.");
            var payload = new byte[data.Length - 12]; System.Buffer.BlockCopy(data, 12, payload, 0, payload.Length);
            return new CdpSessionValueSet(ReadUInt32(data, 8), CdpValueSet.Parse(payload));
        }

        private async Task SendSessionPayloadAsync(ulong channelId, byte[] payload, CancellationToken cancellationToken)
        {
            var count = (ushort)Math.Max(1, (payload.Length + 16383) / 16384); var sequence = ++_sequence;
            for (ushort index = 0; index < count; index++)
            {
                var start = index * 16384; var length = Math.Min(16384, payload.Length - start); var fragment = new byte[length]; System.Buffer.BlockCopy(payload, start, fragment, 0, length);
                var header = NewSessionHeader(CdpHeader.Session, channelId, 0, sequence, index, count);
                await WriteEncryptedAsync(header, fragment, cancellationToken);
            }
        }

        private async Task WriteEncryptedAsync(CdpHeader header, byte[] payload, CancellationToken cancellationToken)
        {
            await _connection.WriteRawAsync(_crypto.Encrypt(header, payload), cancellationToken);
        }

        private CdpHeader NewSessionHeader(byte type, ulong channelId, ulong requestId, uint sequence, ushort index, ushort count)
        {
            var header = new CdpHeader { Type = type, SessionId = _sessionId, ChannelId = channelId, RequestId = requestId, SequenceNumber = sequence, FragmentIndex = index, FragmentCount = count };
            if (type == CdpHeader.Session) header.AdditionalHeaders.Add(new CdpAdditionalHeader(2, Encoding.ASCII.GetBytes("A.1")));
            return header;
        }

        private static async Task<IList<byte>> ReadFileRangeAsync(StorageFile file, ulong position, uint length, CancellationToken cancellationToken)
        {
            if (file == null) throw new ShareProtocolException("The selected file is unavailable.");
            using (var input = await file.OpenReadAsync())
            using (var reader = new DataReader(input))
            {
                input.Seek(position); var result = new List<byte>((int)length); while (result.Count < length) { cancellationToken.ThrowIfCancellationRequested(); var loaded = await reader.LoadAsync(length - (uint)result.Count); if (loaded == 0) break; var buffer = new byte[loaded]; reader.ReadBytes(buffer); result.AddRange(buffer); }
                if (result.Count != length) throw new EndOfStreamException("The selected file ended before the requested range."); return result;
            }
        }

        private async Task SendAuthenticationAsync(byte messageType, byte[] certificate, byte[] signature, CancellationToken cancellationToken)
        {
            var payload = new MemoryStream(); WriteUInt16(payload, ConnectionModeProximal); payload.WriteByte(messageType);
            WriteBytesWithLength(payload, certificate); WriteBytesWithLength(payload, signature);
            await SendConnectMessageAsync(messageType, payload.ToArray(), cancellationToken, true);
        }

        private async Task ReceiveAuthenticationAsync(byte expectedType, CancellationToken cancellationToken)
        {
            var packet = await CdpConnection.ReadAsync(_connection, cancellationToken);
            var payload = new MemoryStream(ReadConnectionPayload(packet, expectedType));
            var certificate = ReadBytesWithLength(payload); var signature = ReadBytesWithLength(payload);
            if (!CdpIdentity.VerifyAuthentication(certificate, signature, _hostNonce, _clientNonce))
                throw new ShareProtocolException("Microsoft Nearby returned an invalid CDP certificate signature.");
        }

        private async Task SendConnectMessageAsync(byte messageType, byte[] payload, CancellationToken cancellationToken, bool alreadyHasHeader)
        {
            var header = CopyHeader(_replyHeaders ?? NewConnectHeader());
            header.Type = CdpHeader.Connect; header.Flags = 0; header.SessionId = _sessionId; header.ChannelId = 0; header.SequenceNumber = 0; header.FragmentIndex = 0; header.FragmentCount = 1;
            if (!alreadyHasHeader)
            {
                var body = new MemoryStream(); WriteUInt16(body, ConnectionModeProximal); body.WriteByte(messageType); body.Write(payload, 0, payload.Length); payload = body.ToArray();
            }
            await CdpConnection.WriteAsync(_connection, header, payload, cancellationToken);
        }

        private byte[] ReadConnectionPayload(CdpPacket packet, byte expectedType)
        {
            if (packet.Header.Type != CdpHeader.Connect) throw new ShareProtocolException("Microsoft Nearby returned a non-connection CDP message.");
            var payload = packet.Payload;
            if ((packet.Header.Flags & CdpHeader.SessionEncrypted) != 0) payload = _crypto.Decrypt(packet);
            if (payload.Length < 3) throw new ShareProtocolException("Microsoft Nearby returned a truncated CDP connection message.");
            var mode = ReadUInt16(payload, 0); var messageType = payload[2];
            if (mode != ConnectionModeProximal || messageType != expectedType) throw new ShareProtocolException("Microsoft Nearby returned an unexpected CDP connection message.");
            var remaining = new byte[payload.Length - 3]; System.Buffer.BlockCopy(payload, 3, remaining, 0, remaining.Length); return remaining;
        }

        private CdpHeader NewConnectHeader()
        {
            var header = new CdpHeader { Type = CdpHeader.Connect, SessionId = _sessionId, FragmentCount = 1 };
            header.AdditionalHeaders.Add(CdpAdditionalHeader.UInt32(0x81, 0x70000003));
            header.AdditionalHeaders.Add(CdpAdditionalHeader.UInt64(0x82, 0x1F));
            header.AdditionalHeaders.Add(CdpAdditionalHeader.UInt64(0x83, 7));
            return header;
        }

        private static CdpHeader CopyHeader(CdpHeader source)
        {
            var result = new CdpHeader { Type = source.Type, Flags = source.Flags, SequenceNumber = source.SequenceNumber, RequestId = source.RequestId, FragmentIndex = source.FragmentIndex, FragmentCount = source.FragmentCount, SessionId = source.SessionId, ChannelId = source.ChannelId };
            foreach (var header in source.AdditionalHeaders) result.AdditionalHeaders.Add(new CdpAdditionalHeader(header.Type, header.Value));
            return result;
        }

        private static byte[] ReadBytesWithLength(Stream stream)
        {
            var length = ReadUInt16(stream); if (length > stream.Length - stream.Position) throw new InvalidDataException("CDP length-delimited value is truncated.");
            var result = new byte[length]; var read = stream.Read(result, 0, result.Length); if (read != result.Length) throw new EndOfStreamException(); return result;
        }

        private static void WriteBytesWithLength(Stream stream, byte[] value) { WriteUInt16(stream, checked((ushort)value.Length)); stream.Write(value, 0, value.Length); }
        private static void WriteUtf8WithLength(Stream stream, string value) { var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty); WriteUInt16(stream, checked((ushort)bytes.Length)); stream.Write(bytes, 0, bytes.Length); }
        private static ushort ReadUInt16(Stream stream) { var first = stream.ReadByte(); var second = stream.ReadByte(); if (second < 0) throw new EndOfStreamException(); return (ushort)((first << 8) | second); }
        private static ushort ReadUInt16(byte[] value, int offset) { return (ushort)((value[offset] << 8) | value[offset + 1]); }
        private static uint ReadUInt32(Stream stream) { return ((uint)ReadUInt16(stream) << 16) | ReadUInt16(stream); }
        private static ulong ReadUInt64(Stream stream) { ulong value = 0; for (var i = 0; i < 8; i++) { var item = stream.ReadByte(); if (item < 0) throw new EndOfStreamException(); value = (value << 8) | (byte)item; } return value; }
        private static void WriteUInt16(Stream stream, ushort value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void WriteUInt32(Stream stream, uint value) { WriteUInt16(stream, (ushort)(value >> 16)); WriteUInt16(stream, (ushort)value); }
        private static void WriteUInt64(Stream stream, ulong value) { for (var i = 7; i >= 0; i--) stream.WriteByte((byte)(value >> (8 * i))); }
        private static void WriteUInt32(byte[] value, int offset, uint number) { value[offset] = (byte)(number >> 24); value[offset + 1] = (byte)(number >> 16); value[offset + 2] = (byte)(number >> 8); value[offset + 3] = (byte)number; }
        private static uint ReadUInt32(byte[] value, int offset) { return ((uint)value[offset] << 24) | ((uint)value[offset + 1] << 16) | ((uint)value[offset + 2] << 8) | value[offset + 3]; }
        private static ulong ReadUInt64(byte[] value, int offset) { ulong number = 0; for (var i = 0; i < 8; i++) number = (number << 8) | value[offset + i]; return number; }
        private static ulong RandomUInt64() { var bytes = new byte[8]; using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(bytes); return ReadUInt64(new MemoryStream(bytes)); }
        private static uint RandomUInt32() { return (uint)RandomUInt64(); }

        private sealed class CdpSessionValueSet
        {
            internal uint MessageId { get; private set; }
            internal CdpValueSet Values { get; private set; }
            internal CdpSessionValueSet(uint messageId, CdpValueSet values) { MessageId = messageId; Values = values; }
            internal bool ContainsKey(string key) { return Values.Values.ContainsKey(key); }
            internal uint GetUInt32(string key) { return Values.GetUInt32(key); }
            internal ulong GetUInt64(string key) { return Values.GetUInt64(key); }
        }
    }
}
