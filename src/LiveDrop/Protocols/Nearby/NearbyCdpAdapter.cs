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

namespace LiveDrop.Protocols.Nearby
{
    internal sealed class NearbyCdpAdapter : IShareProtocolAdapter
    {
        private const int UdpPort = 5050;
        private const int TcpPort = 5040;
        private const ushort Signature = 0x3030;
        private readonly string _displayName;
        private DatagramSocket _udp;
        private CancellationTokenSource _stopSource;
        private Task _queryLoop;
        private int _sequence;

        internal NearbyCdpAdapter(string displayName) { _displayName = string.IsNullOrWhiteSpace(displayName) ? "LiveDrop" : displayName; }

        public string Name { get { return "Microsoft Nearby Share"; } }
        public ShareTransport Transport { get { return ShareTransport.MicrosoftNearby; } }
        public event EventHandler<PeerDiscoveredEventArgs> PeerDiscovered;
        public event EventHandler<ShareOfferReceivedEventArgs> OfferReceived;
        public event EventHandler<StatusChangedEventArgs> StatusChanged;

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            if (_stopSource != null) return;
            _stopSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _udp = new DatagramSocket();
            _udp.MessageReceived += OnMessageReceived;
            await _udp.BindServiceNameAsync(UdpPort.ToString());
            _queryLoop = QueryLoopAsync(_stopSource.Token);
            StatusChanged?.Invoke(this, new StatusChangedEventArgs("Microsoft Nearby discovery is active."));
        }

        public async Task StopAsync()
        {
            var source = _stopSource;
            if (source == null) return;
            _stopSource = null;
            source.Cancel();
            if (_queryLoop != null) { try { await _queryLoop; } catch { } _queryLoop = null; }
            if (_udp != null) { _udp.MessageReceived -= OnMessageReceived; _udp.Dispose(); _udp = null; }
            source.Dispose();
        }

        public async Task SendAsync(PeerDescriptor peer, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            if (peer == null || peer.Transport != ShareTransport.MicrosoftNearby) throw new ShareProtocolException("The selected peer is not a Microsoft Nearby peer.");
            throw new ShareProtocolException("Microsoft Nearby discovery is enabled; CDP authentication and transfer are the next protocol milestone.");
        }

        private async Task QueryLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await SendDatagramAsync(BuildPresenceRequest(), "255.255.255.255", cancellationToken);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { StatusChanged?.Invoke(this, new StatusChangedEventArgs("Nearby discovery: " + ex.Message)); }
            }
        }

        private async void OnMessageReceived(DatagramSocket sender, DatagramSocketMessageReceivedEventArgs args)
        {
            try
            {
                var reader = args.GetDataReader();
                var data = new byte[checked((int)reader.UnconsumedBufferLength)];
                reader.ReadBytes(data);
                if (data.Length >= 43 && data[42] == 0)
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
            if (data == null || data.Length < 43) return false;
            var offset = 0;
            if (ReadUInt16(data, ref offset) != Signature) return false;
            var messageLength = ReadUInt16(data, ref offset);
            if (messageLength > data.Length || data[offset++] != 3 || data[offset++] != 1) return false;
            offset += 2 + 4 + 8 + 2 + 2 + 8 + 8 + 2;
            if (offset >= data.Length || data[offset++] != 1) return false;
            if (offset + 2 > data.Length) return false;
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
