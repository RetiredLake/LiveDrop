using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LiveDrop.Models;

namespace LiveDrop.Protocols.QuickShare
{
    internal sealed class QuickShareUkeyMessage
    {
        internal int Type { get; private set; }
        internal byte[] Data { get; private set; }
        internal QuickShareUkeyMessage(int type, byte[] data) { Type = type; Data = data ?? new byte[0]; }
    }

    internal sealed class QuickShareConnectionRequest
    {
        internal string EndpointId { get; set; }
        internal string EndpointName { get; set; }
        internal byte[] EndpointInfo { get; set; }
    }

    internal sealed class QuickShareConnectionResponse
    {
        internal bool Accepted { get; set; }
        internal int SafeToDisconnectVersion { get; set; }
    }

    internal sealed class QuickSharePayloadChunk
    {
        internal int PacketType { get; set; }
        internal long PayloadId { get; set; }
        internal int PayloadType { get; set; }
        internal long TotalSize { get; set; }
        internal string FileName { get; set; }
        internal long Offset { get; set; }
        internal bool Last { get; set; }
        internal byte[] Body { get; set; }
    }

    internal sealed class QuickShareFileMetadata
    {
        internal string Name { get; set; }
        internal string MimeType { get; set; }
        internal long PayloadId { get; set; }
        internal long AttachmentId { get; set; }
        internal long Size { get; set; }
    }

    internal static class QuickShareFrames
    {
        internal const int UkeyClientInit = 2;
        internal const int UkeyServerInit = 3;
        internal const int UkeyClientFinish = 4;
        internal const int NearbyWifiLan = 5;
        internal const int NearbyConnectionRequest = 1;
        internal const int NearbyConnectionResponse = 2;
        internal const int NearbyPayloadTransfer = 3;
        internal const int NearbyBandwidthUpgradeNegotiation = 4;
        internal const int NearbyKeepAlive = 5;
        internal const int NearbyDisconnection = 6;
        internal const int NearbyBandwidthUpgradeRetry = 12;
        internal const int SharingIntroduction = 1;
        internal const int SharingResponse = 2;
        internal const int SharingPairedKeyEncryption = 3;
        internal const int SharingPairedKeyResult = 4;
        internal const int SharingCancel = 6;
        internal const int SharingProgressUpdate = 7;

        internal static byte[] WrapUkey(int type, byte[] data)
        {
            var writer = new ProtoWriter();
            writer.WriteEnum(1, type);
            writer.WriteBytes(2, data);
            return writer.ToArray();
        }

        internal static QuickShareUkeyMessage ParseUkey(byte[] data)
        {
            var reader = new ProtoReader(data);
            var type = 0;
            var body = new byte[0];
            while (!reader.End)
            {
                var tag = reader.ReadTag();
                var field = tag >> 3;
                var wire = tag & 7;
                if (field == 1 && wire == 0) type = (int)reader.ReadVarint();
                else if (field == 2 && wire == 2) body = reader.ReadBytes();
                else reader.Skip(wire);
            }
            return new QuickShareUkeyMessage(type, body);
        }

        internal static byte[] ParseClientCommitment(byte[] clientInitBody)
        {
            var reader = new ProtoReader(clientInitBody);
            while (!reader.End)
            {
                var tag = reader.ReadTag();
                if ((tag >> 3) != 3 || (tag & 7) != 2) { reader.Skip(tag & 7); continue; }
                var commitment = new ProtoReader(reader.ReadBytes());
                while (!commitment.End)
                {
                    var field = commitment.ReadTag();
                    if ((field >> 3) == 2 && (field & 7) == 2) return commitment.ReadBytes();
                    commitment.Skip(field & 7);
                }
            }
            return new byte[0];
        }

        internal static byte[] ParseClientFinishedPublicKey(byte[] clientFinishBody)
        {
            var reader = new ProtoReader(clientFinishBody);
            while (!reader.End)
            {
                var tag = reader.ReadTag();
                if ((tag >> 3) == 1 && (tag & 7) == 2) return reader.ReadBytes();
                reader.Skip(tag & 7);
            }
            return new byte[0];
        }

        internal static byte[] BuildClientInit(byte[] random, byte[] commitment)
        {
            var commitmentWriter = new ProtoWriter();
            commitmentWriter.WriteEnum(1, 100);
            commitmentWriter.WriteBytes(2, commitment);
            var init = new ProtoWriter();
            init.WriteInt32(1, 1);
            init.WriteBytes(2, random);
            init.WriteMessage(3, commitmentWriter.ToArray());
            init.WriteString(4, "AES_256_CBC-HMAC_SHA256");
            return WrapUkey(UkeyClientInit, init.ToArray());
        }

        internal static byte[] BuildClientFinish(byte[] genericPublicKey)
        {
            var finish = new ProtoWriter();
            finish.WriteBytes(1, genericPublicKey);
            return WrapUkey(UkeyClientFinish, finish.ToArray());
        }

        internal static byte[] BuildPairedKeyEncryption()
        {
            var paired = new ProtoWriter();
            paired.WriteBytes(1, RandomBytes(72));
            paired.WriteBytes(2, RandomBytes(6));
            return BuildSharingFrame(3, 4, paired.ToArray());
        }

        internal static byte[] BuildPairedKeyResult()
        {
            var paired = new ProtoWriter(); paired.WriteEnum(1, 3);
            return BuildSharingFrame(4, 5, paired.ToArray());
        }

        internal static byte[] BuildServerInit(byte[] random, byte[] genericPublicKey)
        {
            var init = new ProtoWriter();
            init.WriteInt32(1, 1);
            init.WriteBytes(2, random);
            init.WriteEnum(3, 100);
            init.WriteBytes(4, genericPublicKey);
            return WrapUkey(UkeyServerInit, init.ToArray());
        }

        internal static byte[] BuildEndpointInfo(string displayName, int deviceType)
        {
            var name = Encoding.UTF8.GetBytes(displayName ?? "LiveDrop");
            var length = Math.Min(255, name.Length);
            var result = new byte[18 + length];
            // Quick Share endpoint info uses the device type in bits 1-3;
            // bit 0 is reserved and the visible/default endpoint uses zero
            // for the remaining flags. This is the encoding used by current
            // interoperable clients.
            result[0] = (byte)((deviceType & 7) << 1);
            var random = new byte[16];
            new Random().NextBytes(random);
            Buffer.BlockCopy(random, 0, result, 1, random.Length);
            result[17] = (byte)length;
            Buffer.BlockCopy(name, 0, result, 18, length);
            return result;
        }

        internal static byte[] BuildConnectionRequest(string endpointId, string displayName, byte[] endpointInfo)
        {
            var request = new ProtoWriter();
            request.WriteString(1, endpointId);
            request.WriteString(2, displayName);
            request.WriteBytes(6, endpointInfo);
            request.WriteInt32(4, Environment.TickCount);
            request.WriteEnum(5, NearbyWifiLan);
            // Android 14+ uses these advertised timers when deciding whether
            // to keep the LAN connection alive after the UKEY2 exchange.
            request.WriteInt32(8, 10000);
            request.WriteInt32(9, 600000);
            return BuildOfflineFrame(NearbyConnectionRequest, 2, request.ToArray());
        }

        internal static byte[] BuildConnectionAccept()
        {
            var response = new ProtoWriter();
            response.WriteInt32(1, 0);
            response.WriteEnum(3, 1);
            var osInfo = new ProtoWriter();
            osInfo.WriteEnum(1, 3);
            response.WriteMessage(4, osInfo.ToArray());
            // This implementation stays on its single LAN socket. Declare
            // that explicitly and advertise the safe teardown mode that the
            // receiver implements for current Android clients.
            response.WriteInt32(5, 0);
            response.WriteInt32(7, 1);
            response.WriteInt32(9, 600000);
            return BuildOfflineFrame(NearbyConnectionResponse, 3, response.ToArray());
        }

        internal static int ReadOfflineFrameType(byte[] data)
        {
            var outer = new ProtoReader(data);
            while (!outer.End)
            {
                var tag = outer.ReadTag();
                if ((tag >> 3) == 2 && (tag & 7) == 2)
                {
                    var v1 = new ProtoReader(outer.ReadBytes());
                    while (!v1.End)
                    {
                        var v1Tag = v1.ReadTag();
                        if ((v1Tag >> 3) == 1 && (v1Tag & 7) == 0) return (int)v1.ReadVarint();
                        v1.Skip(v1Tag & 7);
                    }
                }
                else outer.Skip(tag & 7);
            }
            return 0;
        }

        internal static QuickShareConnectionRequest ParseConnectionRequest(byte[] data)
        {
            var request = new QuickShareConnectionRequest();
            var v1 = ReadV1(data);
            while (!v1.End)
            {
                var tag = v1.ReadTag();
                if ((tag >> 3) != 2 || (tag & 7) != 2) { v1.Skip(tag & 7); continue; }
                var body = new ProtoReader(v1.ReadBytes());
                while (!body.End)
                {
                    var field = body.ReadTag();
                    if ((field >> 3) == 1 && (field & 7) == 2) request.EndpointId = body.ReadString();
                    else if ((field >> 3) == 2 && (field & 7) == 2) request.EndpointName = body.ReadString();
                    else if ((field >> 3) == 6 && (field & 7) == 2) request.EndpointInfo = body.ReadBytes();
                    else body.Skip(field & 7);
                }
                break;
            }
            return request;
        }

        internal static bool IsAcceptedConnection(byte[] data)
        {
            return ParseConnectionResponse(data).Accepted;
        }

        internal static QuickShareConnectionResponse ParseConnectionResponse(byte[] data)
        {
            var result = new QuickShareConnectionResponse();
            var v1 = ReadV1(data);
            while (!v1.End)
            {
                var tag = v1.ReadTag();
                if ((tag >> 3) != 3 || (tag & 7) != 2) { v1.Skip(tag & 7); continue; }
                var response = new ProtoReader(v1.ReadBytes());
                while (!response.End)
                {
                    var field = response.ReadTag();
                    if ((field >> 3) == 3 && (field & 7) == 0) result.Accepted = response.ReadVarint() == 1;
                    else if ((field >> 3) == 7 && (field & 7) == 0) result.SafeToDisconnectVersion = (int)response.ReadVarint();
                    else response.Skip(field & 7);
                }
                return result;
            }
            return result;
        }

        internal static bool IsAcceptedSharingResponse(byte[] data)
        {
            var v1 = ReadV1(data);
            while (!v1.End)
            {
                var tag = v1.ReadTag();
                if ((tag >> 3) != 3 || (tag & 7) != 2) { v1.Skip(tag & 7); continue; }
                var response = new ProtoReader(v1.ReadBytes());
                while (!response.End)
                {
                    var field = response.ReadTag();
                    if ((field >> 3) == 1 && (field & 7) == 0) return response.ReadVarint() == 1;
                    response.Skip(field & 7);
                }
            }
            return false;
        }

        internal static bool IsCancelSharingFrame(byte[] data)
        {
            try { return ReadOfflineFrameType(data) == SharingCancel; }
            catch { return false; }
        }

        internal static byte[] BuildCancelSharingFrame()
        {
            return BuildOfflineFrame(SharingCancel, 0, null);
        }

        internal static byte[] ReadSharingPayload(byte[] data)
        {
            var chunk = ParsePayloadChunk(data);
            return chunk == null ? null : chunk.Body;
        }

        internal static QuickSharePayloadChunk ParsePayloadChunk(byte[] data)
        {
            var v1 = ReadV1(data);
            while (!v1.End)
            {
                var tag = v1.ReadTag();
                if ((tag >> 3) != 4 || (tag & 7) != 2) { v1.Skip(tag & 7); continue; }
                var transfer = new ProtoReader(v1.ReadBytes());
                var packetType = 0;
                var header = new byte[0]; var chunk = new byte[0];
                while (!transfer.End)
                {
                    var field = transfer.ReadTag();
                    if ((field >> 3) == 1 && (field & 7) == 0) packetType = (int)transfer.ReadVarint();
                    else if ((field >> 3) == 2 && (field & 7) == 2) header = transfer.ReadBytes();
                    else if ((field >> 3) == 3 && (field & 7) == 2) chunk = transfer.ReadBytes();
                    else transfer.Skip(field & 7);
                }
                var result = new QuickSharePayloadChunk { PacketType = packetType, Body = new byte[0] };
                var headerReader = new ProtoReader(header);
                while (!headerReader.End)
                {
                    var field = headerReader.ReadTag();
                    if ((field >> 3) == 1 && (field & 7) == 0) result.PayloadId = (long)headerReader.ReadVarint();
                    else if ((field >> 3) == 2 && (field & 7) == 0) result.PayloadType = (int)headerReader.ReadVarint();
                    else if ((field >> 3) == 3 && (field & 7) == 0) result.TotalSize = (long)headerReader.ReadVarint();
                    else if ((field >> 3) == 5 && (field & 7) == 2) result.FileName = headerReader.ReadString();
                    else headerReader.Skip(field & 7);
                }
                var chunkReader = new ProtoReader(chunk);
                while (!chunkReader.End)
                {
                    var field = chunkReader.ReadTag();
                    if ((field >> 3) == 1 && (field & 7) == 0) result.Last = chunkReader.ReadVarint() != 0;
                    else if ((field >> 3) == 2 && (field & 7) == 0) result.Offset = (long)chunkReader.ReadVarint();
                    else if ((field >> 3) == 3 && (field & 7) == 2) result.Body = chunkReader.ReadBytes();
                    else chunkReader.Skip(field & 7);
                }
                return result;
            }
            return null;
        }

        internal static IList<QuickShareFileMetadata> ParseIntroduction(byte[] data)
        {
            var result = new List<QuickShareFileMetadata>();
            var v1 = ReadV1(data);
            while (!v1.End)
            {
                var tag = v1.ReadTag();
                if ((tag >> 3) != 2 || (tag & 7) != 2) { v1.Skip(tag & 7); continue; }
                var intro = new ProtoReader(v1.ReadBytes());
                while (!intro.End)
                {
                    var field = intro.ReadTag();
                    if ((field >> 3) != 1 || (field & 7) != 2) { intro.Skip(field & 7); continue; }
                    var meta = new ProtoReader(intro.ReadBytes());
                    var file = new QuickShareFileMetadata { MimeType = "application/octet-stream" };
                    while (!meta.End)
                    {
                        var metaField = meta.ReadTag();
                        if ((metaField >> 3) == 1 && (metaField & 7) == 2) file.Name = meta.ReadString();
                        else if ((metaField >> 3) == 3 && (metaField & 7) == 0) file.PayloadId = (long)meta.ReadVarint();
                        else if ((metaField >> 3) == 4 && (metaField & 7) == 0) file.Size = (long)meta.ReadVarint();
                        else if ((metaField >> 3) == 5 && (metaField & 7) == 2) file.MimeType = meta.ReadString();
                        else if ((metaField >> 3) == 6 && (metaField & 7) == 0) file.AttachmentId = (long)meta.ReadVarint();
                        else meta.Skip(metaField & 7);
                    }
                    result.Add(file);
                }
                break;
            }
            return result;
        }

        internal static byte[] BuildBytesPayload(byte[] body, long payloadId, bool last)
        {
            return BuildPayloadChunk(payloadId, 1, body == null ? 0 : body.Length, 0, body, last);
        }

        internal static byte[] BuildIntroduction(IReadOnlyList<ShareFileDescriptor> files)
        {
            var payloadIds = new List<long>();
            for (var i = 0; i < files.Count; i++) payloadIds.Add(1000 + i);
            return BuildIntroduction(files, payloadIds);
        }

        internal static byte[] BuildIntroduction(IReadOnlyList<ShareFileDescriptor> files, IReadOnlyList<long> payloadIds)
        {
            if (files == null || payloadIds == null || files.Count != payloadIds.Count) throw new ArgumentException("Quick Share file metadata and payload IDs must have the same count.");
            var intro = new ProtoWriter();
            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                var payloadId = payloadIds[index];
                if (payloadId <= 0) throw new ArgumentException("Quick Share payload IDs must be positive.");
                var metadata = new ProtoWriter();
                metadata.WriteString(1, file.Name);
                metadata.WriteEnum(2, FileType(file.MimeType));
                metadata.WriteInt64(3, payloadId);
                metadata.WriteInt64(4, file.Size);
                metadata.WriteString(5, file.MimeType);
                // Quick Share clients use FileMetadata.id as the attachment
                // identity and expect it to equal payload_id.
                metadata.WriteInt64(6, payloadId);
                metadata.WriteInt64(8, StableAttachmentHash(file.Name, file.Size));
                intro.WriteMessage(1, metadata.ToArray());
            }
            intro.WriteEnum(8, 1);
            return BuildSharingFrame(SharingIntroduction, 2, intro.ToArray());
        }

        internal static byte[] BuildAcceptTransfer()
        {
            var response = new ProtoWriter();
            response.WriteEnum(1, 1);
            return BuildSharingFrame(SharingResponse, 3, response.ToArray());
        }

        internal static byte[] BuildRejectTransfer()
        {
            var response = new ProtoWriter();
            response.WriteEnum(1, 2);
            return BuildSharingFrame(SharingResponse, 3, response.ToArray());
        }

        internal static byte[] BuildDisconnection()
        {
            return BuildDisconnection(false, false);
        }

        internal static byte[] BuildDisconnection(bool requestSafeToDisconnect, bool acknowledgeSafeToDisconnect)
        {
            var disconnection = new ProtoWriter();
            if (requestSafeToDisconnect) disconnection.WriteBool(1, true);
            if (acknowledgeSafeToDisconnect) disconnection.WriteBool(2, true);
            return BuildOfflineFrame(NearbyDisconnection, 7, disconnection.ToArray());
        }

        internal static bool IsDisconnection(byte[] data)
        {
            return ReadOfflineFrameType(data) == NearbyDisconnection;
        }

        internal static bool IsSafeDisconnectRequest(byte[] data)
        {
            return ReadDisconnectionFlag(data, 1);
        }

        internal static bool IsSafeDisconnectAcknowledgement(byte[] data)
        {
            return ReadDisconnectionFlag(data, 2);
        }

        internal static byte[] BuildKeepAlive()
        {
            return BuildKeepAlive(false, NextKeepAliveSequence());
        }

        internal static byte[] BuildKeepAlive(bool acknowledgement)
        {
            return BuildKeepAlive(acknowledgement, NextKeepAliveSequence());
        }

        internal static byte[] BuildKeepAlive(bool acknowledgement, uint sequence)
        {
            var keepAlive = new ProtoWriter();
            if (acknowledgement) keepAlive.WriteBool(1, true);
            keepAlive.WriteInt32(2, unchecked((int)sequence));
            return BuildOfflineFrame(NearbyKeepAlive, 6, keepAlive.ToArray());
        }

        internal static bool IsKeepAliveAcknowledgement(byte[] data)
        {
            var v1 = ReadV1(data);
            while (!v1.End)
            {
                var tag = v1.ReadTag();
                if ((tag >> 3) != 6 || (tag & 7) != 2) { v1.Skip(tag & 7); continue; }
                var keepAlive = new ProtoReader(v1.ReadBytes());
                while (!keepAlive.End)
                {
                    var field = keepAlive.ReadTag();
                    if ((field >> 3) == 1 && (field & 7) == 0) return keepAlive.ReadVarint() != 0;
                    keepAlive.Skip(field & 7);
                }
                return false;
            }
            return false;
        }

        internal static uint ReadKeepAliveSequence(byte[] data)
        {
            var v1 = ReadV1(data);
            while (!v1.End)
            {
                var tag = v1.ReadTag();
                if ((tag >> 3) != 6 || (tag & 7) != 2) { v1.Skip(tag & 7); continue; }
                var keepAlive = new ProtoReader(v1.ReadBytes());
                while (!keepAlive.End)
                {
                    var field = keepAlive.ReadTag();
                    if ((field >> 3) == 2 && (field & 7) == 0) return (uint)keepAlive.ReadVarint();
                    keepAlive.Skip(field & 7);
                }
                return 0;
            }
            return 0;
        }

        internal static byte[] BuildFileChunk(long payloadId, long totalSize, long offset, byte[] body, bool last)
        {
            return BuildFileChunk(payloadId, totalSize, offset, body, last, null);
        }

        internal static byte[] BuildFileChunk(long payloadId, long totalSize, long offset, byte[] body, bool last, string fileName)
        {
            return BuildPayloadChunk(payloadId, 2, totalSize, offset, body, last, fileName);
        }

        internal static byte[] BuildPayloadChunk(long payloadId, long offset, byte[] body, bool last)
        {
            return BuildPayloadChunk(payloadId, 2, body == null ? 0 : body.Length, offset, body, last);
        }

        internal static byte[] BuildPayloadChunk(long payloadId, int payloadType, long totalSize, long offset, byte[] body, bool last)
        {
            return BuildPayloadChunk(payloadId, payloadType, totalSize, offset, body, last, null);
        }

        private static byte[] BuildPayloadChunk(long payloadId, int payloadType, long totalSize, long offset, byte[] body, bool last, string fileName)
        {
            var header = new ProtoWriter();
            header.WriteInt64(1, payloadId);
            header.WriteEnum(2, payloadType);
            header.WriteInt64(3, totalSize);
            if (payloadType == 2 && !string.IsNullOrEmpty(fileName)) header.WriteString(5, fileName);
            var chunk = new ProtoWriter();
            chunk.WriteInt32(1, last ? 1 : 0);
            chunk.WriteInt64(2, offset);
            if (body != null && body.Length > 0) chunk.WriteBytes(3, body);
            var transfer = new ProtoWriter();
            transfer.WriteEnum(1, 1);
            transfer.WriteMessage(2, header.ToArray());
            transfer.WriteMessage(3, chunk.ToArray());
            return BuildOfflineFrame(NearbyPayloadTransfer, 4, transfer.ToArray());
        }

        private static ProtoReader ReadV1(byte[] data)
        {
            var outer = new ProtoReader(data);
            while (!outer.End)
            {
                var tag = outer.ReadTag();
                if ((tag >> 3) == 2 && (tag & 7) == 2) return new ProtoReader(outer.ReadBytes());
                outer.Skip(tag & 7);
            }
            return new ProtoReader(new byte[0]);
        }

        private static bool ReadDisconnectionFlag(byte[] data, int flagField)
        {
            var v1 = ReadV1(data);
            while (!v1.End)
            {
                var tag = v1.ReadTag();
                if ((tag >> 3) != 7 || (tag & 7) != 2) { v1.Skip(tag & 7); continue; }
                var disconnection = new ProtoReader(v1.ReadBytes());
                while (!disconnection.End)
                {
                    var field = disconnection.ReadTag();
                    if ((field >> 3) == flagField && (field & 7) == 0) return disconnection.ReadVarint() != 0;
                    disconnection.Skip(field & 7);
                }
                return false;
            }
            return false;
        }

        private static byte[] RandomBytes(int count)
        {
            var value = new byte[count];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(value);
            return value;
        }

        private static int _keepAliveSequence;

        private static uint NextKeepAliveSequence()
        {
            return unchecked((uint)System.Threading.Interlocked.Increment(ref _keepAliveSequence));
        }

        private static byte[] BuildOfflineFrame(int type, int nestedField, byte[] nested)
        {
            var v1 = new ProtoWriter();
            v1.WriteEnum(1, type);
            if (nestedField > 0) v1.WriteMessage(nestedField, nested);
            var frame = new ProtoWriter();
            frame.WriteEnum(1, 1);
            frame.WriteMessage(2, v1.ToArray());
            return frame.ToArray();
        }

        private static byte[] BuildSharingFrame(int type, int nestedField, byte[] nested)
        {
            return BuildOfflineFrame(type, nestedField, nested);
        }

        private static int FileType(string mime)
        {
            if ((mime ?? string.Empty).StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return 1;
            if ((mime ?? string.Empty).StartsWith("video/", StringComparison.OrdinalIgnoreCase)) return 2;
            if ((mime ?? string.Empty).StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) return 4;
            return 5;
        }

        private static long StableAttachmentHash(string name, long size)
        {
            unchecked
            {
                long hash = 17;
                foreach (var c in name ?? string.Empty) hash = hash * 31 + c;
                return hash * 31 + size;
            }
        }
    }
}
