using System;
using System.Threading;
using System.Threading.Tasks;
using LiveDrop.Models;

namespace LiveDrop.Protocols
{
    public interface IShareProtocolAdapter : IDisposable
    {
        string Name { get; }
        ShareTransport Transport { get; }
        event EventHandler<PeerDiscoveredEventArgs> PeerDiscovered;
        event EventHandler<ShareOfferReceivedEventArgs> OfferReceived;
        event EventHandler<StatusChangedEventArgs> StatusChanged;
        Task StartAsync(CancellationToken cancellationToken);
        Task StopAsync();
        Task SendAsync(PeerDescriptor peer, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken);
    }

    public sealed class ShareProtocolException : Exception
    {
        public ShareProtocolException(string message) : base(message) { }
        public ShareProtocolException(string message, Exception inner) : base(message, inner) { }
    }
}

