using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage;
using LiveDrop.Models;
using LiveDrop.Protocols.QuickShare;
using LiveDrop.Transports;

internal static class QuickShareCancellationSmoke
{
    private static void Main()
    {
        TestResources.Initialize();
        try { RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }

    private static async Task MustFinishAsync(Task task, string stage)
    {
        if (await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5))) != task)
            throw new Exception("Cancellation did not terminate " + stage + ".");
    }

    private static async Task RunAsync()
    {
        // Isolated loopback only. Production discovery/connect guards are not bypassed in the app.
        using (var listener = new StreamSocketListener())
        {
            var accepted = new TaskCompletionSource<StreamSocket>();
            listener.ConnectionReceived += (sender, args) => accepted.TrySetResult(args.Socket);
            await listener.BindEndpointAsync(new HostName("127.0.0.1"), string.Empty);
            var socket = new StreamSocket();
            await socket.ConnectAsync(new HostName("127.0.0.1"), listener.Information.LocalPort);
            using (var remote = await accepted.Task)
            using (var connection = new SocketConnection(socket))
            using (var source = new CancellationTokenSource())
            {
                var read = connection.ReadFrameAsync(source.Token);
                source.Cancel();
                await MustFinishAsync(read, "a blocked handshake read");
                try { await read; throw new Exception("The blocked read completed instead of canceling."); }
                catch (OperationCanceledException) { }
            }
        }

        var path = Path.Combine(Path.GetTempPath(), "LiveDrop-cancel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var root = await StorageFolder.GetFolderFromPathAsync(path);
            var incoming = await root.CreateFolderAsync("Received");
            var file = await root.CreateFileAsync("cancel.bin");
            var bytes = new byte[4 * 1024 * 1024];
            await FileIO.WriteBytesAsync(file, bytes);
            var offer = new ShareOffer(new List<ShareFileDescriptor> { new ShareFileDescriptor(file, "application/octet-stream", bytes.Length) }, "");
            using (var listener = new StreamSocketListener())
            using (var source = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                var accepted = new TaskCompletionSource<Task>();
                listener.ConnectionReceived += (sender, args) => accepted.TrySetResult(QuickShareSession.ReceiveAsync(
                    args.Socket, "Cancellation receiver", (peer, data) => Task.FromResult(true), null, null, CancellationToken.None, incoming));
                await listener.BindEndpointAsync(new HostName("127.0.0.1"), string.Empty);
                var socket = new StreamSocket();
                await socket.ConnectAsync(new HostName("127.0.0.1"), listener.Information.LocalPort);
                var canceledMidFile = false;
                var progress = new ImmediateProgress(value =>
                {
                    if (value.BytesTransferred > 0 && value.BytesTransferred < value.TotalBytes)
                    {
                        canceledMidFile = true;
                        source.Cancel();
                    }
                });
                using (var connection = new SocketConnection(socket))
                {
                    var send = QuickShareSession.SendAsync(connection, "Cancellation sender", "test", QuickShareFrames.BuildEndpointInfo("Cancellation sender", 3), offer, progress, source.Token);
                    await MustFinishAsync(send, "the mid-file sender");
                    var senderStopped = false;
                    try { await send; }
                    catch (Exception) when (source.IsCancellationRequested) { senderStopped = true; }
                    if (!senderStopped) throw new Exception("The canceled sender reported success.");
                }
                if (!canceledMidFile) throw new Exception("The test never reached a partial file.");
                var receive = await accepted.Task;
                await MustFinishAsync(receive, "the disconnected receiver");
                var failed = false;
                try { await receive; } catch { failed = true; }
                if (!failed) throw new Exception("The partial receiver reported success.");
                if ((await incoming.GetFilesAsync()).Count != 0) throw new Exception("Cancellation left a final or partial incoming file.");
            }
        }
        finally { Directory.Delete(path, true); }
        Console.WriteLine("PASS: blocked read aborts; mid-file sender stops; receiver closes and removes the partial file");
    }

    private sealed class ImmediateProgress : IProgress<ShareProgress>
    {
        private readonly Action<ShareProgress> _report;
        internal ImmediateProgress(Action<ShareProgress> report) { _report = report; }
        public void Report(ShareProgress progress) { _report(progress); }
    }
}
