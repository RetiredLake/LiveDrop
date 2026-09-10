using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.UI.Core;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using LiveDrop.Models;
using LiveDrop.Services;

namespace LiveDrop
{
    public sealed partial class MainPage : Page
    {
        private ShareCoordinator _coordinator;
        private ShareOperation _pendingShareOperation;
        private ShareOffer _pendingOffer;
        private CancellationTokenSource _sendCancellation;

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
            _coordinator.OfferReceived += OnOfferReceived;
            _coordinator.StatusChanged += OnStatusChanged;
            await _coordinator.StartAsync();
            ConsumePendingShare();
        }

        private async void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (_sendCancellation != null) _sendCancellation.Cancel();
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
            _pendingShareOperation = args.ShareOperation;
            _pendingOffer = offer;
            StatusText.Text = offer.Files.Count == 0
                ? "Text is ready to share after selecting a peer."
                : offer.Files.Count + " file(s) are ready to share after selecting a peer.";
            SendButton.IsEnabled = PeersList.SelectedItem != null && offer.Files.Count > 0;
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

        private void OnPeerSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SendButton.IsEnabled = _pendingOffer != null && _pendingOffer.Files.Count > 0 && PeersList.SelectedItem != null;
        }

        private async void OnSendClicked(object sender, RoutedEventArgs e)
        {
            var selected = PeersList.SelectedItem as PeerListItem;
            if (selected == null || _pendingOffer == null || _coordinator == null) return;
            SendButton.IsEnabled = false;
            _sendCancellation = new CancellationTokenSource();
            try
            {
                var progress = new Progress<ShareProgress>(value =>
                    StatusText.Text = value.FileName + ": " + value.BytesTransferred + "/" + value.TotalBytes + " bytes");
                await _coordinator.SendAsync(selected.Peer, _pendingOffer, progress, _sendCancellation.Token);
                _pendingShareOperation?.ReportCompleted();
                _pendingShareOperation = null;
                _pendingOffer = null;
                StatusText.Text = "Share completed.";
            }
            catch (OperationCanceledException)
            {
                StatusText.Text = "Share canceled.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Share failed: " + ex.Message;
                _pendingShareOperation?.ReportError(ex.Message);
            }
            finally
            {
                if (_sendCancellation != null) { _sendCancellation.Dispose(); _sendCancellation = null; }
                SendButton.IsEnabled = _pendingOffer != null && PeersList.SelectedItem != null;
            }
        }

        private async void OnOfferReceived(object sender, ShareOfferReceivedEventArgs e)
        {
            var decision = new System.Threading.Tasks.TaskCompletionSource<bool>();
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, async () =>
            {
                var dialog = new MessageDialog(e.Peer.DisplayName + " wants to send " + e.Offer.Files.Count + " file(s).", "Accept nearby transfer?");
                dialog.Commands.Add(new UICommand("Accept"));
                dialog.Commands.Add(new UICommand("Reject"));
                dialog.DefaultCommandIndex = 0;
                dialog.CancelCommandIndex = 1;
                var result = await dialog.ShowAsync();
                decision.TrySetResult(result.Label == "Accept");
            });
            await e.CompleteAsync(await decision.Task);
        }
    }

    internal sealed class PeerListItem
    {
        internal PeerDescriptor Peer { get; private set; }
        internal PeerListItem(PeerDescriptor peer) { Peer = peer; }
        public override string ToString() { return Peer.DisplayName + " — " + Peer.Transport; }
    }
}
