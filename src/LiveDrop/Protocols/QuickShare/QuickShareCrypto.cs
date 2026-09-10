using System;
using System.Security.Cryptography;
using System.Text;

namespace LiveDrop.Protocols.QuickShare
{
    internal sealed class QuickShareCrypto
    {
        private static readonly byte[] D2dSalt =
        {
            0x82, 0xAA, 0x55, 0xA0, 0xD3, 0x97, 0xF8, 0x83, 0x46, 0xCA, 0x1C, 0xEE, 0x8D, 0x39, 0x09, 0xB9,
            0x5F, 0x13, 0xFA, 0x7D, 0xEB, 0x1D, 0x4A, 0xB3, 0x83, 0x76, 0xB8, 0x25, 0x6D, 0xA8, 0x55, 0x10
        };

        private readonly byte[] _sendEncryption;
        private readonly byte[] _receiveEncryption;
        private readonly byte[] _sendHmac;
        private readonly byte[] _receiveHmac;
        private int _sendSequence;
        private int _receiveSequence;

        private QuickShareCrypto(byte[] sendEncryption, byte[] receiveEncryption, byte[] sendHmac, byte[] receiveHmac)
        {
            _sendEncryption = sendEncryption;
            _receiveEncryption = receiveEncryption;
            _sendHmac = sendHmac;
            _receiveHmac = receiveHmac;
        }

        internal static QuickShareCrypto Create(byte[] sharedSecretHash, byte[] clientInit, byte[] serverInit, bool server, out byte[] authKey)
        {
            var transcript = Combine(clientInit, serverInit);
            authKey = Hkdf(sharedSecretHash, Encoding.UTF8.GetBytes("UKEY2 v1 auth"), transcript, 32);
            var nextSecret = Hkdf(sharedSecretHash, Encoding.UTF8.GetBytes("UKEY2 v1 next"), transcript, 32);
            var client = Hkdf(nextSecret, D2dSalt, Encoding.UTF8.GetBytes("client"), 32);
            var serverSecret = Hkdf(nextSecret, D2dSalt, Encoding.UTF8.GetBytes("server"), 32);
            using (var sha = SHA256.Create())
            {
                var secureMessageSalt = sha.ComputeHash(Encoding.UTF8.GetBytes("SecureMessage"));
                var clientEncryption = Hkdf(client, secureMessageSalt, Encoding.UTF8.GetBytes("ENC:2"), 32);
                var clientHmac = Hkdf(client, secureMessageSalt, Encoding.UTF8.GetBytes("SIG:1"), 32);
                var serverEncryption = Hkdf(serverSecret, secureMessageSalt, Encoding.UTF8.GetBytes("ENC:2"), 32);
                var serverHmac = Hkdf(serverSecret, secureMessageSalt, Encoding.UTF8.GetBytes("SIG:1"), 32);
                return server
                    ? new QuickShareCrypto(serverEncryption, clientEncryption, serverHmac, clientHmac)
                    : new QuickShareCrypto(clientEncryption, serverEncryption, clientHmac, serverHmac);
            }
        }

        internal string PinCode(byte[] authKey)
        {
            var hash = 0;
            var multiplier = 1;
            foreach (var value in authKey)
            {
                var signed = unchecked((sbyte)value);
                hash = (hash + signed * multiplier) % 9973;
                multiplier = (multiplier * 31) % 9973;
            }
            return Math.Abs(hash).ToString("D4");
        }

        internal byte[] EncryptOffline(byte[] offlineFrame)
        {
            var deviceMessage = new ProtoWriter();
            deviceMessage.WriteBytes(1, offlineFrame);
            deviceMessage.WriteInt32(2, ++_sendSequence);
            var iv = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(iv);
            var encrypted = AesCrypt(deviceMessage.ToArray(), _sendEncryption, iv, true);
            var metadata = new ProtoWriter(); metadata.WriteEnum(1, 13); metadata.WriteInt32(2, 1);
            var header = new ProtoWriter();
            header.WriteEnum(1, 1);
            header.WriteEnum(2, 2);
            header.WriteBytes(5, iv);
            header.WriteMessage(6, metadata.ToArray());
            var headerAndBody = new ProtoWriter(); headerAndBody.WriteMessage(1, header.ToArray()); headerAndBody.WriteBytes(2, encrypted);
            var headerAndBodyBytes = headerAndBody.ToArray();
            var secure = new ProtoWriter(); secure.WriteBytes(1, headerAndBodyBytes); secure.WriteBytes(2, Hmac(headerAndBodyBytes, _sendHmac));
            return secure.ToArray();
        }

        internal byte[] DecryptOffline(byte[] secureMessage)
        {
            var secureReader = new ProtoReader(secureMessage);
            var headerAndBodyBytes = new byte[0];
            var signature = new byte[0];
            while (!secureReader.End)
            {
                var tag = secureReader.ReadTag();
                if ((tag >> 3) == 1 && (tag & 7) == 2) headerAndBodyBytes = secureReader.ReadBytes();
                else if ((tag >> 3) == 2 && (tag & 7) == 2) signature = secureReader.ReadBytes();
                else secureReader.Skip(tag & 7);
            }
            if (!FixedEquals(signature, Hmac(headerAndBodyBytes, _receiveHmac))) throw new InvalidOperationException("Quick Share HMAC verification failed.");
            var bodyReader = new ProtoReader(headerAndBodyBytes);
            var header = new byte[0];
            var encryptedBody = new byte[0];
            while (!bodyReader.End)
            {
                var tag = bodyReader.ReadTag();
                if ((tag >> 3) == 1 && (tag & 7) == 2) header = bodyReader.ReadBytes();
                else if ((tag >> 3) == 2 && (tag & 7) == 2) encryptedBody = bodyReader.ReadBytes();
                else bodyReader.Skip(tag & 7);
            }
            var headerReader = new ProtoReader(header);
            var iv = new byte[0];
            while (!headerReader.End)
            {
                var tag = headerReader.ReadTag();
                if ((tag >> 3) == 5 && (tag & 7) == 2) iv = headerReader.ReadBytes();
                else headerReader.Skip(tag & 7);
            }
            var plainDeviceMessage = AesCrypt(encryptedBody, _receiveEncryption, iv, false);
            var deviceReader = new ProtoReader(plainDeviceMessage);
            var message = new byte[0];
            var sequence = 0;
            while (!deviceReader.End)
            {
                var tag = deviceReader.ReadTag();
                if ((tag >> 3) == 1 && (tag & 7) == 2) message = deviceReader.ReadBytes();
                else if ((tag >> 3) == 2 && (tag & 7) == 0) sequence = (int)deviceReader.ReadVarint();
                else deviceReader.Skip(tag & 7);
            }
            if (sequence != ++_receiveSequence) throw new InvalidOperationException("Quick Share sequence number is out of order.");
            return message;
        }

        private static byte[] AesCrypt(byte[] input, byte[] key, byte[] iv, bool encrypt)
        {
            using (var aes = Aes.Create())
            {
                aes.KeySize = 256; aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor()) return transform.TransformFinalBlock(input, 0, input.Length);
            }
        }

        private static byte[] Hmac(byte[] value, byte[] key)
        {
            using (var hmac = new HMACSHA256(key)) return hmac.ComputeHash(value);
        }

        private static byte[] Hkdf(byte[] input, byte[] salt, byte[] info, int length)
        {
            var prk = Hmac(input, salt ?? new byte[0]);
            var output = new byte[length];
            var previous = new byte[0];
            var offset = 0;
            byte counter = 1;
            while (offset < length)
            {
                var data = Combine(previous, info, new[] { counter++ });
                previous = Hmac(data, prk);
                var copy = Math.Min(previous.Length, length - offset);
                Buffer.BlockCopy(previous, 0, output, offset, copy);
                offset += copy;
            }
            return output;
        }

        private static byte[] Combine(params byte[][] values)
        {
            var size = 0; foreach (var value in values) size += value == null ? 0 : value.Length;
            var result = new byte[size]; var offset = 0;
            foreach (var value in values) { if (value == null) continue; Buffer.BlockCopy(value, 0, result, offset, value.Length); offset += value.Length; }
            return result;
        }

        private static bool FixedEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;
            var result = 0; for (var i = 0; i < left.Length; i++) result |= left[i] ^ right[i]; return result == 0;
        }
    }
}
