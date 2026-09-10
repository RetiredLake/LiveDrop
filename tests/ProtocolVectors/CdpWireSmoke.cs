using System;
using System.Threading;
using System.Threading.Tasks;
using LiveDrop.Protocols.Nearby;

namespace LiveDrop.Transports
{
    internal sealed class SocketConnection
    {
        internal Task<byte[]> ReadRawAsync(int length, CancellationToken cancellationToken) { throw new NotSupportedException(); }
        internal Task WriteRawAsync(byte[] data, CancellationToken cancellationToken) { throw new NotSupportedException(); }
    }
}

internal static class CdpWireSmoke
{
    private static void Main()
    {
        var secret = new byte[64];
        for (var i = 0; i < secret.Length; i++) secret[i] = (byte)i;
        var crypto = new CdpCrypto(secret);
        CheckRoundTrip(crypto, new byte[] { 1, 2, 3, 4, 5 });
        CheckRoundTrip(crypto, new byte[12]);

        var header = NewHeader();
        var packet = crypto.Encrypt(header, new byte[] { 7, 8, 9 });
        var parsedHeader = CdpHeader.Parse(Copy(packet, 0, header.HeaderSize));
        var encryptedPayload = Copy(packet, header.HeaderSize, packet.Length - header.HeaderSize);
        var restored = crypto.Decrypt(new CdpPacket(parsedHeader, encryptedPayload));
        if (restored.Length != 3 || restored[0] != 7 || restored[1] != 8 || restored[2] != 9)
            throw new InvalidOperationException("CDP packet parse vector mismatch.");
        encryptedPayload[encryptedPayload.Length - 1] ^= 1;
        try
        {
            crypto.Decrypt(new CdpPacket(parsedHeader, encryptedPayload));
            throw new InvalidOperationException("CDP HMAC tamper vector was accepted.");
        }
        catch (System.IO.InvalidDataException) { }
        Console.WriteLine("CDP header, encryption, and HMAC smoke vectors passed.");
    }

    private static void CheckRoundTrip(CdpCrypto crypto, byte[] input)
    {
        var header = NewHeader();
        var packet = crypto.Encrypt(header, input);
        var parsedHeader = CdpHeader.Parse(Copy(packet, 0, header.HeaderSize));
        var output = crypto.Decrypt(new CdpPacket(parsedHeader, Copy(packet, header.HeaderSize, packet.Length - header.HeaderSize)));
        if (input.Length != output.Length) throw new InvalidOperationException("CDP encryption length vector mismatch.");
        for (var i = 0; i < input.Length; i++) if (input[i] != output[i]) throw new InvalidOperationException("CDP encryption round-trip mismatch.");
    }

    private static CdpHeader NewHeader()
    {
        var header = new CdpHeader { Type = CdpHeader.Session, SessionId = 0x0102030405060708UL, ChannelId = 9, SequenceNumber = 4, FragmentCount = 1 };
        header.AdditionalHeaders.Add(new CdpAdditionalHeader(2, new byte[] { (byte)'A', (byte)'.', (byte)'1' }));
        return header;
    }

    private static byte[] Copy(byte[] source, int offset, int length)
    {
        var result = new byte[length]; Buffer.BlockCopy(source, offset, result, 0, length); return result;
    }
}
