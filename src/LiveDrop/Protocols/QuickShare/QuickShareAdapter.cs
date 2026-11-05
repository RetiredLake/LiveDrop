using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Foundation.Metadata;
using Windows.System.Profile;
using LiveDrop.Models;
using LiveDrop.Protocols;
using LiveDrop.Transports;

namespace LiveDrop.Protocols.QuickShare
{
    internal sealed class QuickShareAdapter : IShareProtocolAdapter
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> LocalEndpoints = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        private readonly string _displayName;
        private readonly string _endpointId;
        private readonly string _instanceName;
        private readonly string _serviceInstanceName;
        private readonly byte[] _endpointInfo;
        private StreamSocketListener _listener;
        private MdnsMulticastService _mdns;
        private QuickShareDnssdPublisher _dnssd;
        private QuickShareBleBeacon _bleBeacon;
        private CancellationTokenSource _stopSource;
        private Task _queryLoop;
        private int _port;
        private string _address;
        private bool _mdnsStarted;

        internal QuickShareAdapter(string displayName)
        {
            _displayName = string.IsNullOrWhiteSpace(displayName) ? "LiveDrop" : displayName;
            _endpointId = CreateEndpointId();
            _instanceName = CreateInstanceName(_endpointId);
            _serviceInstanceName = _instanceName + "." + MdnsCodec.QuickShareService;
            _endpointInfo = QuickShareFrames.BuildEndpointInfo(_displayName, IsWindowsMobile() ? 1 : 3);
        }

        public string Name { get { return "Google Quick Share"; } }
        public ShareTransport Transport { get { return ShareTransport.GoogleQuickShare; } }
        public event EventHandler<PeerDiscoveredEventArgs> PeerDiscovered;
        public event EventHandler<ShareOfferReceivedEventArgs> OfferReceived;
        public event EventHandler<StatusChangedEventArgs> StatusChanged;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_stopSource != null) return;
            LocalEndpoints.TryAdd(_endpointId, 0);
            _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _listener = new StreamSocketListener();
            _listener.ConnectionReceived += OnConnectionReceived;
            // An empty service name asks WinRT for an ephemeral TCP port. The
            // string "0" is treated as a service-name lookup on Windows 10
            // Mobile and can fail with "No such host is known".
            await _listener.BindServiceNameAsync(string.Empty);
            _port = int.Parse(_listener.Information.LocalPort);
            _address = FindLocalIpv4();
            _mdns = new MdnsMulticastService(MdnsCodec.QuickShareService);
            _mdns.ServicesReceived += OnServicesReceived;
            try
            {
                await _mdns.StartAsync(_stopSource.Token);
                _mdnsStarted = true;
            }
            catch (Exception ex)
            {
                StatusChanged?.Invoke(this, new StatusChangedEventArgs("Quick Share mDNS socket unavailable: " + ex.Message));
            }
            _dnssd = new QuickShareDnssdPublisher(_serviceInstanceName, _endpointInfo);
            var dnssdStarted = !_mdnsStarted && await _dnssd.RegisterAsync(_listener, _address);
            _bleBeacon = new QuickShareBleBeacon();
            var bleStarted = await _bleBeacon.StartAsync();
            if (_mdnsStarted) await AnnounceAsync(_stopSource.Token);
            if (!_mdnsStarted && !dnssdStarted)
            {
                await StopAsync();
                throw new InvalidOperationException("Windows could not register the Quick Share DNS-SD service.");
            }
            var discoveryToken = _stopSource.Token;
            _queryLoop = Task.Run(() => QueryLoopAsync(discoveryToken));
            StatusChanged?.Invoke(this, new StatusChangedEventArgs(
                bleStarted
                    ? "Quick Share Bluetooth and LAN discovery is active."
                    : "Quick Share LAN discovery is active. Set the other device to Everyone mode"));
        }

        public async Task StopAsync()
        {
            var source = _stopSource;
            if (source == null) return;
            _stopSource = null;
            source.Cancel();
            // Keep this process's retired IDs filtered while other views retain DNS records.
            if (_queryLoop != null)
            {
                try { await _queryLoop.ConfigureAwait(false); } catch { }
                _queryLoop = null;
            }
            if (_bleBeacon != null) { _bleBeacon.Dispose(); _bleBeacon = null; }
            if (_dnssd != null) { _dnssd.Dispose(); _dnssd = null; }
            if (_mdns != null)
            {
                if (_mdnsStarted)
                {
                    using (var goodbyeSource = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    {
                        try { await _mdns.AnnounceAsync(_instanceName, "livedrop-" + _endpointId + ".local.", _address, _port, _endpointInfo, goodbyeSource.Token, true).ConfigureAwait(false); }
                        catch { }
                    }
                }
                _mdns.ServicesReceived -= OnServicesReceived;
                _mdns.Dispose();
                _mdns = null;
            }
            _mdnsStarted = false;
            if (_listener != null) { _listener.ConnectionReceived -= OnConnectionReceived; _listener.Dispose(); _listener = null; }
            source.Dispose();
        }

        public async Task SendAsync(PeerDescriptor peer, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            if (peer == null || peer.Transport != ShareTransport.GoogleQuickShare) throw new ShareProtocolException("The selected peer is not a Quick Share peer.");
            if (offer == null || !offer.HasContent) throw new ShareProtocolException("Select a file or web link to share.");
            if (ProtocolUtilities.IsLoopbackAddress(peer.Address) || IsLocalAddress(peer.Address))
                throw new ShareProtocolException("Loopback transfers are disabled.");
            var stage = "validating the selected endpoint";
            try
            {
                StatusChanged?.Invoke(this, new StatusChangedEventArgs("Quick Share connecting to " + peer.Address + ":" + peer.Port + "."));
                stage = "opening the TCP connection";
                using (var connection = await SocketConnection.ConnectAsync(peer.Address, peer.Port, cancellationToken))
                {
                    StatusChanged?.Invoke(this, new StatusChangedEventArgs("Quick Share TCP connection established."));
                    stage = "completing UKEY2 security handshake";
                    await QuickShareSession.SendAsync(connection, _displayName, _endpointId, _endpointInfo, offer, progress, cancellationToken,
                        message => StatusChanged?.Invoke(this, new StatusChangedEventArgs(message)));
                }
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception ex)
            {
                var detail = "Quick Share outbound failed while " + stage + " to " + peer.Address + ":" + peer.Port +
                    ": " + ex.Message + " (0x" + ex.HResult.ToString("X8") + ").";
                StatusChanged?.Invoke(this, new StatusChangedEventArgs(detail));
                throw new ShareProtocolException(detail, ex);
            }
        }

        private async Task QueryLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_mdnsStarted)
                    {
                        await AnnounceAsync(cancellationToken);
                        await _mdns.QueryAsync(cancellationToken);
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { StatusChanged?.Invoke(this, new StatusChangedEventArgs("Quick Share discovery: " + ex.Message)); }
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async Task AnnounceAsync(CancellationToken cancellationToken)
        {
            var host = "livedrop-" + _endpointId + ".local.";
            await _mdns.AnnounceAsync(_instanceName, host, _address, _port, _endpointInfo, cancellationToken);
        }

        private void OnServicesReceived(object sender, IList<MdnsServiceRecord> services)
        {
            foreach (var service in services)
            {
                if (service == null ||
                    string.Equals(service.InstanceName, _serviceInstanceName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(service.InstanceName, _instanceName + ".", StringComparison.OrdinalIgnoreCase) ||
                    ProtocolUtilities.IsLoopbackAddress(service.Address) ||
                    string.IsNullOrWhiteSpace(service.Address) || service.Port < 1 || service.Port > 65535 ||
                    IsLocalAddress(service.Address)) continue;
                var id = ExtractEndpointId(service.InstanceName);
                if (string.IsNullOrWhiteSpace(id) || LocalEndpoints.ContainsKey(id)) continue;
                var peer = new PeerDescriptor(id, service.DisplayName ?? "Android device", Transport, service.Address, service.Port, "mDNS/TCP,UKEY2");
                PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs(peer));
            }
        }

        private void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            var remote = args.Socket.Information.RemoteAddress;
            StatusChanged?.Invoke(this, new StatusChangedEventArgs("Quick Share TCP connection received from " + (remote == null ? "unknown peer" : remote.RawName) + "."));
            try { args.Socket.Control.NoDelay = true; } catch { }
            var token = _stopSource == null ? CancellationToken.None : _stopSource.Token;
            _ = HandleIncomingAsync(args.Socket, token);
        }

        private async Task HandleIncomingAsync(StreamSocket socket, CancellationToken cancellationToken)
        {
            ShareOfferReceivedEventArgs received = null;
            var transferAccepted = false;
            var remoteAddress = socket.Information.RemoteAddress == null ? string.Empty : socket.Information.RemoteAddress.RawName;
            if (ProtocolUtilities.IsLoopbackAddress(remoteAddress) || IsLocalAddress(remoteAddress))
            {
                socket.Dispose();
                return;
            }
            try
            {
                // QuickShareSession owns the socket and its single reader/writer pair.
                await QuickShareSession.ReceiveAsync(socket, _displayName, async (peer, offer) =>
                {
                    var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var complete = new Func<bool, Task>(accepted => { decision.TrySetResult(accepted); return Task.CompletedTask; });
                    if (OfferReceived == null) return true;
                    received = new ShareOfferReceivedEventArgs(peer, offer, complete);
                    OfferReceived(this, received);
                    transferAccepted = await decision.Task;
                    return transferAccepted;
                }, message => StatusChanged?.Invoke(this, new StatusChangedEventArgs(message)), progress => received?.ReportProgress(progress), cancellationToken);
                received?.ReportCompletion(transferAccepted, transferAccepted ? "Files are saved in Pictures/LiveDrop." : "Share canceled.");
            }
            catch (OperationCanceledException) { received?.ReportCompletion(false, "Share canceled."); }
            catch (Exception ex)
            {
                received?.ReportCompletion(false, ex.Message);
                StatusChanged?.Invoke(this, new StatusChangedEventArgs("Quick Share transfer failed: " + ex.Message));
            }
        }

        private static string CreateEndpointId()
        {
            const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
            var random = new Random();
            var chars = new char[4];
            for (var i = 0; i < chars.Length; i++) chars[i] = alphabet[random.Next(alphabet.Length)];
            return new string(chars);
        }

        private static bool IsWindowsMobile()
        {
            return string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Mobile", StringComparison.OrdinalIgnoreCase) ||
                ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons");
        }

        private static string CreateInstanceName(string endpointId)
        {
            var raw = new byte[] { 0x23, (byte)endpointId[0], (byte)endpointId[1], (byte)endpointId[2], (byte)endpointId[3], 0xFC, 0x9F, 0x5E, 0, 0 };
            return ProtocolUtilities.Base64Url(raw);
        }

        private static string ExtractEndpointId(string instanceName)
        {
            try
            {
                var rawName = (instanceName ?? string.Empty).TrimEnd('.');
                var label = rawName.Split('.')[0];
                var raw = Convert.FromBase64String(label.Replace('-', '+').Replace('_', '/').PadRight(((label.Length + 3) / 4) * 4, '='));
                if (raw.Length < 8 || raw[0] != 0x23 || raw[5] != 0xFC || raw[6] != 0x9F || raw[7] != 0x5E) return string.Empty;
                return System.Text.Encoding.ASCII.GetString(raw, 1, 4);
            }
            catch { return string.Empty; }
        }

        private static string FindLocalIpv4()
        {
            var host = MdnsMulticastService.GetLanHosts().FirstOrDefault();
            if (host == null) throw new InvalidOperationException("No connected LAN IPv4 address is available for Quick Share.");
            return host.RawName;
        }
        private bool IsLocalAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            if (address.StartsWith("127.", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(address, _address, StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                return NetworkInformation.GetHostNames().Any(host =>
                    host.Type == HostNameType.Ipv4 &&
                    string.Equals(host.RawName, address, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
    }
}
