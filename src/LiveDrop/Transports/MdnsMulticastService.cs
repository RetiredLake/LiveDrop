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
        private sealed class DiscoveryCache
        {
            internal readonly Dictionary<string, MdnsServiceRecord> Records = new Dictionary<string, MdnsServiceRecord>(StringComparer.OrdinalIgnoreCase);
            internal readonly Dictionary<string, string> Addresses = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            internal DateTime Reset = DateTime.UtcNow;
        }
        private readonly Dictionary<DatagramSocket, DiscoveryCache> _caches = new Dictionary<DatagramSocket, DiscoveryCache>();
        private readonly object _cacheLock = new object();
        private readonly Dictionary<DatagramSocket, byte[]> _announcements = new Dictionary<DatagramSocket, byte[]>();
        private readonly Dictionary<DatagramSocket, DateTime> _lastQueryReply = new Dictionary<DatagramSocket, DateTime>();
        private string _instanceName;
        private string _hostName;
        private bool _started;
        private readonly HostName _multicastAddress = new HostName("224.0.0.251");
        internal event EventHandler<IList<MdnsServiceRecord>> ServicesReceived;

        internal MdnsMulticastService(string serviceType) { _serviceType = serviceType; }

        internal static IList<HostName> GetLanHosts()
        {
            var hosts = NetworkInformation.GetHostNames().Where(host => host.Type == HostNameType.Ipv4 &&
                host.IPInformation != null && host.IPInformation.NetworkAdapter != null &&
                !host.RawName.StartsWith("127.") && !host.RawName.StartsWith("169.254.")).ToList();
            var primary = NetworkInformation.GetInternetConnectionProfile();
            if (primary != null && primary.NetworkAdapter != null && !primary.IsWwanConnectionProfile)
            {
                var preferred = hosts.Where(host => host.IPInformation.NetworkAdapter.NetworkAdapterId == primary.NetworkAdapter.NetworkAdapterId).ToList();
                if (preferred.Count > 0) return preferred;
            }
            // Offline LANs remain usable. Do not advertise Hyper-V's host-only
            // Default Switch merely because it reports LocalAccess.
            var connected = NetworkInformation.GetConnectionProfiles().Where(profile =>
                profile.NetworkAdapter != null && !profile.IsWwanConnectionProfile && profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None &&
                (profile.ProfileName ?? "").IndexOf("vEthernet", StringComparison.OrdinalIgnoreCase) < 0)
                .Select(profile => profile.NetworkAdapter.NetworkAdapterId).ToList();
            return hosts.Where(host => connected.Contains(host.IPInformation.NetworkAdapter.NetworkAdapterId))
                .GroupBy(host => host.IPInformation.NetworkAdapter.NetworkAdapterId).Select(group => group.First()).ToList();
        }

        internal async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_started) return;
            Exception lastError = null;
            var hosts = GetLanHosts();
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
            if (!_started) throw new InvalidOperationException(AppText.Get("NoLANInterfaceCouldStartMDNSDiscovery"), lastError);
        }

        internal async Task QueryAsync(CancellationToken cancellationToken)
        {
            if (!_started) return;
            foreach (var socket in _sockets.Keys)
                await SendAsync(socket, MdnsCodec.BuildQuery(_serviceType), cancellationToken);
        }

        internal async Task AnnounceAsync(string instanceName, string hostName, string address, int port, byte[] endpointInfo, CancellationToken cancellationToken, bool goodbye = false)
        {
            if (!_started) return;
            foreach (var socket in _sockets)
            {
                var payload = MdnsCodec.BuildAnnouncement(_serviceType, instanceName, hostName, socket.Value, port, endpointInfo, goodbye);
                lock (_cacheLock)
                {
                    _instanceName = instanceName;
                    _hostName = hostName;
                    if (goodbye) _announcements.Remove(socket.Key);
                    else _announcements[socket.Key] = payload;
                }
                await SendAsync(socket.Key, payload, cancellationToken);
            }
        }

        private async Task SendAsync(DatagramSocket socket, byte[] payload, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var output = await socket.GetOutputStreamAsync(_multicastAddress, "5353").AsTask(cancellationToken);
            var writer = new DataWriter(output);
            writer.WriteBytes(payload);
            await writer.StoreAsync().AsTask(cancellationToken);
            await writer.FlushAsync().AsTask(cancellationToken);
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
                byte[] reply = null;
                lock (_cacheLock)
                {
                    DateTime lastReply;
                    if (_announcements.ContainsKey(sender) && MdnsCodec.IsQueryFor(data, _serviceType, _instanceName, _hostName) &&
                        (!_lastQueryReply.TryGetValue(sender, out lastReply) || DateTime.UtcNow - lastReply >= TimeSpan.FromSeconds(1)))
                    {
                        reply = _announcements[sender];
                        _lastQueryReply[sender] = DateTime.UtcNow;
                    }
                    DiscoveryCache cache;
                    if (!_caches.TryGetValue(sender, out cache)) _caches[sender] = cache = new DiscoveryCache();
                    if (DateTime.UtcNow - cache.Reset > TimeSpan.FromMinutes(2) || cache.Records.Count > 512 || cache.Addresses.Count > 1024)
                    {
                        cache.Records.Clear();
                        cache.Addresses.Clear();
                        cache.Reset = DateTime.UtcNow;
                    }
                    records = MdnsCodec.Parse(data, _serviceType, cache.Records, cache.Addresses)
                        .Where(record => MdnsCodec.IsUsablePeerAnnouncement(record, args.RemoteAddress.RawName)).ToList();
                }
                if (reply != null) _ = ReplyAsync(sender, reply);
                if (records.Count > 0) ServicesReceived?.Invoke(this, records);
            }
            catch { }
        }

        private async Task ReplyAsync(DatagramSocket socket, byte[] payload)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                try { await SendAsync(socket, payload, timeout.Token); }
                catch { }
            }
        }

        public void Dispose()
        {
            foreach (var socket in _sockets.Keys)
            {
                socket.MessageReceived -= OnMessageReceived;
                socket.Dispose();
            }
            _sockets.Clear();
            lock (_cacheLock) { _announcements.Clear(); _lastQueryReply.Clear(); _caches.Clear(); }
            _started = false;
        }
    }
}
