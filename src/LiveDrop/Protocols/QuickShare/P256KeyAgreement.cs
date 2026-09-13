using System;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace LiveDrop.Protocols.QuickShare
{
    // Small RTM-compatible P-256 implementation used only for the UKEY2 ephemeral key.
    // It avoids bringing a modern .NET cryptography package into the classic UWP target.
    internal sealed class P256KeyAgreement
    {
        private static readonly BigInteger P = FromHex("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
        private static readonly BigInteger A = P - 3;
        private static readonly BigInteger N = FromHex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
        private static readonly Point G = new Point(
            FromHex("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296"),
            FromHex("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5"));

        private readonly BigInteger _private;
        private readonly Point _public;

        private P256KeyAgreement(BigInteger privateValue)
        {
            _private = privateValue;
            _public = Multiply(G, privateValue);
        }

        internal static P256KeyAgreement Create()
        {
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create())
            {
                do
                {
                    random.GetBytes(bytes);
                } while (FromBigEndian(bytes) <= 0 || FromBigEndian(bytes) >= N);
            }
            return new P256KeyAgreement(FromBigEndian(bytes));
        }

        internal byte[] ExportGenericPublicKey()
        {
            var coordinates = new ProtoWriter();
            // Quick Share's EcP256PublicKey fields are positive
            // big-endian two's-complement integers. A coordinate with the
            // high bit set needs a leading zero or Android interprets it as
            // negative and rejects the UKEY2 initiation.
            coordinates.WriteBytes(1, ToSignedBigEndian(_public.X));
            coordinates.WriteBytes(2, ToSignedBigEndian(_public.Y));
            var key = new ProtoWriter();
            key.WriteEnum(1, 1);
            key.WriteMessage(2, coordinates.ToArray());
            return key.ToArray();
        }

        internal byte[][] ExportPublicCoordinates()
        {
            return new[] { ToBigEndian(_public.X, 32), ToBigEndian(_public.Y, 32) };
        }

        internal byte[] ComputeSharedSecretHash(byte[] genericPublicKey)
        {
            var peer = ParseGenericPublicKey(genericPublicKey);
            if (peer.Infinity || !IsOnCurve(peer)) throw new InvalidOperationException("The peer P-256 key is invalid.");
            var shared = Multiply(peer, _private);
            if (shared.Infinity) throw new InvalidOperationException("The P-256 shared point is invalid.");
            using (var sha = SHA256.Create()) return sha.ComputeHash(ToBigEndian(shared.X, 32));
        }

        internal byte[] ComputeCdpSharedSecret(byte[] publicX, byte[] publicY)
        {
            var peer = new Point(FromBigEndian(publicX), FromBigEndian(publicY));
            if (peer.Infinity || !IsOnCurve(peer)) throw new InvalidOperationException("The peer P-256 key is invalid.");
            var shared = Multiply(peer, _private);
            if (shared.Infinity) throw new InvalidOperationException("The P-256 shared point is invalid.");
            var data = new byte[8 + 32 + 8];
            var prepend = new byte[] { 0xD6, 0x37, 0xF1, 0xAA, 0xE2, 0xF0, 0x41, 0x8C };
            var append = new byte[] { 0xA8, 0xF8, 0x1A, 0x57, 0x4E, 0x22, 0x8A, 0xB7 };
            Buffer.BlockCopy(prepend, 0, data, 0, prepend.Length);
            Buffer.BlockCopy(ToBigEndian(shared.X, 32), 0, data, prepend.Length, 32);
            Buffer.BlockCopy(append, 0, data, prepend.Length + 32, append.Length);
            using (var sha = SHA512.Create()) return sha.ComputeHash(data);
        }

        internal byte[] SignSha256(byte[] data)
        {
            using (var sha = SHA256.Create()) return EncodeSignature(SignHash(sha.ComputeHash(data ?? new byte[0])));
        }

        internal byte[] SignSha256Raw(byte[] data)
        {
            Signature signature;
            using (var sha = SHA256.Create()) signature = SignHash(sha.ComputeHash(data ?? new byte[0]));
            var result = new byte[64];
            Buffer.BlockCopy(ToBigEndian(signature.R, 32), 0, result, 0, 32);
            Buffer.BlockCopy(ToBigEndian(signature.S, 32), 0, result, 32, 32);
            return result;
        }

        internal static bool VerifySha256(byte[] data, byte[] signature, byte[] publicX, byte[] publicY)
        {
            using (var sha = SHA256.Create()) return VerifyHash(sha.ComputeHash(data ?? new byte[0]), signature, publicX, publicY);
        }

        internal static bool VerifySha256Raw(byte[] data, byte[] signature, byte[] publicX, byte[] publicY)
        {
            if (signature == null || signature.Length != 64) return false;
            var rawR = new byte[32]; var rawS = new byte[32];
            Buffer.BlockCopy(signature, 0, rawR, 0, 32); Buffer.BlockCopy(signature, 32, rawS, 0, 32);
            using (var sha = SHA256.Create()) return VerifyBigSignature(sha.ComputeHash(data ?? new byte[0]), new Signature(FromBigEndian(rawR), FromBigEndian(rawS)), publicX, publicY);
        }

        private Signature SignHash(byte[] hash)
        {
            var digest = FromBigEndian(hash);
            var random = new byte[32];
            using (var generator = RandomNumberGenerator.Create())
            {
                while (true)
                {
                    generator.GetBytes(random);
                    var k = FromBigEndian(random);
                    if (k <= 0 || k >= N) continue;
                    var point = Multiply(G, k);
                    var r = Mod(point.X, N);
                    if (r == 0) continue;
                    var s = Mod(Inverse(k, N) * Mod(digest + r * _private, N), N);
                    if (s == 0) continue;
                    if (s > N / 2) s = N - s;
                    return new Signature(r, s);
                }
            }
        }

        private static bool VerifyHash(byte[] hash, byte[] signature, byte[] publicX, byte[] publicY)
        {
            Signature parsed;
            if (!TryDecodeSignature(signature, out parsed)) return false;
            return VerifyBigSignature(hash, parsed, publicX, publicY);
        }

        private static bool VerifyBigSignature(byte[] hash, Signature parsed, byte[] publicX, byte[] publicY)
        {
            if (parsed.R <= 0 || parsed.R >= N || parsed.S <= 0 || parsed.S >= N) return false;
            var point = new Point(FromBigEndian(publicX), FromBigEndian(publicY));
            if (point.Infinity || !IsOnCurve(point)) return false;
            var digest = FromBigEndian(hash);
            var inverse = Inverse(parsed.S, N);
            var first = Mod(digest * inverse, N);
            var second = Mod(parsed.R * inverse, N);
            var sum = Add(Multiply(G, first), Multiply(point, second));
            return !sum.Infinity && Mod(sum.X, N) == parsed.R;
        }

        private static byte[] EncodeSignature(Signature signature)
        {
            var r = EncodeInteger(signature.R);
            var s = EncodeInteger(signature.S);
            var body = new byte[r.Length + s.Length];
            Buffer.BlockCopy(r, 0, body, 0, r.Length);
            Buffer.BlockCopy(s, 0, body, r.Length, s.Length);
            return WrapDer(0x30, body);
        }

        private static bool TryDecodeSignature(byte[] data, out Signature signature)
        {
            signature = default(Signature);
            if (data == null || data.Length < 8 || data[0] != 0x30) return false;
            var offset = 1;
            int length;
            if (!ReadDerLength(data, ref offset, out length) || length != data.Length - offset) return false;
            BigInteger r; BigInteger s;
            if (!ReadDerInteger(data, ref offset, out r) || !ReadDerInteger(data, ref offset, out s) || offset != data.Length) return false;
            signature = new Signature(r, s);
            return true;
        }

        private static bool ReadDerInteger(byte[] data, ref int offset, out BigInteger value)
        {
            value = 0;
            if (offset >= data.Length || data[offset++] != 0x02) return false;
            int length;
            if (!ReadDerLength(data, ref offset, out length) || length <= 0 || offset + length > data.Length) return false;
            var bytes = new byte[length]; Buffer.BlockCopy(data, offset, bytes, 0, length); offset += length;
            value = FromBigEndian(bytes);
            return true;
        }

        private static byte[] EncodeInteger(BigInteger value)
        {
            var raw = ToBigEndian(value, 32);
            var first = 0;
            while (first < raw.Length - 1 && raw[first] == 0) first++;
            var length = raw.Length - first;
            var needsZero = (raw[first] & 0x80) != 0;
            var content = new byte[length + (needsZero ? 1 : 0)];
            Buffer.BlockCopy(raw, first, content, needsZero ? 1 : 0, length);
            return WrapDer(0x02, content);
        }

        private static byte[] WrapDer(byte tag, byte[] content)
        {
            var length = EncodeDerLength(content.Length);
            var result = new byte[1 + length.Length + content.Length];
            result[0] = tag; Buffer.BlockCopy(length, 0, result, 1, length.Length); Buffer.BlockCopy(content, 0, result, 1 + length.Length, content.Length);
            return result;
        }

        private static byte[] EncodeDerLength(int length)
        {
            if (length < 128) return new[] { (byte)length };
            if (length < 256) return new[] { (byte)0x81, (byte)length };
            return new[] { (byte)0x82, (byte)(length >> 8), (byte)length };
        }

        private static bool ReadDerLength(byte[] data, ref int offset, out int length)
        {
            length = 0;
            if (offset >= data.Length) return false;
            var first = data[offset++];
            if ((first & 0x80) == 0) { length = first; return true; }
            var count = first & 0x7F;
            if (count == 0 || count > 2 || offset + count > data.Length) return false;
            for (var i = 0; i < count; i++) length = (length << 8) | data[offset++];
            return true;
        }

        private static Point ParseGenericPublicKey(byte[] genericPublicKey)
        {
            var outer = new ProtoReader(genericPublicKey);
            var coordinateData = new byte[0];
            while (!outer.End)
            {
                var tag = outer.ReadTag();
                if ((tag >> 3) == 2 && (tag & 7) == 2) coordinateData = outer.ReadBytes();
                else outer.Skip(tag & 7);
            }
            var coordinates = new ProtoReader(coordinateData);
            var x = new byte[0];
            var y = new byte[0];
            while (!coordinates.End)
            {
                var tag = coordinates.ReadTag();
                if ((tag >> 3) == 1 && (tag & 7) == 2) x = coordinates.ReadBytes();
                else if ((tag >> 3) == 2 && (tag & 7) == 2) y = coordinates.ReadBytes();
                else coordinates.Skip(tag & 7);
            }
            if (x.Length == 0 || y.Length == 0) throw new InvalidOperationException("The UKEY2 public key is missing coordinates.");
            return new Point(FromBigEndian(x), FromBigEndian(y));
        }

        private static Point Multiply(Point point, BigInteger scalar)
        {
            var result = Point.InfinityPoint;
            var addend = point;
            var value = scalar;
            while (value > 0)
            {
                if (!value.IsEven) result = Add(result, addend);
                addend = Add(addend, addend);
                value >>= 1;
            }
            return result;
        }

        private static Point Add(Point left, Point right)
        {
            if (left.Infinity) return right;
            if (right.Infinity) return left;
            if (left.X == right.X)
            {
                if (Mod(left.Y + right.Y, P) == 0) return Point.InfinityPoint;
                var slope = Mod((3 * left.X * left.X + A) * Inverse(2 * left.Y), P);
                var xDouble = Mod(slope * slope - 2 * left.X, P);
                return new Point(xDouble, Mod(slope * (left.X - xDouble) - left.Y, P));
            }
            var lambda = Mod((right.Y - left.Y) * Inverse(right.X - left.X), P);
            var x = Mod(lambda * lambda - left.X - right.X, P);
            return new Point(x, Mod(lambda * (left.X - x) - left.Y, P));
        }

        private static bool IsOnCurve(Point point)
        {
            return Mod(point.Y * point.Y - (point.X * point.X * point.X + A * point.X + FromHex("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B")), P) == 0;
        }

        private static BigInteger Inverse(BigInteger value) { return Inverse(value, P); }
        private static BigInteger Inverse(BigInteger value, BigInteger modulus) { return BigInteger.ModPow(Mod(value, modulus), modulus - 2, modulus); }
        private static BigInteger Mod(BigInteger value, BigInteger modulus) { var result = value % modulus; return result.Sign < 0 ? result + modulus : result; }

        private static BigInteger FromHex(string value) { return BigInteger.Parse("00" + value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture); }

        private static BigInteger FromBigEndian(byte[] value)
        {
            var little = new byte[value.Length + 1];
            for (var i = 0; i < value.Length; i++) little[i] = value[value.Length - i - 1];
            return new BigInteger(little);
        }

        private static byte[] ToBigEndian(BigInteger value, int length)
        {
            var result = new byte[length];
            var little = value.ToByteArray();
            for (var i = 0; i < length && i < little.Length; i++) result[length - i - 1] = little[i];
            return result;
        }

        private static byte[] ToSignedBigEndian(BigInteger value)
        {
            var unsigned = ToBigEndian(value, 32);
            if ((unsigned[0] & 0x80) == 0) return unsigned;
            var signed = new byte[33];
            Buffer.BlockCopy(unsigned, 0, signed, 1, unsigned.Length);
            return signed;
        }

        private struct Point
        {
            internal readonly BigInteger X;
            internal readonly BigInteger Y;
            internal readonly bool Infinity;
            internal Point(BigInteger x, BigInteger y) { X = x; Y = y; Infinity = false; }
            private Point(bool infinity) { X = 0; Y = 0; Infinity = infinity; }
            internal static Point InfinityPoint { get { return new Point(true); } }
        }

        private struct Signature
        {
            internal readonly BigInteger R;
            internal readonly BigInteger S;
            internal Signature(BigInteger r, BigInteger s) { R = r; S = s; }
        }
    }
}
