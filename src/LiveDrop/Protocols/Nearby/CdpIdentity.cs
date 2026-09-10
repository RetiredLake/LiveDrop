using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using LiveDrop.Protocols.QuickShare;

namespace LiveDrop.Protocols.Nearby
{
    // CDP carries a self-signed P-256 certificate during device authentication.
    // The certificate is generated in memory so the RTM target does not depend on
    // newer X509Certificate2 construction or certificate-enrollment APIs.
    internal sealed class CdpIdentity
    {
        private readonly P256KeyAgreement _key;
        internal byte[] Certificate { get; private set; }

        private CdpIdentity(P256KeyAgreement key, byte[] certificate)
        {
            _key = key;
            Certificate = certificate;
        }

        internal static CdpIdentity Create(string displayName)
        {
            var key = P256KeyAgreement.Create();
            var coordinates = key.ExportPublicCoordinates();
            var commonName = string.IsNullOrWhiteSpace(displayName) ? "LiveDrop" : displayName;
            var signatureAlgorithm = Sequence(Oid(new[] { 1, 2, 840, 10045, 4, 3, 2 }));
            var publicKeyAlgorithm = Sequence(
                Oid(new[] { 1, 2, 840, 10045, 2, 1 }),
                Oid(new[] { 1, 2, 840, 10045, 3, 1, 7 }));
            var publicKey = new byte[65];
            publicKey[0] = 4;
            Buffer.BlockCopy(coordinates[0], 0, publicKey, 1, 32);
            Buffer.BlockCopy(coordinates[1], 0, publicKey, 33, 32);
            var subjectPublicKeyInfo = Sequence(publicKeyAlgorithm, BitString(publicKey));
            var name = Name(commonName);
            var validity = Sequence(
                UtcTime(DateTime.UtcNow.AddDays(-1)),
                UtcTime(DateTime.UtcNow.AddYears(10)));
            var serial = new byte[16];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(serial);
            serial[0] &= 0x7F;
            var tbs = Sequence(
                Explicit(0, Integer(new byte[] { 2 })),
                Integer(serial),
                signatureAlgorithm,
                name,
                validity,
                name,
                subjectPublicKeyInfo);
            var certificateSignature = key.SignSha256(tbs);
            var certificate = Sequence(tbs, signatureAlgorithm, BitString(certificateSignature));
            return new CdpIdentity(key, certificate);
        }

        internal byte[] SignAuthentication(ulong hostNonce, ulong clientNonce)
        {
            var input = new byte[16 + Certificate.Length];
            WriteUInt64LittleEndian(input, 0, hostNonce);
            WriteUInt64LittleEndian(input, 8, clientNonce);
            Buffer.BlockCopy(Certificate, 0, input, 16, Certificate.Length);
            return _key.SignSha256Raw(input);
        }

        internal static bool VerifyAuthentication(byte[] certificate, byte[] signature, ulong hostNonce, ulong clientNonce)
        {
            byte[] publicX;
            byte[] publicY;
            byte[] tbs;
            if (!TryParseCertificate(certificate, out publicX, out publicY, out tbs)) return false;
            if (!P256KeyAgreement.VerifySha256(tbs, ExtractCertificateSignature(certificate), publicX, publicY)) return false;
            var input = new byte[16 + certificate.Length];
            WriteUInt64LittleEndian(input, 0, hostNonce);
            WriteUInt64LittleEndian(input, 8, clientNonce);
            Buffer.BlockCopy(certificate, 0, input, 16, certificate.Length);
            return P256KeyAgreement.VerifySha256Raw(input, signature, publicX, publicY);
        }

        internal static bool VerifyCertificate(byte[] certificate)
        {
            byte[] publicX; byte[] publicY; byte[] tbs;
            return TryParseCertificate(certificate, out publicX, out publicY, out tbs)
                && P256KeyAgreement.VerifySha256(tbs, ExtractCertificateSignature(certificate), publicX, publicY);
        }

        private static byte[] ExtractCertificateSignature(byte[] certificate)
        {
            try
            {
                var cursor = 0; byte tag; byte[] outer; byte[] content; byte[] encoded;
                if (!ReadElement(certificate, ref cursor, out tag, out outer, out encoded) || tag != 0x30) return new byte[0];
                var inner = 0;
                if (!ReadElement(outer, ref inner, out tag, out content, out encoded)) return new byte[0];
                byte[] algorithm;
                if (!ReadElement(outer, ref inner, out tag, out algorithm, out encoded)) return new byte[0];
                byte[] bits;
                if (!ReadElement(outer, ref inner, out tag, out bits, out encoded) || tag != 0x03 || bits.Length < 1 || bits[0] != 0) return new byte[0];
                var result = new byte[bits.Length - 1]; Buffer.BlockCopy(bits, 1, result, 0, result.Length); return result;
            }
            catch { return new byte[0]; }
        }

        private static bool TryParseCertificate(byte[] certificate, out byte[] publicX, out byte[] publicY, out byte[] tbs)
        {
            publicX = null; publicY = null; tbs = null;
            try
            {
                var cursor = 0; byte tag; byte[] outer; byte[] tbsContent; byte[] tbsEncoded;
                if (!ReadElement(certificate, ref cursor, out tag, out outer, out tbsEncoded) || tag != 0x30) return false;
                var inner = 0;
                if (!ReadElement(outer, ref inner, out tag, out tbsContent, out tbsEncoded) || tag != 0x30) return false;
                tbs = tbsEncoded;
                var tbsCursor = 0; byte[] value; byte[] ignored;
                if (!ReadElement(tbsContent, ref tbsCursor, out tag, out value, out ignored) || tag != 0xA0) return false;
                if (!ReadElement(tbsContent, ref tbsCursor, out tag, out value, out ignored) || tag != 0x02) return false;
                if (!ReadElement(tbsContent, ref tbsCursor, out tag, out value, out ignored) || tag != 0x30) return false;
                if (!ReadElement(tbsContent, ref tbsCursor, out tag, out value, out ignored) || tag != 0x30) return false;
                if (!ReadElement(tbsContent, ref tbsCursor, out tag, out value, out ignored) || tag != 0x30) return false;
                if (!ReadElement(tbsContent, ref tbsCursor, out tag, out value, out ignored) || tag != 0x30) return false;
                if (!ReadElement(tbsContent, ref tbsCursor, out tag, out value, out ignored) || tag != 0x30) return false;
                var spkiContent = value;
                var spkiCursor = 0;
                if (!ReadElement(spkiContent, ref spkiCursor, out tag, out value, out ignored) || tag != 0x30) return false;
                byte[] keyBits;
                if (!ReadElement(spkiContent, ref spkiCursor, out tag, out keyBits, out ignored) || tag != 0x03 || keyBits.Length != 66 || keyBits[0] != 0 || keyBits[1] != 4) return false;
                publicX = new byte[32]; publicY = new byte[32];
                Buffer.BlockCopy(keyBits, 2, publicX, 0, 32); Buffer.BlockCopy(keyBits, 34, publicY, 0, 32);
                return true;
            }
            catch { return false; }
        }

        private static bool ReadElement(byte[] data, ref int offset, out byte tag, out byte[] content, out byte[] encoded)
        {
            tag = 0; content = null; encoded = null;
            if (data == null || offset >= data.Length) return false;
            var start = offset; tag = data[offset++];
            int length;
            if (!ReadLength(data, ref offset, out length) || length < 0 || offset + length > data.Length) return false;
            content = new byte[length]; Buffer.BlockCopy(data, offset, content, 0, length); offset += length;
            encoded = new byte[offset - start]; Buffer.BlockCopy(data, start, encoded, 0, encoded.Length); return true;
        }

        private static bool ReadLength(byte[] data, ref int offset, out int length)
        {
            length = 0; if (offset >= data.Length) return false;
            var first = data[offset++];
            if ((first & 0x80) == 0) { length = first; return true; }
            var count = first & 0x7F; if (count == 0 || count > 3 || offset + count > data.Length) return false;
            for (var i = 0; i < count; i++) length = (length << 8) | data[offset++];
            return true;
        }

        private static byte[] Sequence(params byte[][] values) { return Wrap(0x30, Combine(values)); }
        private static byte[] Explicit(int tag, byte[] value) { return Wrap((byte)(0xA0 | tag), value); }
        private static byte[] Integer(byte[] value) { return Wrap(0x02, value); }
        private static byte[] BitString(byte[] value) { var data = new byte[value.Length + 1]; Buffer.BlockCopy(value, 0, data, 1, value.Length); return Wrap(0x03, data); }
        private static byte[] UtcTime(DateTime value) { return Wrap(0x17, Encoding.ASCII.GetBytes(value.ToUniversalTime().ToString("yyMMddHHmmss'Z'", CultureInfo.InvariantCulture))); }
        private static byte[] Name(string commonName)
        {
            return Sequence(Wrap(0x31, Sequence(Oid(new[] { 2, 5, 4, 3 }), Wrap(0x0C, Encoding.UTF8.GetBytes(commonName)))));
        }

        private static byte[] Oid(int[] values)
        {
            var body = new MemoryStream(); body.WriteByte((byte)(values[0] * 40 + values[1]));
            for (var i = 2; i < values.Length; i++)
            {
                var value = values[i]; var count = 1; var temp = value;
                while ((temp >>= 7) > 0) count++;
                for (var part = count - 1; part >= 0; part--) body.WriteByte((byte)(((value >> (7 * part)) & 0x7F) | (part == 0 ? 0 : 0x80)));
            }
            return Wrap(0x06, body.ToArray());
        }

        private static byte[] Wrap(byte tag, byte[] value)
        {
            var length = EncodeLength(value.Length); var output = new byte[1 + length.Length + value.Length];
            output[0] = tag; Buffer.BlockCopy(length, 0, output, 1, length.Length); Buffer.BlockCopy(value, 0, output, 1 + length.Length, value.Length); return output;
        }

        private static byte[] EncodeLength(int value)
        {
            if (value < 128) return new[] { (byte)value };
            if (value < 256) return new[] { (byte)0x81, (byte)value };
            return new[] { (byte)0x82, (byte)(value >> 8), (byte)value };
        }

        private static byte[] Combine(byte[][] values)
        {
            var length = 0; foreach (var value in values) length += value == null ? 0 : value.Length;
            var output = new byte[length]; var offset = 0;
            foreach (var value in values) { if (value == null) continue; Buffer.BlockCopy(value, 0, output, offset, value.Length); offset += value.Length; }
            return output;
        }

        private static void WriteUInt64LittleEndian(byte[] target, int offset, ulong value)
        {
            for (var i = 0; i < 8; i++) target[offset + i] = (byte)(value >> (i * 8));
        }
    }
}
