using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LiveDrop.Models;
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
        var publicKey = alice.ExportGenericPublicKey();
        var publicKeyReader = new ProtoReader(publicKey);
        var coordinateData = new byte[0];
        while (!publicKeyReader.End)
        {
            var tag = publicKeyReader.ReadTag();
            if ((tag >> 3) == 2 && (tag & 7) == 2) coordinateData = publicKeyReader.ReadBytes();
            else publicKeyReader.Skip(tag & 7);
        }
        var coordinatesReader = new ProtoReader(coordinateData);
        var coordinatesChecked = 0;
        while (!coordinatesReader.End)
        {
            var tag = coordinatesReader.ReadTag();
            if ((tag >> 3) == 1 || (tag >> 3) == 2)
            {
                var coordinate = coordinatesReader.ReadBytes();
                if (coordinate.Length != 32 && coordinate.Length != 33) throw new InvalidOperationException("Quick Share public-key coordinate length mismatch.");
                if ((coordinate[0] & 0x80) != 0) throw new InvalidOperationException("Quick Share public-key coordinate sign mismatch.");
                coordinatesChecked++;
            }
            else coordinatesReader.Skip(tag & 7);
        }
        if (coordinatesChecked != 2) throw new InvalidOperationException("Quick Share public-key coordinate vector mismatch.");
        var controlBody = new byte[] { 1, 2, 3 };
        var controlStart = QuickShareFrames.ParsePayloadChunk(QuickShareFrames.BuildPayloadChunk(43, 1, controlBody.Length, 0, controlBody, false));
        var controlEnd = QuickShareFrames.ParsePayloadChunk(QuickShareFrames.BuildPayloadChunk(43, 1, controlBody.Length, controlBody.Length, new byte[0], true));
        if (controlStart == null || controlEnd == null || controlStart.Offset != 0 || controlEnd.Offset != controlBody.Length ||
            controlEnd.TotalSize != controlBody.Length || !controlEnd.Last)
            throw new InvalidOperationException("Quick Share control-payload marker vector mismatch.");
        var descriptor = new ShareFileDescriptor { Name = "photo.jpg", MimeType = "image/jpeg", Size = 123 };
        var introduction = QuickShareFrames.ParseIntroduction(QuickShareFrames.BuildIntroduction(new[] { descriptor }, new[] { 12345L }));
        if (introduction.Count != 1 || introduction[0].PayloadId != 12345L || introduction[0].AttachmentId != 12345L)
            throw new InvalidOperationException("Quick Share file identity vector mismatch.");
        var fileChunk = QuickShareFrames.ParsePayloadChunk(QuickShareFrames.BuildFileChunk(12345L, 123L, 0, new byte[] { 1, 2 }, false, "photo.jpg"));
        if (fileChunk == null || fileChunk.PayloadType != 2 || fileChunk.PayloadId != 12345L || fileChunk.FileName != "photo.jpg")
            throw new InvalidOperationException("Quick Share file-header vector mismatch.");
        var connectionResponse = QuickShareFrames.ParseConnectionResponse(QuickShareFrames.BuildConnectionAccept());
        if (!connectionResponse.Accepted || connectionResponse.SafeToDisconnectVersion != 1)
            throw new InvalidOperationException("Quick Share connection-response vector mismatch.");
        var disconnection = QuickShareFrames.BuildDisconnection(true, false);
        if (!QuickShareFrames.IsDisconnection(disconnection) || !QuickShareFrames.IsSafeDisconnectRequest(disconnection))
            throw new InvalidOperationException("Quick Share safe-disconnect vector mismatch.");
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
