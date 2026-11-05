using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace LiveDrop.Transports
{
    internal sealed class SocketConnection : IDisposable
    {
        private readonly StreamSocket _socket;
        private readonly DataReader _reader;
        private readonly DataWriter _writer;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private int _aborted;
        private int _disposed;

        internal SocketConnection(StreamSocket socket)
        {
            _socket = socket;
            _reader = new DataReader(socket.InputStream) { ByteOrder = ByteOrder.BigEndian, InputStreamOptions = InputStreamOptions.Partial };
            _writer = new DataWriter(socket.OutputStream) { ByteOrder = ByteOrder.BigEndian };
        }

        internal static async Task<SocketConnection> ConnectAsync(string address, int port, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (Protocols.ProtocolUtilities.IsLoopbackAddress(address))
                throw new InvalidOperationException(AppText.Get("LoopbackTransfersAreDisabled"));
            var socket = new StreamSocket();
            // StreamSocketControl properties must be configured before the
            // socket enters the connected state. Setting NoDelay after
            // ConnectAsync raises E_ILLEGAL_METHOD_CALL on UWP.
            socket.Control.NoDelay = true;
            try
            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(15));
                    using (timeout.Token.Register(() => { try { socket.Dispose(); } catch { } }))
                    await socket.ConnectAsync(new HostName(address), port.ToString(), SocketProtectionLevel.PlainSocket).AsTask(timeout.Token);
                }
                return new SocketConnection(socket);
            }
            catch (Exception ex)
            {
                socket.Dispose();
                if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(AppText.Get("ShareCanceledMessage"), ex, cancellationToken);
                if (ex is OperationCanceledException) throw new TimeoutException(AppText.Get("TheTCPConnectionTimedOut"), ex);
                throw;
            }
        }

        internal async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            if (frame == null || frame.Length > Protocols.ProtocolUtilities.MaxFrameLength) throw new InvalidOperationException(AppText.Get("FrameIsTooLarge"));
            using (cancellationToken.Register(Abort))
            {
                await _writeLock.WaitAsync(cancellationToken);
                try
                {
                    _writer.WriteUInt32((uint)frame.Length);
                    _writer.WriteBytes(frame);
                    await _writer.StoreAsync().AsTask(cancellationToken);
                    await _writer.FlushAsync().AsTask(cancellationToken);
                }
                finally { _writeLock.Release(); }
            }
        }

        internal async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
        {
            await LoadExactlyAsync(4, cancellationToken);
            var length = _reader.ReadUInt32();
            if (length > Protocols.ProtocolUtilities.MaxFrameLength) throw new InvalidOperationException(AppText.Get("FrameIsTooLarge"));
            await LoadExactlyAsync(length, cancellationToken);
            var data = new byte[length];
            _reader.ReadBytes(data);
            return data;
        }

        internal async Task<byte[]> ReadRawAsync(int length, CancellationToken cancellationToken)
        {
            if (length < 0) throw new ArgumentOutOfRangeException("length");
            await LoadExactlyAsync((uint)length, cancellationToken);
            var data = new byte[length];
            _reader.ReadBytes(data);
            return data;
        }

        internal async Task WriteRawAsync(byte[] data, CancellationToken cancellationToken)
        {
            if (data == null) throw new ArgumentNullException("data");
            using (cancellationToken.Register(Abort))
            {
                await _writeLock.WaitAsync(cancellationToken);
                try
                {
                    _writer.WriteBytes(data);
                    await _writer.StoreAsync().AsTask(cancellationToken);
                    await _writer.FlushAsync().AsTask(cancellationToken);
                }
                finally { _writeLock.Release(); }
            }
        }

        private async Task LoadExactlyAsync(uint length, CancellationToken cancellationToken)
        {
            using (cancellationToken.Register(Abort))
            {
                try
                {
                    var loaded = 0u;
                    while (loaded < length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var count = await _reader.LoadAsync(length - loaded).AsTask(cancellationToken);
                        if (count == 0) throw new EndOfStreamException(AppText.Get("ThePeerClosedTheConnection"));
                        loaded += count;
                    }
                }
                catch (Exception ex) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(AppText.Get("ShareCanceledMessage"), ex, cancellationToken);
                }
            }
        }

        private void Abort()
        {
            if (Interlocked.Exchange(ref _aborted, 1) != 0) return;
            try { _socket.Dispose(); } catch { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Abort();
            try { _reader.Dispose(); } catch { }
            try { _writer.Dispose(); } catch { }
            _writeLock.Dispose();
        }
    }
}
