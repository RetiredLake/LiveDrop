using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LiveDrop.Transports;

namespace LiveDrop.Protocols.Nearby
{
    internal sealed class CdpHeader
    {
        internal const ushort Signature = 0x3030;
        internal const byte Version = 3;
        internal const byte Discovery = 1;
        internal const byte Connect = 2;
        internal const byte Control = 3;
        internal const byte Session = 4;
        internal const short HasHmac = 0x0002;
        internal const short SessionEncrypted = 0x0004;

        internal byte Type { get; set; }
        internal short Flags { get; set; }
        internal uint SequenceNumber { get; set; }
        internal ulong RequestId { get; set; }
        internal ushort FragmentIndex { get; set; }
        internal ushort FragmentCount { get; set; } = 1;
        internal ulong SessionId { get; set; }
        internal ulong ChannelId { get; set; }
        internal List<CdpAdditionalHeader> AdditionalHeaders { get; private set; } = new List<CdpAdditionalHeader>();
        internal ushort MessageLength { get; set; }

        internal int HeaderSize
        {
            get
            {
                var size = 40;
                foreach (var header in AdditionalHeaders) size += 2 + header.Value.Length;
                return size + 2;
            }
        }

        internal byte[] Serialize()
        {
            var stream = new MemoryStream();
            WriteUInt16(stream, Signature); WriteUInt16(stream, MessageLength); stream.WriteByte(Version); stream.WriteByte(Type);
            WriteUInt16(stream, unchecked((ushort)Flags)); WriteUInt32(stream, SequenceNumber); WriteUInt64(stream, RequestId);
            WriteUInt16(stream, FragmentIndex); WriteUInt16(stream, FragmentCount); WriteUInt64(stream, SessionId); WriteUInt64(stream, ChannelId);
            foreach (var header in AdditionalHeaders)
            {
                if (header.Value.Length > 255) throw new InvalidOperationException("CDP additional header is too large.");
                stream.WriteByte(header.Type); stream.WriteByte((byte)header.Value.Length); stream.Write(header.Value, 0, header.Value.Length);
            }
            stream.WriteByte(0); stream.WriteByte(0);
            return stream.ToArray();
        }

        internal static CdpHeader Parse(byte[] data)
        {
            if (data == null || data.Length < 42) throw new InvalidDataException("CDP header is truncated.");
            var offset = 0;
            if (ReadUInt16(data, ref offset) != Signature) throw new InvalidDataException("CDP signature is invalid.");
            var messageLength = ReadUInt16(data, ref offset);
            var version = data[offset++];
            var type = data[offset++];
            if (version != Version) throw new InvalidDataException("CDP version is not supported.");
            var header = new CdpHeader { MessageLength = messageLength, Type = type };
            header.Flags = unchecked((short)ReadUInt16(data, ref offset));
            header.SequenceNumber = ReadUInt32(data, ref offset); header.RequestId = ReadUInt64(data, ref offset);
            header.FragmentIndex = ReadUInt16(data, ref offset); header.FragmentCount = ReadUInt16(data, ref offset);
            header.SessionId = ReadUInt64(data, ref offset); header.ChannelId = ReadUInt64(data, ref offset);
            while (true)
            {
                if (offset + 2 > data.Length) throw new InvalidDataException("CDP additional header is truncated.");
                var additionalType = data[offset++]; var length = data[offset++];
                if (additionalType == 0)
                {
                    if (length != 0) throw new InvalidDataException("CDP end-of-header marker is invalid.");
                    break;
                }
                if (offset + length > data.Length) throw new InvalidDataException("CDP additional header value is truncated.");
                var value = new byte[length]; Buffer.BlockCopy(data, offset, value, 0, length); offset += length;
                header.AdditionalHeaders.Add(new CdpAdditionalHeader(additionalType, value));
            }
            if (offset != data.Length) throw new InvalidDataException("CDP header contains trailing bytes.");
            return header;
        }

        private static ushort ReadUInt16(byte[] data, ref int offset) { if (offset + 2 > data.Length) throw new InvalidDataException(); var value = (ushort)((data[offset] << 8) | data[offset + 1]); offset += 2; return value; }
        private static uint ReadUInt32(byte[] data, ref int offset) { return ((uint)ReadUInt16(data, ref offset) << 16) | ReadUInt16(data, ref offset); }
        private static ulong ReadUInt64(byte[] data, ref int offset) { ulong value = 0; for (var i = 0; i < 8; i++) value = (value << 8) | data[offset++]; return value; }
        private static void WriteUInt16(Stream stream, ushort value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void WriteUInt32(Stream stream, uint value) { WriteUInt16(stream, (ushort)(value >> 16)); WriteUInt16(stream, (ushort)value); }
        private static void WriteUInt64(Stream stream, ulong value) { for (var i = 7; i >= 0; i--) stream.WriteByte((byte)(value >> (8 * i))); }
    }

    internal sealed class CdpAdditionalHeader
    {
        internal byte Type { get; private set; }
        internal byte[] Value { get; private set; }
        internal CdpAdditionalHeader(byte type, byte[] value) { Type = type; Value = value ?? new byte[0]; }
        internal static CdpAdditionalHeader UInt32(byte type, uint value) { return new CdpAdditionalHeader(type, new[] { (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value }); }
        internal static CdpAdditionalHeader UInt64(byte type, ulong value)
        {
            var bytes = new byte[8]; for (var i = 7; i >= 0; i--) bytes[7 - i] = (byte)(value >> (i * 8)); return new CdpAdditionalHeader(type, bytes);
        }
        internal ulong AsUInt64() { if (Value.Length != 8) throw new InvalidDataException(); ulong value = 0; foreach (var item in Value) value = (value << 8) | item; return value; }
        internal ulong AsUInt64Little() { if (Value.Length != 8) throw new InvalidDataException(); ulong value = 0; for (var i = 7; i >= 0; i--) value = (value << 8) | Value[i]; return value; }
    }

    internal sealed class CdpPacket
    {
        internal CdpHeader Header { get; private set; }
        internal byte[] Payload { get; private set; }
        internal CdpPacket(CdpHeader header, byte[] payload) { Header = header; Payload = payload ?? new byte[0]; }
    }

    internal sealed class CdpCrypto
    {
        private readonly byte[] _encryptionKey;
        private readonly byte[] _ivKey;
        private readonly byte[] _hmacKey;

        internal CdpCrypto(byte[] sharedSecret)
        {
            if (sharedSecret == null || sharedSecret.Length != 64) throw new ArgumentException("CDP shared secret must be 64 bytes.");
            _encryptionKey = Copy(sharedSecret, 0, 16); _ivKey = Copy(sharedSecret, 16, 16); _hmacKey = Copy(sharedSecret, 32, 32);
        }

        internal byte[] Encrypt(CdpHeader header, byte[] payload)
        {
            var plain = new byte[4 + (payload == null ? 0 : payload.Length)];
            WriteUInt32(plain, 0, (uint)(payload == null ? 0 : payload.Length));
            if (payload != null) Buffer.BlockCopy(payload, 0, plain, 4, payload.Length);
            var encrypted = AesCrypt(plain, BuildIv(header), true, plain.Length % 16 != 0);
            header.Flags = (short)(header.Flags | CdpHeader.SessionEncrypted | CdpHeader.HasHmac);
            header.MessageLength = checked((ushort)(header.HeaderSize + encrypted.Length));
            var unsignedHeader = header.Serialize();
            var unsigned = Combine(unsignedHeader, encrypted);
            var hmac = Hmac(unsigned);
            header.MessageLength = checked((ushort)(header.HeaderSize + encrypted.Length + hmac.Length));
            return Combine(header.Serialize(), encrypted, hmac);
        }

        internal byte[] Decrypt(CdpPacket packet)
        {
            if ((packet.Header.Flags & CdpHeader.HasHmac) == 0 || (packet.Header.Flags & CdpHeader.SessionEncrypted) == 0) throw new InvalidDataException("CDP packet is not encrypted.");
            if (packet.Payload.Length < 32) throw new InvalidDataException("CDP encrypted packet is truncated.");
            var encryptedLength = packet.Payload.Length - 32;
            var encrypted = new byte[encryptedLength]; var supplied = new byte[32];
            Buffer.BlockCopy(packet.Payload, 0, encrypted, 0, encryptedLength); Buffer.BlockCopy(packet.Payload, encryptedLength, supplied, 0, 32);
            var oldLength = packet.Header.MessageLength;
            packet.Header.MessageLength = checked((ushort)(oldLength - 32));
            var expected = Hmac(Combine(packet.Header.Serialize(), encrypted));
            packet.Header.MessageLength = oldLength;
            if (!FixedEquals(expected, supplied)) throw new InvalidDataException("CDP HMAC verification failed.");
            var plain = AesCrypt(encrypted, BuildIv(packet.Header), false, false);
            if (plain.Length < 4) throw new InvalidDataException("CDP decrypted packet is truncated.");
            var length = ReadUInt32(plain, 0); if (length > plain.Length - 4) throw new InvalidDataException("CDP payload length is invalid.");
            var result = new byte[length]; Buffer.BlockCopy(plain, 4, result, 0, (int)length); return result;
        }

        private byte[] BuildIv(CdpHeader header)
        {
            var input = new byte[16]; WriteUInt64(input, 0, header.SessionId); WriteUInt32(input, 8, header.SequenceNumber); WriteUInt16(input, 12, header.FragmentIndex); WriteUInt16(input, 14, header.FragmentCount);
            using (var aes = Aes.Create())
            {
                aes.KeySize = 128; aes.Key = _ivKey; aes.Mode = CipherMode.ECB; aes.Padding = PaddingMode.None;
                using (var transform = aes.CreateEncryptor()) return transform.TransformFinalBlock(input, 0, input.Length);
            }
        }

        private byte[] AesCrypt(byte[] input, byte[] iv, bool encrypt, bool padding)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 128; aes.Key = _encryptionKey; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = padding ? PaddingMode.PKCS7 : PaddingMode.None;
                using (var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor()) return transform.TransformFinalBlock(input, 0, input.Length);
            }
        }

        private byte[] Hmac(byte[] data) { using (var hmac = new HMACSHA256(_hmacKey)) return hmac.ComputeHash(data); }
        private static byte[] Copy(byte[] source, int offset, int length) { var result = new byte[length]; Buffer.BlockCopy(source, offset, result, 0, length); return result; }
        private static byte[] Combine(params byte[][] values) { var size = 0; foreach (var value in values) size += value == null ? 0 : value.Length; var result = new byte[size]; var offset = 0; foreach (var value in values) { if (value == null) continue; Buffer.BlockCopy(value, 0, result, offset, value.Length); offset += value.Length; } return result; }
        private static bool FixedEquals(byte[] left, byte[] right) { if (left == null || right == null || left.Length != right.Length) return false; var value = 0; for (var i = 0; i < left.Length; i++) value |= left[i] ^ right[i]; return value == 0; }
        private static uint ReadUInt32(byte[] value, int offset) { return ((uint)value[offset] << 24) | ((uint)value[offset + 1] << 16) | ((uint)value[offset + 2] << 8) | value[offset + 3]; }
        private static void WriteUInt16(byte[] value, int offset, ushort number) { value[offset] = (byte)(number >> 8); value[offset + 1] = (byte)number; }
        private static void WriteUInt32(byte[] value, int offset, uint number) { value[offset] = (byte)(number >> 24); value[offset + 1] = (byte)(number >> 16); value[offset + 2] = (byte)(number >> 8); value[offset + 3] = (byte)number; }
        private static void WriteUInt64(byte[] value, int offset, ulong number) { for (var i = 7; i >= 0; i--) value[offset + 7 - i] = (byte)(number >> (i * 8)); }
    }

    internal static class CdpConnection
    {
        internal static async Task<CdpPacket> ReadAsync(SocketConnection connection, CancellationToken cancellationToken)
        {
            var baseHeader = await connection.ReadRawAsync(40, cancellationToken);
            var bytes = new MemoryStream(); bytes.Write(baseHeader, 0, baseHeader.Length);
            while (true)
            {
                var pair = await connection.ReadRawAsync(2, cancellationToken); bytes.Write(pair, 0, pair.Length);
                if (pair[0] == 0) { if (pair[1] != 0) throw new InvalidDataException("CDP end-of-header marker is invalid."); break; }
                if (pair[1] > 0) { var value = await connection.ReadRawAsync(pair[1], cancellationToken); bytes.Write(value, 0, value.Length); }
            }
            var header = CdpHeader.Parse(bytes.ToArray());
            if (header.MessageLength < bytes.Length) throw new InvalidDataException("CDP message length is invalid.");
            var payload = await connection.ReadRawAsync(checked((int)(header.MessageLength - bytes.Length)), cancellationToken);
            return new CdpPacket(header, payload);
        }

        internal static async Task WriteAsync(SocketConnection connection, CdpHeader header, byte[] payload, CancellationToken cancellationToken)
        {
            header.MessageLength = checked((ushort)(header.HeaderSize + (payload == null ? 0 : payload.Length)));
            var output = new byte[header.MessageLength]; var headerBytes = header.Serialize(); Buffer.BlockCopy(headerBytes, 0, output, 0, headerBytes.Length); if (payload != null) Buffer.BlockCopy(payload, 0, output, headerBytes.Length, payload.Length);
            await connection.WriteRawAsync(output, cancellationToken);
        }
    }
}
