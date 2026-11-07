using System;
using System.IO;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

// Narrow adapter for the existing engine's .NET crypto calls. All algorithms
// are provided by Windows cryptography, not a new managed implementation.
namespace System.Security.Cryptography
{
    internal sealed class RandomNumberGenerator : IDisposable
    {
        public static RandomNumberGenerator Create() { return new RandomNumberGenerator(); }
        public void GetBytes(byte[] bytes)
        {
            byte[] random;
            CryptographicBuffer.CopyToByteArray(CryptographicBuffer.GenerateRandom((uint)bytes.Length), out random);
            Buffer.BlockCopy(random, 0, bytes, 0, bytes.Length);
        }
        public void Dispose() { }
    }
    internal class LegacyHash : IDisposable
    {
        private readonly HashAlgorithmProvider _provider;
        protected LegacyHash(string algorithm) { _provider = HashAlgorithmProvider.OpenAlgorithm(algorithm); }
        public byte[] ComputeHash(byte[] bytes)
        {
            byte[] result;
            CryptographicBuffer.CopyToByteArray(_provider.HashData(CryptographicBuffer.CreateFromByteArray(bytes)), out result);
            return result;
        }
        public byte[] ComputeHash(Stream stream)
        {
            var hash = _provider.CreateHash();
            var buffer = new byte[65536];
            int count;
            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                var chunk = new byte[count]; Buffer.BlockCopy(buffer, 0, chunk, 0, count);
                hash.Append(CryptographicBuffer.CreateFromByteArray(chunk));
            }
            byte[] result; CryptographicBuffer.CopyToByteArray(hash.GetValueAndReset(), out result); return result;
        }
        public void Dispose() { }
    }
    internal sealed class SHA256 : LegacyHash
    {
        private SHA256() : base(HashAlgorithmNames.Sha256) { }
        public static SHA256 Create() { return new SHA256(); }
    }
    internal sealed class SHA512 : LegacyHash
    {
        private SHA512() : base(HashAlgorithmNames.Sha512) { }
        public static SHA512 Create() { return new SHA512(); }
    }
    internal sealed class HMACSHA256 : IDisposable
    {
        private readonly CryptographicKey _key;
        public HMACSHA256(byte[] key) { _key = MacAlgorithmProvider.OpenAlgorithm(MacAlgorithmNames.HmacSha256).CreateKey(CryptographicBuffer.CreateFromByteArray(key)); }
        public byte[] ComputeHash(byte[] bytes)
        {
            byte[] result;
            CryptographicBuffer.CopyToByteArray(CryptographicEngine.Sign(_key, CryptographicBuffer.CreateFromByteArray(bytes)), out result);
            return result;
        }
        public void Dispose() { }
    }
    internal enum CipherMode { CBC, ECB }
    internal enum PaddingMode { None, PKCS7 }
    internal sealed class Aes : IDisposable
    {
        public int KeySize { get; set; }
        public byte[] Key { get; set; }
        public byte[] IV { get; set; }
        public CipherMode Mode { get; set; }
        public PaddingMode Padding { get; set; }
        public static Aes Create() { return new Aes(); }
        public LegacyTransform CreateEncryptor() { return new LegacyTransform(this, true); }
        public LegacyTransform CreateDecryptor() { return new LegacyTransform(this, false); }
        public void Dispose() { }
    }
    internal sealed class LegacyTransform : IDisposable
    {
        private readonly Aes _aes;
        private readonly bool _encrypt;
        internal LegacyTransform(Aes aes, bool encrypt) { _aes = aes; _encrypt = encrypt; }
        public byte[] TransformFinalBlock(byte[] input, int offset, int count)
        {
            var algorithm = _aes.Mode == CipherMode.ECB
                ? (_aes.Padding == PaddingMode.PKCS7 ? SymmetricAlgorithmNames.AesEcbPkcs7 : SymmetricAlgorithmNames.AesEcb)
                : (_aes.Padding == PaddingMode.PKCS7 ? SymmetricAlgorithmNames.AesCbcPkcs7 : SymmetricAlgorithmNames.AesCbc);
            var key = SymmetricKeyAlgorithmProvider.OpenAlgorithm(algorithm).CreateSymmetricKey(CryptographicBuffer.CreateFromByteArray(_aes.Key));
            var bytes = new byte[count]; Buffer.BlockCopy(input, offset, bytes, 0, count);
            var data = CryptographicBuffer.CreateFromByteArray(bytes);
            var iv = _aes.Mode == CipherMode.ECB ? null : CryptographicBuffer.CreateFromByteArray(_aes.IV);
            byte[] result;
            CryptographicBuffer.CopyToByteArray(_encrypt ? CryptographicEngine.Encrypt(key, data, iv) : CryptographicEngine.Decrypt(key, data, iv), out result);
            return result;
        }
        public void Dispose() { }
    }
}
