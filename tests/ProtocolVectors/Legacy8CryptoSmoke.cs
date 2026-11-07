using System;
using System.Linq;
using System.Text;
using System.Security.Cryptography;

// Compile with LiveDrop8's LegacyCryptography.cs, not the framework crypto
// implementation. Known vectors ensure the adapter is interoperable.
internal static class Legacy8CryptoSmoke
{
    private static byte[] Hex(string value) { return Enumerable.Range(0, value.Length / 2).Select(i => Convert.ToByte(value.Substring(i * 2, 2), 16)).ToArray(); }
    private static void Equal(byte[] actual, string expected)
    { if (!actual.SequenceEqual(Hex(expected))) throw new Exception("Cryptography vector failed."); }
    private static void Main()
    {
        using (var sha = SHA256.Create()) Equal(sha.ComputeHash(Encoding.UTF8.GetBytes("abc")), "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        using (var sha = SHA512.Create()) Equal(sha.ComputeHash(Encoding.UTF8.GetBytes("abc")), "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f");
        using (var hmac = new HMACSHA256(Enumerable.Repeat((byte)0x0b, 20).ToArray())) Equal(hmac.ComputeHash(Encoding.UTF8.GetBytes("Hi There")), "b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7");
        using (var aes = Aes.Create())
        {
            aes.KeySize = 128; aes.Key = Hex("2b7e151628aed2a6abf7158809cf4f3c");
            aes.IV = Hex("000102030405060708090a0b0c0d0e0f"); aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
            var plain = Hex("6bc1bee22e409f96e93d7e117393172a");
            using (var encrypt = aes.CreateEncryptor()) Equal(encrypt.TransformFinalBlock(plain, 0, plain.Length), "7649abac8119b246cee98e9b12e9197d");
            var cipher = Hex("7649abac8119b246cee98e9b12e9197d");
            using (var decrypt = aes.CreateDecryptor()) Equal(decrypt.TransformFinalBlock(cipher, 0, cipher.Length), "6bc1bee22e409f96e93d7e117393172a");
            aes.Mode = CipherMode.ECB;
            using (var encrypt = aes.CreateEncryptor()) Equal(encrypt.TransformFinalBlock(plain, 0, plain.Length), "3ad77bb40d7a3660a89ecaf32466ef97");
        }
        Console.WriteLine("PASS: 8.1 Windows crypto SHA-256/SHA-512/HMAC-SHA256/AES-CBC/AES-ECB known vectors.");
    }
}
