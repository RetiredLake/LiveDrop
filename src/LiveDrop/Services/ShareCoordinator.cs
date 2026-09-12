using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using LiveDrop.Models;
using LiveDrop.Protocols;
using LiveDrop.Protocols.Nearby;
using LiveDrop.Protocols.QuickShare;

namespace LiveDrop.Services
{
    internal sealed class ShareCoordinator : IDisposable
    {
        private readonly List<IShareProtocolAdapter> _adapters;
        private readonly CancellationTokenSource _stopSource = new CancellationTokenSource();
        private bool _started;

        internal ShareCoordinator(string displayName)
        {
            _adapters = new List<IShareProtocolAdapter>
            {
                new NearbyCdpAdapter(displayName),
                new QuickShareAdapter(displayName)
            };
            foreach (var adapter in _adapters)
            {
                adapter.PeerDiscovered += OnPeerDiscovered;
                adapter.OfferReceived += OnOfferReceived;
                adapter.StatusChanged += OnStatusChanged;
            }
        }

        internal event EventHandler<PeerDiscoveredEventArgs> PeerDiscovered;
        internal event EventHandler<ShareOfferReceivedEventArgs> OfferReceived;
        internal event EventHandler<StatusChangedEventArgs> StatusChanged;

        internal async Task StartAsync()
        {
            if (_started) return;
            _started = true;
            var tasks = new List<Task>();
            foreach (var adapter in _adapters)
            {
                tasks.Add(StartAdapterAsync(adapter));
            }
            await Task.WhenAll(tasks);
        }

        private async Task StartAdapterAsync(IShareProtocolAdapter adapter)
        {
            try
            {
                await adapter.StartAsync(_stopSource.Token);
            }
            catch (Exception ex)
            {
                await adapter.StopAsync();
                StatusChanged?.Invoke(adapter, new StatusChangedEventArgs(adapter.Name + " startup: " + ex.Message));
            }
        }

        internal async Task StopAsync()
        {
            if (!_started) return;
            _started = false;
            _stopSource.Cancel();
            foreach (var adapter in _adapters)
            {
                try { await adapter.StopAsync(); } catch (Exception ex) { StatusChanged?.Invoke(this, new StatusChangedEventArgs("Discovery shutdown: " + ex.Message)); }
            }
        }

        internal async Task SendAsync(PeerDescriptor peer, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken cancellationToken)
        {
            foreach (var adapter in _adapters)
                if (adapter.Transport == peer.Transport) { await adapter.SendAsync(peer, offer, progress, cancellationToken); return; }
            throw new ShareProtocolException("No adapter is available for the selected peer.");
        }

        private void OnPeerDiscovered(object sender, PeerDiscoveredEventArgs e) { PeerDiscovered?.Invoke(this, e); }
        private void OnOfferReceived(object sender, ShareOfferReceivedEventArgs e) { OfferReceived?.Invoke(this, e); }
        private void OnStatusChanged(object sender, StatusChangedEventArgs e) { StatusChanged?.Invoke(sender, e); }

        public void Dispose()
        {
            StopAsync().GetAwaiter().GetResult();
            foreach (var adapter in _adapters)
            {
                adapter.PeerDiscovered -= OnPeerDiscovered;
                adapter.OfferReceived -= OnOfferReceived;
                adapter.StatusChanged -= OnStatusChanged;
                adapter.Dispose();
            }
            _stopSource.Dispose();
        }
    }
}
