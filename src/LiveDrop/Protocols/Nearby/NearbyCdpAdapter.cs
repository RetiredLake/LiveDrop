using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.Sockets;
using Windows.Storage.Streams;
using LiveDrop.Models;
using LiveDrop.Protocols;
using LiveDrop.Transports;

namespace LiveDrop.Protocols.Nearby
{
    internal sealed class NearbyCdpAdapter : IShareProtocolAdapter
    {
        private const int UdpPort = 5050;
        private const int TcpPort = 5040;
        private const ushort Signature = 0x3030;
        private readonly string _displayName;
        private readonly CdpIdentity _identity;
        private DatagramSocket _udp;
        private StreamSocketListener _tcp;
        private MicrosoftNearbyBleBeacon _bleBeacon;
        private CancellationTokenSource _stopSource;
        private Task _queryLoop;
        private int _sequence;

        internal NearbyCdpAdapter(string displayName)
        {
            _displayName = string.IsNullOrWhiteSpace(displayName) ? "LiveDrop" : displayName;
            _identity = CdpIdentity.Create(_displayName);
        }

        public string Name { get { return "Microsoft Nearby Share"; } }
        public ShareTransport Transport { get { return ShareTransport.MicrosoftNearby; } }
        public event EventHandler<PeerDiscoveredEventArgs> PeerDiscovered;
        public event EventHandler<ShareOfferReceivedEventArgs> OfferReceived;
        public event EventHandler<StatusChangedEventArgs> StatusChanged;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_stopSource != null) return;
            _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _tcp = new StreamSocketListener();
            _tcp.ConnectionReceived += OnConnectionReceived;
            try { await _tcp.BindServiceNameAsync(TcpPort.ToString()); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not open TCP 5040 (" + SocketError.GetStatus(ex.HResult) + "). Windows Nearby Sharing may already be using this port.", ex);
            }
            _udp = new DatagramSocket();
            _udp.MessageReceived += OnMessageReceived;
            try { await _udp.BindServiceNameAsync(UdpPort.ToString()); }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Could not open UDP 5050 (" + SocketError.GetStatus(ex.HResult) + "). Windows Nearby Sharing may already be using this port.", ex);
            }
            _bleBeacon = new MicrosoftNearbyBleBeacon();
            var bleStarted = await _bleBeacon.StartAsync(_displayName);
            var discoveryToken = _stopSource.Token;
            _queryLoop = Task.Run(() => QueryLoopAsync(discoveryToken));
            StatusChanged?.Invoke(this, new StatusChangedEventArgs(
                bleStarted
                    ? "Microsoft Nearby Bluetooth and LAN discovery is active."
                    : "Microsoft Nearby LAN discovery is active; Bluetooth discovery is unavailable."));
        }

        public async Task StopAsync()
        {
            var source = _stopSource;
            if (source == null) return;
            _stopSource = null;
            source.Cancel();
            if (_queryLoop != null) { try { await _queryLoop.ConfigureAwait(false); } catch { } _queryLoop = null; }
            if (_udp != null) { _udp.MessageReceived -= OnMessageReceived; _udp.Dispose(); _udp = null; }
            if (_bleBeacon != null) { _bleBeacon.Dispose(); _bleBeacon = null; }
            if (_tcp != null) { _tcp.ConnectionReceived -= OnConnectionReceived; _tcp.Dispose(); _tcp = null; }
            source.Dispose();
        }

        public async Task SendAsync(PeerDescriptor peer, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            if (peer == null || peer.Transport != ShareTransport.MicrosoftNearby)
                throw new ShareProtocolException("The selected peer is not a Microsoft Nearby peer.");
            if (offer == null || offer.Files == null || offer.Files.Count == 0)
                throw new ShareProtocolException("Microsoft Nearby requires at least one file.");

            using (var connection = await SocketConnection.ConnectAsync(peer.Address, peer.Port))
            {
                var session = await CdpClientSession.ConnectAsync(connection, _identity, cancellationToken);
                await session.SendFilesAsync(offer, progress, cancellationToken);
            }
        }

        private async Task QueryLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var request = BuildPresenceRequest();
                    foreach (var address in GetBroadcastAddresses())
                        await SendDatagramAsync(request, address, cancellationToken);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { StatusChanged?.Invoke(this, new StatusChangedEventArgs("Nearby discovery: " + ex.Message)); }
                try { await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken); }
                catch (OperationCanceledException) { return; }
            }
        }

        private async void OnMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                var reader = args.GetDataReader();
                var data = new byte[checked((int)reader.UnconsumedBufferLength)];
                reader.ReadBytes(data);
                var payloadOffset = GetDiscoveryPayloadOffset(data);
                if (payloadOffset < 0) return;
                if (NetworkInformation.GetHostNames().Any(host => host.Type == HostNameType.Ipv4 && host.RawName == args.RemoteAddress.RawName)) return;
                if (data[payloadOffset] == 0)
                {
                    await SendDatagramAsync(BuildPresenceResponse(), args.RemoteAddress.RawName, _stopSource == null ? CancellationToken.None : _stopSource.Token);
                    return;
                }
                NearbyPresence presence;
                if (!TryParsePresence(data, out presence)) return;
                var peer = new PeerDescriptor(
                    "nearby:" + args.RemoteAddress.RawName,
                    presence.DeviceName,
                    Transport,
                    args.RemoteAddress.RawName,
                    TcpPort,
                    "CDP v3,TCP 5040,UDP 5050");
                PeerDiscovered?.Invoke(this, new PeerDiscoveredEventArgs(peer));
            }
            catch { }
            await Task.CompletedTask;
        }

        private static string[] GetBroadcastAddresses()
        {
            var addresses = new System.Collections.Generic.HashSet<string>();
            foreach (var host in NetworkInformation.GetHostNames())
            {
                if (host.Type != HostNameType.Ipv4 || host.IPInformation == null || !host.IPInformation.PrefixLength.HasValue ||
                    host.RawName.StartsWith("127.") || host.RawName.StartsWith("169.254.")) continue;
                var prefix = (int)host.IPInformation.PrefixLength.Value;
                if (prefix < 1 || prefix > 30) continue;
                var bytes = IPAddress.Parse(host.RawName).GetAddressBytes();
                var value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
                var broadcast = value | (uint.MaxValue >> prefix);
                addresses.Add(new IPAddress(new byte[] { (byte)(broadcast >> 24), (byte)(broadcast >> 16), (byte)(broadcast >> 8), (byte)broadcast }).ToString());
            }
            if (addresses.Count == 0) addresses.Add("255.255.255.255");
            return addresses.ToArray();
        }

        private void OnConnectionReceived(StreamSocketListener sender, StreamSocketListenerConnectionReceivedEventArgs args)
        {
            args.Socket.Control.NoDelay = true;
            var token = _stopSource == null ? CancellationToken.None : _stopSource.Token;
            _ = HandleIncomingAsync(args.Socket, token);
        }

        private async Task HandleIncomingAsync(StreamSocket socket, CancellationToken cancellationToken)
        {
            var address = socket.Information.RemoteAddress == null ? string.Empty : socket.Information.RemoteAddress.RawName;
            var peer = new PeerDescriptor("nearby:" + address, "Nearby device", Transport, address, TcpPort, "CDP v3/TCP");
            try
            {
                using (var connection = new SocketConnection(socket))
                {
                    await CdpServerSession.ReceiveAsync(connection, _identity, peer, async (remote, offer) =>
                    {
                        var decision = new TaskCompletionSource<bool>();
                        var complete = new Func<bool, Task>(accepted => { decision.TrySetResult(accepted); return Task.CompletedTask; });
                        if (OfferReceived == null) return false;
                        OfferReceived(this, new ShareOfferReceivedEventArgs(remote, offer, complete));
                        return await decision.Task;
                    }, message => StatusChanged?.Invoke(this, new StatusChangedEventArgs(message)), cancellationToken);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { StatusChanged?.Invoke(this, new StatusChangedEventArgs("Microsoft Nearby transfer failed: " + ex.Message)); }
        }

        private async Task SendDatagramAsync(byte[] data, string address, CancellationToken cancellationToken)
        {
            var output = await _udp.GetOutputStreamAsync(new HostName(address), UdpPort.ToString());
            var writer = new DataWriter(output);
            writer.WriteBytes(data);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            writer.Dispose();
        }

        private byte[] BuildPresenceRequest()
        {
            var payload = new byte[] { 0 };
            return BuildCdpMessage(1, payload);
        }

        private byte[] BuildPresenceResponse()
        {
            var payload = new MemoryStream();
            WriteInt16(payload, 1);
            WriteInt16(payload, 11);
            var name = Encoding.UTF8.GetBytes(_displayName);
            WriteUInt16(payload, (ushort)Math.Min(255, name.Length));
            payload.Write(name, 0, Math.Min(255, name.Length));
            WriteInt32(payload, Environment.TickCount);
            var idInput = Encoding.UTF8.GetBytes(_displayName + "LiveDrop");
            using (var sha = SHA256.Create()) payload.Write(sha.ComputeHash(idInput), 0, 32);
            return BuildCdpMessage(1, new byte[] { 1 }.Concat(payload.ToArray()).ToArray());
        }

        private byte[] BuildCdpMessage(byte messageType, byte[] payload)
        {
            var headerLength = 42;
            var stream = new MemoryStream();
            WriteUInt16(stream, Signature);
            WriteUInt16(stream, (ushort)(headerLength + payload.Length));
            stream.WriteByte(3);
            stream.WriteByte(messageType);
            WriteInt16(stream, 0);
            WriteInt32(stream, ++_sequence);
            WriteUInt64(stream, 0);
            WriteUInt16(stream, 0);
            WriteUInt16(stream, 1);
            WriteUInt64(stream, 0);
            WriteUInt64(stream, 0);
            stream.WriteByte(0); stream.WriteByte(0);
            stream.Write(payload, 0, payload.Length);
            return stream.ToArray();
        }

        private static bool TryParsePresence(byte[] data, out NearbyPresence presence)
        {
            presence = null;
            var offset = GetDiscoveryPayloadOffset(data);
            if (offset < 0 || data[offset++] != 1) return false;
            if (offset + 6 > data.Length) return false;
            var mode = ReadInt16(data, ref offset);
            var deviceType = ReadInt16(data, ref offset);
            var nameLength = ReadUInt16(data, ref offset);
            if (nameLength > data.Length - offset) return false;
            var name = Encoding.UTF8.GetString(data, offset, nameLength); offset += nameLength;
            if (offset + 4 + 32 > data.Length) return false;
            offset += 4 + 32;
            presence = new NearbyPresence { DeviceName = string.IsNullOrWhiteSpace(name) ? "Windows PC" : name, DeviceType = deviceType, Mode = mode };
            return true;
        }

        private static int GetDiscoveryPayloadOffset(byte[] data)
        {
            if (data == null || data.Length < 43) return -1;
            var offset = 0;
            if (ReadUInt16(data, ref offset) != Signature || ReadUInt16(data, ref offset) != data.Length ||
                data[4] != 3 || data[5] != 1 || data[20] != 0 || data[21] != 0 || data[22] != 0 || data[23] != 1)
                return -1;
            offset = 40;
            while (offset + 2 <= data.Length)
            {
                var type = data[offset++];
                var length = data[offset++];
                if (type == 0) return length == 0 && offset < data.Length ? offset : -1;
                if (offset + length > data.Length) return -1;
                offset += length;
            }
            return -1;
        }

        private static ushort ReadUInt16(byte[] data, ref int offset) { var value = (ushort)((data[offset] << 8) | data[offset + 1]); offset += 2; return value; }
        private static short ReadInt16(byte[] data, ref int offset) { return unchecked((short)ReadUInt16(data, ref offset)); }
        private static void WriteUInt16(Stream stream, ushort value) { stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void WriteInt16(Stream stream, short value) { WriteUInt16(stream, unchecked((ushort)value)); }
        private static void WriteInt32(Stream stream, int value) { stream.WriteByte((byte)(value >> 24)); stream.WriteByte((byte)(value >> 16)); stream.WriteByte((byte)(value >> 8)); stream.WriteByte((byte)value); }
        private static void WriteUInt64(Stream stream, ulong value) { for (var i = 7; i >= 0; i--) stream.WriteByte((byte)(value >> (8 * i))); }

        private sealed class NearbyPresence
        {
            internal string DeviceName { get; set; }
            internal short DeviceType { get; set; }
            internal short Mode { get; set; }
        }

        public void Dispose() { StopAsync().GetAwaiter().GetResult(); }
    }
}
