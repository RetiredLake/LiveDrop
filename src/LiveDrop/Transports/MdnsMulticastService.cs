using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace LiveDrop.Transports
{
    internal sealed class MdnsMulticastService : IDisposable
    {
        private readonly string _serviceType;
        private DatagramSocket _socket;
        private bool _started;
        private readonly HostName _multicastAddress = new HostName("224.0.0.251");
        internal event EventHandler<IList<MdnsServiceRecord>> ServicesReceived;

        internal MdnsMulticastService(string serviceType) { _serviceType = serviceType; }

        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_started) return;
            _socket = new DatagramSocket();
            _socket.MessageReceived += OnMessageReceived;
            await _socket.BindServiceNameAsync("5353");
            _socket.JoinMulticastGroup(_multicastAddress);
            _started = true;
        }

        internal async Task QueryAsync(CancellationToken cancellationToken)
        {
            if (!_started) return;
            await SendAsync(MdnsCodec.BuildQuery(_serviceType), cancellationToken);
        }

        internal async Task AnnounceAsync(string instanceName, string hostName, string address, int port, byte[] endpointInfo, CancellationToken cancellationToken)
        {
            if (!_started) return;
            await SendAsync(MdnsCodec.BuildAnnouncement(_serviceType, instanceName, hostName, address, port, endpointInfo), cancellationToken);
        }

        private async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var output = await _socket.GetOutputStreamAsync(_multicastAddress, "5353");
            var writer = new DataWriter(output);
            writer.WriteBytes(payload);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            writer.Dispose();
        }

        private void OnMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                var reader = args.GetDataReader();
                var data = new byte[checked((int)reader.UnconsumedBufferLength)];
                reader.ReadBytes(data);
                var records = MdnsCodec.Parse(data, _serviceType);
                if (records.Count > 0) ServicesReceived?.Invoke(this, records);
            }
            catch { }
        }

        public void Dispose()
        {
            if (_socket == null) return;
            _socket.MessageReceived -= OnMessageReceived;
            _socket.Dispose();
            _socket = null;
            _started = false;
        }
    }
}
