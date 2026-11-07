using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Networking;
using Windows.Storage.Streams;
using Windows.Security.Cryptography;

namespace LiveDrop.Transports
{
    // The 8.1 WinRT socket projection lacks MulticastOnly. Winsock's supported
    // SO_REUSEADDR option provides multicast port sharing on the actual 8.1 OS,
    // as well as on Windows 10/11 compatibility hosts. No protocol changes.
    internal sealed class LegacyMulticastSocket : IDisposable
    {
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private IntPtr _socket = new IntPtr(-1);
        private bool _winsock, _disposed;
        private byte[] _localAddress;
        internal LegacyMulticastSocket Control { get { return this; } }
        internal uint OutboundUnicastHopLimit { get; set; }
        internal event Action<LegacyMulticastSocket, LegacyDatagramReceived> MessageReceived;

        internal Task BindServiceNameAsync(string port, HostName host)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("mDNS socket");
                _localAddress = System.Net.IPAddress.Parse(host.RawName).GetAddressBytes();
                var startup = Native.WSAStartup(0x0202, new byte[512]);
                if (startup != 0) throw Failure("WSAStartup", startup);
                _winsock = true;
                try
                {
                    _socket = Native.socket(2, 2, 17);
                    if (_socket == new IntPtr(-1)) throw Failure("socket");
                    SetOption(0xffff, 4, BitConverter.GetBytes(1)); // SO_REUSEADDR before bind
                    SetOption(0, 9, _localAddress); // IP_MULTICAST_IF
                    SetOption(0, 10, BitConverter.GetBytes(255)); // IP_MULTICAST_TTL
                    var address = Address(new byte[4], int.Parse(port));
                    if (Native.bind(_socket, address, address.Length) != 0) throw Failure("bind UDP " + port);
                    uint nonblocking = 1;
                    if (Native.ioctlsocket(_socket, 0x8004667e, ref nonblocking) != 0) throw Failure("ioctlsocket");
                }
                catch { CloseCore(); throw; }
            }
            return Task.FromResult(true);
        }

        internal void JoinMulticastGroup(HostName group)
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException("mDNS socket");
                var membership = System.Net.IPAddress.Parse(group.RawName).GetAddressBytes().Concat(_localAddress).ToArray();
                SetOption(0, 12, membership); // IP_ADD_MEMBERSHIP on this LAN adapter
            }
            var receive = ReceiveAsync();
        }

        internal async Task SendAsync(byte[] payload, CancellationToken cancellationToken)
        {
            var destination = Address(new byte[] { 224, 0, 0, 251 }, 5353);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    if (_disposed) throw new ObjectDisposedException("mDNS socket");
                    var sent = Native.sendto(_socket, payload, payload.Length, 0, destination, destination.Length);
                    if (sent == payload.Length) return;
                    var error = Native.WSAGetLastError();
                    if (error != 10035) throw Failure("send mDNS", error);
                }
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReceiveAsync()
        {
            var data = new byte[65535];
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    LegacyDatagramReceived packet = null;
                    lock (_sync)
                    {
                        if (_disposed) return;
                        var remote = new byte[16]; var length = remote.Length;
                        var count = Native.recvfrom(_socket, data, data.Length, 0, remote, ref length);
                        if (count >= 0)
                        {
                            var bytes = new byte[count]; System.Buffer.BlockCopy(data, 0, bytes, 0, count);
                            packet = new LegacyDatagramReceived(bytes, new HostName(string.Join(".", remote.Skip(4).Take(4))));
                        }
                        else
                        {
                            var error = Native.WSAGetLastError();
                            if (error != 10035 && error != 10054) throw Failure("receive mDNS", error);
                        }
                    }
                    if (packet != null)
                    {
                        var handler = MessageReceived;
                        if (handler != null) handler(this, packet);
                    }
                    else await Task.Delay(25, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                // Surface terminal socket failures to the owner; never leave an
                // unobserved task exception or silently claim discovery works.
                var handler = ReceiveFailed;
                if (handler != null) handler(ex);
            }
        }
        internal event Action<Exception> ReceiveFailed;
        private void SetOption(int level, int option, byte[] value)
        { if (Native.setsockopt(_socket, level, option, value, value.Length) != 0) throw Failure("setsockopt " + option); }
        private static byte[] Address(byte[] ip, int port)
        { var bytes = new byte[16]; bytes[0] = 2; bytes[2] = (byte)(port >> 8); bytes[3] = (byte)port; System.Buffer.BlockCopy(ip, 0, bytes, 4, 4); return bytes; }
        private static Exception Failure(string operation, int error = 0)
        { return new InvalidOperationException(operation + " failed (Winsock " + (error == 0 ? Native.WSAGetLastError() : error) + ")."); }
        private void CloseCore()
        {
            if (_socket != new IntPtr(-1)) { Native.closesocket(_socket); _socket = new IntPtr(-1); }
            if (_winsock) { Native.WSACleanup(); _winsock = false; }
        }
        public void Dispose()
        {
            lock (_sync) { if (_disposed) return; _disposed = true; _stop.Cancel(); CloseCore(); }
        }
        private static class Native
        {
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int WSAStartup(ushort version, [Out] byte[] data);
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int WSACleanup();
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int WSAGetLastError();
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern IntPtr socket(int family, int type, int protocol);
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int closesocket(IntPtr socket);
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int setsockopt(IntPtr socket, int level, int option, byte[] value, int length);
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int bind(IntPtr socket, byte[] address, int length);
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int ioctlsocket(IntPtr socket, uint command, ref uint value);
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int sendto(IntPtr socket, byte[] data, int length, int flags, byte[] destination, int destinationLength);
            [DllImport("ws2_32.dll", ExactSpelling = true)] internal static extern int recvfrom(IntPtr socket, [Out] byte[] data, int length, int flags, [Out] byte[] address, ref int addressLength);
        }
    }
    internal sealed class LegacyDatagramReceived
    {
        private readonly byte[] _data;
        internal HostName RemoteAddress { get; private set; }
        internal LegacyDatagramReceived(byte[] data, HostName remote) { _data = data; RemoteAddress = remote; }
        internal DataReader GetDataReader() { return DataReader.FromBuffer(CryptographicBuffer.CreateFromByteArray(_data)); }
    }
}
