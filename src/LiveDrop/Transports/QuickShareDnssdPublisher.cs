using System;
using System.Linq;
using System.Threading.Tasks;
using Windows.Foundation.Metadata;
using Windows.Networking;
using Windows.Networking.Connectivity;
using Windows.Networking.ServiceDiscovery.Dnssd;
using Windows.Networking.Sockets;

namespace LiveDrop.Transports
{
    internal sealed class QuickShareDnssdPublisher : IDisposable
    {
        private readonly string _serviceInstanceName;
        private readonly string _endpointInfo;
        private DnssdServiceInstance _service;

        internal QuickShareDnssdPublisher(string serviceInstanceName, byte[] endpointInfo)
        {
            _serviceInstanceName = serviceInstanceName;
            _endpointInfo = Protocols.ProtocolUtilities.Base64Url(endpointInfo ?? new byte[0]);
        }

        internal async Task<bool> RegisterAsync(StreamSocketListener listener, string address)
        {
            if (listener == null || !ApiInformation.IsTypePresent("Windows.Networking.ServiceDiscovery.Dnssd.DnssdServiceInstance"))
                return false;

            try
            {
                var hostNames = NetworkInformation.GetHostNames();
                var hostName = hostNames.FirstOrDefault(
                    item => item.Type == HostNameType.DomainName &&
                            item.RawName.EndsWith(".local", StringComparison.OrdinalIgnoreCase));
                if (hostName == null) return false;
                var service = new DnssdServiceInstance(
                    _serviceInstanceName,
                    hostName,
                    checked((ushort)int.Parse(listener.Information.LocalPort)));
                service.TextAttributes["n"] = _endpointInfo;
                var result = await service.RegisterStreamSocketListenerAsync(listener);
                if (result == null || result.Status != DnssdRegistrationStatus.Success)
                    return false;
                _service = service;
                return true;
            }
            catch
            {
                _service = null;
                return false;
            }
        }

        public void Dispose()
        {
            _service = null;
        }
    }
}
