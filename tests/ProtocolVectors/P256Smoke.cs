using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LiveDrop.Protocols.Nearby;
using LiveDrop.Protocols.QuickShare;

namespace LiveDrop.Models
{
    internal sealed class ShareFileDescriptor
    {
        internal string Name { get; set; }
        internal string MimeType { get; set; }
        internal long Size { get; set; }
    }
}

internal static class P256Smoke
{
    private static void Main()
    {
        var alice = P256KeyAgreement.Create();
        var bob = P256KeyAgreement.Create();
        var aliceSecret = alice.ComputeSharedSecretHash(bob.ExportGenericPublicKey());
        var bobSecret = bob.ComputeSharedSecretHash(alice.ExportGenericPublicKey());
        if (!aliceSecret.SequenceEqual(bobSecret)) throw new InvalidOperationException("P-256 shared secret mismatch.");
        byte[] clientAuth;
        byte[] serverAuth;
        var client = QuickShareCrypto.Create(aliceSecret, new byte[] { 1, 2 }, new byte[] { 3, 4 }, false, out clientAuth);
        var server = QuickShareCrypto.Create(aliceSecret, new byte[] { 1, 2 }, new byte[] { 3, 4 }, true, out serverAuth);
        var frame = QuickShareFrames.BuildBytesPayload(new byte[] { 9, 8, 7 }, 42, false);
        var decrypted = server.DecryptOffline(client.EncryptOffline(frame));
        if (!frame.SequenceEqual(decrypted)) throw new InvalidOperationException("Quick Share encrypted frame mismatch.");
        var endpointInfo = QuickShareFrames.BuildEndpointInfo("LiveDrop", 3);
        if (endpointInfo[0] != 6 || endpointInfo[17] != 8 || Encoding.UTF8.GetString(endpointInfo, 18, 8) != "LiveDrop")
            throw new InvalidOperationException("Quick Share endpoint-info vector mismatch.");
        var controlBody = new byte[] { 1, 2, 3 };
        var controlStart = QuickShareFrames.ParsePayloadChunk(QuickShareFrames.BuildPayloadChunk(43, 1, controlBody.Length, 0, controlBody, false));
        var controlEnd = QuickShareFrames.ParsePayloadChunk(QuickShareFrames.BuildPayloadChunk(43, 1, controlBody.Length, controlBody.Length, new byte[0], true));
        if (controlStart == null || controlEnd == null || controlStart.Offset != 0 || controlEnd.Offset != controlBody.Length ||
            controlEnd.TotalSize != controlBody.Length || !controlEnd.Last)
            throw new InvalidOperationException("Quick Share control-payload marker vector mismatch.");
        var signed = alice.SignSha256(new byte[] { 5, 4, 3 });
        var coordinates = alice.ExportPublicCoordinates();
        if (!P256KeyAgreement.VerifySha256(new byte[] { 5, 4, 3 }, signed, coordinates[0], coordinates[1])) throw new InvalidOperationException("P-256 signature vector mismatch.");
        var identity = CdpIdentity.Create("LiveDrop test");
        if (!CdpIdentity.VerifyCertificate(identity.Certificate)) throw new InvalidOperationException("CDP certificate vector mismatch.");
        var signature = identity.SignAuthentication(0x0102030405060708UL, 0x1112131415161718UL);
        if (!CdpIdentity.VerifyAuthentication(identity.Certificate, signature, 0x0102030405060708UL, 0x1112131415161718UL))
            throw new InvalidOperationException("CDP certificate authentication vector mismatch.");
        var values = new CdpValueSet()
            .AddUInt32("ControlMessage", 6)
            .AddUInt64("BytesToSend", 123456789UL)
            .AddGuid("OperationId", new Guid("00112233-4455-6677-8899-aabbccddeeff"))
            .AddStringArray("FileNames", new List<string> { "one.txt", "two.bin" })
            .AddUInt32Array("ContentIds", new List<uint> { 0, 1 });
        var parsed = CdpValueSet.Parse(values.Serialize());
        if (parsed.GetUInt32("ControlMessage") != 6 || parsed.GetUInt64("BytesToSend") != 123456789UL ||
            parsed.GetGuid("OperationId") != new Guid("00112233-4455-6677-8899-aabbccddeeff"))
            throw new InvalidOperationException("CDP NearShare ValueSet vector mismatch.");
        Console.WriteLine("P-256, Quick Share framing/crypto, CDP certificate, and NearShare ValueSet smoke vectors passed.");
    }
}
