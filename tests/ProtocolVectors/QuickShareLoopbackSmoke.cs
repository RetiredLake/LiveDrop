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
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
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

            var offer = new ShareOffer(
                new List<ShareFileDescriptor> { new ShareFileDescriptor(source, "application/octet-stream", expected.Length) },
                string.Empty);
            var listener = new StreamSocketListener();
            var receiverTaskSource = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
            listener.ConnectionReceived += (sender, args) =>
            {
                var task = QuickShareSession.ReceiveAsync(
                    args.Socket,
                    "Loopback receiver",
                    (peer, incoming) => Task.FromResult(true),
                    status => { },
                    progress => { },
                    CancellationToken.None,
                    receivedFolder);
                if (!receiverTaskSource.TrySetResult(task)) args.Socket.Dispose();
            };

            await listener.BindServiceNameAsync("0");
            var outboundSocket = new StreamSocket();
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
            finally
            {
                listener.Dispose();
            }

            var receiverWait = await Task.WhenAny(receiverTaskSource.Task, Task.Delay(TimeSpan.FromSeconds(30)));
            if (receiverWait != receiverTaskSource.Task) throw new TimeoutException("Loopback receiver did not accept the connection.");
            await (await receiverTaskSource.Task);

            var files = await receivedFolder.GetFilesAsync();
            var target = files.FirstOrDefault(file => file.Name == "loopback.bin");
            if (target == null) throw new InvalidOperationException("Loopback receiver did not save the file.");
            var buffer = await FileIO.ReadBufferAsync(target);
            var reader = DataReader.FromBuffer(buffer);
            var actual = new byte[checked((int)reader.UnconsumedBufferLength)];
            reader.ReadBytes(actual);
            reader.Dispose();
            if (!expected.SequenceEqual(actual)) throw new InvalidOperationException("Loopback file contents did not round-trip.");
        }
        finally
        {
            try { Directory.Delete(rootPath, true); } catch { }
        }
        Console.WriteLine("PASS: Quick Share loopback handshake and file receive; production loopback guard");
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
