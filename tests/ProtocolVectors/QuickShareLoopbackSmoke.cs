using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage;
using Windows.Storage.Streams;
using LiveDrop.Models;
using LiveDrop.Protocols;
using LiveDrop.Protocols.QuickShare;
using LiveDrop.Transports;

// This is the only place where a LiveDrop-to-LiveDrop loopback transfer is
// allowed. It calls QuickShareSession directly over a private test socket;
// production adapters and SocketConnection.ConnectAsync reject loopback.
internal static class QuickShareLoopbackSmoke
{
    private static void Main()
    {
        try { RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex)
        {
            var aggregate = ex as AggregateException;
            if (aggregate != null) foreach (var failure in aggregate.Flatten().InnerExceptions) Console.Error.WriteLine(failure);
            else Console.Error.WriteLine(ex);
            Environment.ExitCode = 1;
        }
    }

    private static async Task RunAsync()
    {
        var namedRequest = QuickShareFrames.ParseConnectionRequest(QuickShareFrames.BuildConnectionRequest(
            "test", "", QuickShareFrames.BuildEndpointInfo("Pixel test", 1)));
        if (namedRequest.EndpointName != "Pixel test")
            throw new InvalidOperationException("Endpoint metadata name was lost.");
        if (!ProtocolUtilities.IsLoopbackAddress("127.0.0.1") ||
            !ProtocolUtilities.IsLoopbackAddress("::1") ||
            !ProtocolUtilities.IsLoopbackAddress("::ffff:127.0.0.1") ||
            !ProtocolUtilities.IsLoopbackAddress("localhost") ||
            ProtocolUtilities.IsLoopbackAddress("192.168.1.20"))
            throw new InvalidOperationException("Loopback transfer guard vector failed.");
        await AssertProductionConnectRejectsLoopbackAsync("127.0.0.1");
        await AssertProductionConnectRejectsLoopbackAsync("::1");
        await AssertProductionConnectRejectsLoopbackAsync("localhost");

        var rootPath = Path.Combine(Path.GetTempPath(), "LiveDrop-quickshare-loopback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(rootPath);
        var receivedPath = Path.Combine(rootPath, "Received");
        Directory.CreateDirectory(receivedPath);
        try
        {
            var root = await StorageFolder.GetFolderFromPathAsync(rootPath);
            var receivedFolder = await StorageFolder.GetFolderFromPathAsync(receivedPath);
            var source = await root.CreateFileAsync("loopback.bin", CreationCollisionOption.ReplaceExisting);
            var expected = new byte[700000];
            for (var i = 0; i < expected.Length; i++) expected[i] = (byte)((i * 31 + 7) & 0xFF);
            await FileIO.WriteBytesAsync(source, expected);
            var secondSource = await root.CreateFileAsync("loopback-second.txt", CreationCollisionOption.ReplaceExisting);
            var secondExpected = new byte[700000];
            for (var i = 0; i < secondExpected.Length; i++) secondExpected[i] = (byte)((i * 17 + 3) & 0xFF);
            await FileIO.WriteBytesAsync(secondSource, secondExpected);

            var offer = new ShareOffer(
                new List<ShareFileDescriptor>
                {
                    new ShareFileDescriptor(source, "application/octet-stream", expected.Length),
                    new ShareFileDescriptor(secondSource, "text/plain", secondExpected.Length)
                },
                string.Empty);
            await TransferAsync(offer, receivedFolder);
            var files = await receivedFolder.GetFilesAsync();
            await AssertFileContentsAsync(files, "loopback.bin", expected);
            await AssertFileContentsAsync(files, "loopback-second.txt", secondExpected);
            var link = new Uri("https://example.org/path?q=phone%20test&second=2#anchor");
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetWebLink(link);
            var textRequested = false;
            package.SetDataProvider(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text, request =>
            {
                textRequested = true;
                request.SetData("A page title, not the URL");
            });
            var linkOffer = await ShareOfferFactory.FromDataPackageViewAsync(package.GetView());
            if (linkOffer.WebLink != link || !linkOffer.HasContent || linkOffer.Files.Count != 0 || textRequested)
                throw new InvalidOperationException("Browser WebLink must take precedence over the fallback text.");
            var legacyPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
#pragma warning disable 618
            legacyPackage.SetUri(link);
#pragma warning restore 618
            if ((await ShareOfferFactory.FromDataPackageViewAsync(legacyPackage.GetView())).WebLink != link)
                throw new InvalidOperationException("Legacy browser Uri share was lost.");
            await TransferAsync(linkOffer, receivedFolder);
            files = await receivedFolder.GetFilesAsync();
            var shortcut = files.FirstOrDefault(file => file.Name == link.Host + ".txt");
            if (shortcut == null || (await FileIO.ReadTextAsync(shortcut)) != link.AbsoluteUri)
                throw new InvalidOperationException("Native Quick Share URL did not round-trip.");
        }
        finally
        {
            try { Directory.Delete(rootPath, true); } catch { }
        }
        Console.WriteLine("PASS: Quick Share handshake, multi-file transfer, browser WebLink to native URL payload and receive; production loopback guard");
    }

    private static async Task TransferAsync(ShareOffer offer, StorageFolder receivedFolder)
    {
            var savedPaths = new List<string>();
            var listener = new StreamSocketListener();
            var receiverTaskSource = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.ConnectionReceived += (sender, args) =>
            {
                var task = QuickShareSession.ReceiveAsync(
                    args.Socket,
                    "Loopback receiver",
                    (peer, incoming) => Task.FromResult(true),
                    status => { },
                    progress => { if (!string.IsNullOrWhiteSpace(progress.SavedFilePath)) savedPaths.Add(progress.SavedFilePath); },
                    CancellationToken.None,
                    receivedFolder);
                if (!receiverTaskSource.TrySetResult(task)) args.Socket.Dispose();
            };

            await listener.BindEndpointAsync(new HostName("127.0.0.1"), string.Empty);
            var outboundSocket = new StreamSocket();
            Exception sendError = null;
            try
            {
                await outboundSocket.ConnectAsync(
                    new HostName("127.0.0.1"),
                    listener.Information.LocalPort,
                    SocketProtectionLevel.PlainSocket);
                using (var connection = new SocketConnection(outboundSocket))
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    await QuickShareSession.SendAsync(
                        connection,
                        "Loopback sender",
                        "test",
                        QuickShareFrames.BuildEndpointInfo("Loopback sender", 3),
                        offer,
                        null,
                        timeout.Token);
                }
            }
            catch (Exception ex) { sendError = ex; }
            finally
            {
                listener.Dispose();
            }

            var receiverWait = await Task.WhenAny(receiverTaskSource.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (receiverWait != receiverTaskSource.Task) throw new TimeoutException("Loopback receiver did not accept the connection.");
            try { await (await receiverTaskSource.Task); }
            catch (Exception ex) { throw new AggregateException("Loopback receiver failed.", sendError ?? ex, ex); }
            if (sendError != null) throw sendError;
            if (savedPaths.Count != (offer.WebLink == null ? offer.Files.Count : 1) || savedPaths.Any(path => !File.Exists(path)))
                throw new InvalidOperationException("Saved-file notification targets must exist after finalization.");

    }

    private static async Task AssertFileContentsAsync(IReadOnlyList<StorageFile> files, string name, byte[] expected)
    {
        var target = files.FirstOrDefault(file => file.Name == name);
        if (target == null) throw new InvalidOperationException("Loopback receiver did not save " + name + ".");
        var buffer = await FileIO.ReadBufferAsync(target);
        var reader = DataReader.FromBuffer(buffer);
        var actual = new byte[checked((int)reader.UnconsumedBufferLength)];
        reader.ReadBytes(actual);
        reader.Dispose();
        if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Loopback file contents did not round-trip: " + name + ".");
    }

    private static async Task AssertProductionConnectRejectsLoopbackAsync(string address)
    {
        try
        {
            using (await SocketConnection.ConnectAsync(address, 9)) { }
            throw new InvalidOperationException("Production socket route allowed loopback: " + address);
        }
        catch (InvalidOperationException ex)
        {
            if (!string.Equals(ex.Message, "Loopback transfers are disabled.", StringComparison.Ordinal)) throw;
        }
    }
}
