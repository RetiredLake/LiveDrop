using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;

namespace LiveDrop.Transports
{
    internal sealed class MdnsMulticastService : IDisposable
    {
        private readonly string _serviceType;
        private readonly Dictionary<DatagramSocket, string> _sockets = new Dictionary<DatagramSocket, string>();
        private readonly Dictionary<string, MdnsServiceRecord> _records = new Dictionary<string, MdnsServiceRecord>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();
        private DateTime _cacheReset = DateTime.UtcNow;
        private bool _started;
        private readonly HostName _multicastAddress = new HostName("224.0.0.251");
        internal event EventHandler<IList<MdnsServiceRecord>> ServicesReceived;

        internal MdnsMulticastService(string serviceType) { _serviceType = serviceType; }

        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_started) return;
            Exception lastError = null;
            var hosts = NetworkInformation.GetHostNames().Where(host => host.Type == HostNameType.Ipv4 &&
                host.IPInformation != null && host.IPInformation.NetworkAdapter != null &&
                !host.RawName.StartsWith("127.") && !host.RawName.StartsWith("169.254."))
                .GroupBy(host => host.IPInformation.NetworkAdapter.NetworkAdapterId).Select(group => group.First());
            foreach (var host in hosts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var socket = new DatagramSocket();
                socket.MessageReceived += OnMessageReceived;
                socket.Control.OutboundUnicastHopLimit = 255;
                socket.Control.MulticastOnly = true;
                try
                {
                    await socket.BindServiceNameAsync("5353", host.IPInformation.NetworkAdapter);
                    socket.JoinMulticastGroup(_multicastAddress);
                    _sockets.Add(socket, host.RawName);
                }
                catch (Exception ex)
                {
                    lastError = ex;
                    socket.MessageReceived -= OnMessageReceived;
                    socket.Dispose();
                }
            }
            _started = _sockets.Count > 0;
            if (!_started) throw new InvalidOperationException("No LAN interface could start mDNS discovery.", lastError);
        }

        internal async Task QueryAsync(CancellationToken cancellationToken)
        {
            if (!_started) return;
            foreach (var socket in _sockets.Keys)
                await SendAsync(socket, MdnsCodec.BuildQuery(_serviceType), cancellationToken);
        }

        internal async Task AnnounceAsync(string instanceName, string hostName, string address, int port, byte[] endpointInfo, CancellationToken cancellationToken)
        {
            if (!_started) return;
            foreach (var socket in _sockets)
                await SendAsync(socket.Key, MdnsCodec.BuildAnnouncement(_serviceType, instanceName, hostName, socket.Value, port, endpointInfo), cancellationToken);
        }

        private async Task SendAsync(DatagramSocket socket, byte[] payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await socket.GetOutputStreamAsync(_multicastAddress, "5353");
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
                IList<MdnsServiceRecord> records;
                lock (_cacheLock)
                {
                    if (DateTime.UtcNow - _cacheReset > TimeSpan.FromMinutes(2) || _records.Count > 512 || _addresses.Count > 1024)
                    {
                        _records.Clear();
                        _addresses.Clear();
                        _cacheReset = DateTime.UtcNow;
                    }
                    records = MdnsCodec.Parse(data, _serviceType, _records, _addresses);
                }
                if (records.Count > 0) ServicesReceived?.Invoke(this, records);
            }
            catch { }
        }

        public void Dispose()
        {
            foreach (var socket in _sockets.Keys)
            {
                socket.MessageReceived -= OnMessageReceived;
                socket.Dispose();
            }
            _sockets.Clear();
            _started = false;
        }
    }
}
