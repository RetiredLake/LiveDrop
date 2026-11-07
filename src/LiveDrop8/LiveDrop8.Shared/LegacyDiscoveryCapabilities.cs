using System;
using System.Threading.Tasks;
using Windows.Networking.Sockets;

namespace LiveDrop.Transports
{
    // 8.1 uses our existing multicast DNS implementation. The UWP BLE
    // advertising and DNS-SD registration APIs do not exist on these targets.
    internal sealed class QuickShareBleBeacon : IDisposable
    {
        internal Task<bool> StartAsync() { return Task.FromResult(false); }
        public void Dispose() { }
    }
    internal sealed class MicrosoftNearbyBleBeacon : IDisposable
    {
        internal Task<bool> StartAsync(string deviceName) { return Task.FromResult(false); }
        public void Dispose() { }
    }
    internal sealed class QuickShareDnssdPublisher : IDisposable
    {
        internal QuickShareDnssdPublisher(string name, byte[] info) { }
        internal Task<bool> RegisterAsync(StreamSocketListener listener, string address) { return Task.FromResult(false); }
        public void Dispose() { }
    }
}
