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

        internal SocketConnection(StreamSocket socket)
        {
            _socket = socket;
            _reader = new DataReader(socket.InputStream) { ByteOrder = ByteOrder.BigEndian, InputStreamOptions = InputStreamOptions.Partial };
            _writer = new DataWriter(socket.OutputStream) { ByteOrder = ByteOrder.BigEndian };
        }

        internal static async Task<SocketConnection> ConnectAsync(string address, int port)
        {
            if (Protocols.ProtocolUtilities.IsLoopbackAddress(address))
                throw new InvalidOperationException("Loopback transfers are disabled.");
            var socket = new StreamSocket();
            await socket.ConnectAsync(new HostName(address), port.ToString(), SocketProtectionLevel.PlainSocket);
            socket.Control.NoDelay = true;
            return new SocketConnection(socket);
        }

        internal async Task WriteFrameAsync(byte[] frame, CancellationToken cancellationToken)
        {
            if (frame == null || frame.Length > Protocols.ProtocolUtilities.MaxFrameLength) throw new InvalidOperationException("Frame is too large.");
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                _writer.WriteUInt32((uint)frame.Length);
                _writer.WriteBytes(frame);
                await _writer.StoreAsync();
                await _writer.FlushAsync();
            }
            finally { _writeLock.Release(); }
        }

        internal async Task<byte[]> ReadFrameAsync(CancellationToken cancellationToken)
        {
            await LoadExactlyAsync(4, cancellationToken);
            var length = _reader.ReadUInt32();
            if (length > Protocols.ProtocolUtilities.MaxFrameLength) throw new InvalidOperationException("Frame is too large.");
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
            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                _writer.WriteBytes(data);
                await _writer.StoreAsync();
                await _writer.FlushAsync();
            }
            finally { _writeLock.Release(); }
        }

        private async Task LoadExactlyAsync(uint length, CancellationToken cancellationToken)
        {
            var loaded = 0u;
            while (loaded < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = await _reader.LoadAsync(length - loaded);
                if (count == 0) throw new EndOfStreamException("The peer closed the connection.");
                loaded += count;
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _writer.Dispose();
            _socket.Dispose();
            _writeLock.Dispose();
        }
    }
}
