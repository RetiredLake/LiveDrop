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

    internal static class QuickShareFrames
    {
        internal const int UkeyClientInit = 2;
        internal const int UkeyServerInit = 3;
        internal const int UkeyClientFinish = 4;
        internal const int NearbyWifiLan = 5;
        internal const int NearbyConnectionRequest = 1;
        internal const int NearbyConnectionResponse = 2;
        internal const int NearbyPayloadTransfer = 3;
        internal const int NearbyKeepAlive = 5;
        internal const int SharingIntroduction = 1;
        internal const int SharingResponse = 2;

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

        internal static byte[] BuildIntroduction(IReadOnlyList<ShareFileDescriptor> files)
        {
            var intro = new ProtoWriter();
            long payloadId = 1000;
            foreach (var file in files)
            {
                var metadata = new ProtoWriter();
                metadata.WriteString(1, file.Name);
                metadata.WriteEnum(2, FileType(file.MimeType));
                metadata.WriteInt64(3, payloadId++);
                metadata.WriteInt64(4, file.Size);
                metadata.WriteString(5, file.MimeType);
                metadata.WriteInt64(6, DateTime.UtcNow.Ticks);
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

        internal static byte[] BuildPayloadChunk(long payloadId, long offset, byte[] body, bool last)
        {
            var header = new ProtoWriter();
            header.WriteInt64(1, payloadId);
            header.WriteEnum(2, 2);
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

        private static byte[] BuildOfflineFrame(int type, int nestedField, byte[] nested)
        {
            var v1 = new ProtoWriter();
            v1.WriteEnum(1, type);
            v1.WriteMessage(nestedField, nested);
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

