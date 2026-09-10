using System;
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
            await _reader.LoadAsync(4);
            var length = _reader.ReadUInt32();
            if (length > Protocols.ProtocolUtilities.MaxFrameLength) throw new InvalidOperationException("Frame is too large.");
            await _reader.LoadAsync(length);
            var data = new byte[length];
            _reader.ReadBytes(data);
            return data;
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

