using System;
using System.Linq;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using LiveDrop.Models;
using LiveDrop.Services;

namespace LiveDrop
{
    public sealed partial class MainPage : Page
    {
        private ShareCoordinator _coordinator;

        public MainPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (_coordinator != null) return;
            _coordinator = new ShareCoordinator("LiveDrop");
            _coordinator.PeerDiscovered += OnPeerDiscovered;
            _coordinator.StatusChanged += OnStatusChanged;
            await _coordinator.StartAsync();
            ConsumePendingShare();
        }

        private async void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_coordinator == null) return;
            await _coordinator.StopAsync();
            _coordinator.Dispose();
            _coordinator = null;
        }

        internal async void ConsumePendingShare()
        {
            var args = (Application.Current as App)?.TakePendingShare();
            if (args == null || _coordinator == null) return;
            var offer = await ShareOfferFactory.FromShareOperationAsync(args.ShareOperation);
            StatusText.Text = offer.Files.Count == 0
                ? "Text is ready to share after selecting a peer."
                : offer.Files.Count + " file(s) are ready to share after selecting a peer.";
            args.ShareOperation.ReportCompleted();
        }

        private async void OnPeerDiscovered(object sender, PeerDiscoveredEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                if (!PeersList.Items.OfType<PeerListItem>().Any(x => x.Peer.StableId == e.Peer.StableId))
                    PeersList.Items.Add(new PeerListItem(e.Peer));
            });
        }

        private async void OnStatusChanged(object sender, StatusChangedEventArgs e)
        {
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => StatusText.Text = e.Message);
        }
    }

    internal sealed class PeerListItem
    {
        internal PeerDescriptor Peer { get; private set; }
        internal PeerListItem(PeerDescriptor peer) { Peer = peer; }
        public override string ToString() { return Peer.DisplayName + " — " + Peer.Transport; }
    }
}

