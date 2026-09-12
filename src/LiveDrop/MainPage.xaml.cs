using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.System;
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
        private readonly MenuFlyout _moreMenu;
        private bool _updateCheckInProgress;
        private readonly System.Collections.Generic.Dictionary<string, string> _discoveryStatus = new System.Collections.Generic.Dictionary<string, string>();

        public MainPage()
        {
            InitializeComponent();
            _moreMenu = new MenuFlyout();
            var githubItem = new MenuFlyoutItem { Text = "About" };
            githubItem.Click += OnAboutClicked;
            var updateItem = new MenuFlyoutItem { Text = "Check for Update" };
            updateItem.Click += OnCheckForUpdateClicked;
            _moreMenu.Items.Add(githubItem);
            _moreMenu.Items.Add(updateItem);
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnMoreClicked(object sender, RoutedEventArgs e)
        {
            _moreMenu.ShowAt(MoreButton);
        }

        private async void OnAboutClicked(object sender, RoutedEventArgs e)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = "Developed by " });
            var link = new Windows.UI.Xaml.Documents.Hyperlink { NavigateUri = new Uri(GitHubUpdateService.RepositoryUrl) };
            link.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = "retiredlake" });
            text.Inlines.Add(link);
            await new ContentDialog { Title = "About", Content = text, PrimaryButtonText = "Close" }.ShowAsync();
        }

        private async void OnCheckForUpdateClicked(object sender, RoutedEventArgs e)
        {
            await CheckForUpdateAsync();
        }

        private async Task CheckForUpdateAsync()
        {
            if (_updateCheckInProgress) return;
            _updateCheckInProgress = true;
            try
            {
                StatusText.Text = "Checking GitHub for updates...";
                var service = new GitHubUpdateService();
                var release = await service.GetLatestReleaseAsync();
                if (release == null)
                {
                    StatusText.Text = "No published update is available.";
                    await new MessageDialog("No published update is available yet.", "Check for Update").ShowAsync();
                    return;
                }
                var currentId = Windows.ApplicationModel.Package.Current.Id.Version;
                var current = new Version(currentId.Major, currentId.Minor, currentId.Build, currentId.Revision);
                if (release.Version <= current)
                {
                    StatusText.Text = "LiveDrop is up to date.";
                    await new MessageDialog("You already have the latest release.", "No update available").ShowAsync();
                    return;
                }

                StatusText.Text = "Downloading v" + release.Version.ToString(4) + "...";
                var progress = new Progress<double>(value =>
                    StatusText.Text = "Downloading v" + release.Version.ToString(4) + " - " + (int)(value * 100) + "%");
                var file = await service.DownloadAsync(release, progress);
                StatusText.Text = "Opening the Windows installer...";
                if (!await Launcher.LaunchFileAsync(file))
                {
                    StatusText.Text = "Windows could not open the installer.";
                    await Launcher.LaunchUriAsync(new Uri(release.ReleaseUrl));
                }
            }
            catch (System.Net.Http.HttpRequestException)
            {
                StatusText.Text = "Could not reach GitHub. Check your connection and try again.";
            }
            catch (TaskCanceledException)
            {
                StatusText.Text = "The update check timed out. Please try again.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Update check failed: " + ex.Message;
            }
            finally
            {
                _updateCheckInProgress = false;
            }
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
            var adapter = sender as LiveDrop.Protocols.IShareProtocolAdapter;
            await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
            {
                _discoveryStatus[adapter == null ? "Discovery" : adapter.Name] = e.Message;
                if (!_updateCheckInProgress) StatusText.Text = string.Join("\n", _discoveryStatus.Values);
            });
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
        public override string ToString() { return Peer.DisplayName + " - " + Peer.Transport; }
    }
}
