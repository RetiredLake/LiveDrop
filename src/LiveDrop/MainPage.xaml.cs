using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Activation;
using Windows.ApplicationModel.DataTransfer.ShareTarget;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.System.Profile;
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
        private readonly CoreDispatcher _viewDispatcher;
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        private bool _viewClosed;
        private bool _loaded;
        private bool _shareTargetSession;
        private bool _filePickerPending;
        private bool _shareOperationStarted;
        private readonly System.Collections.Generic.Dictionary<string, string> _discoveryStatus = new System.Collections.Generic.Dictionary<string, string>();

        public MainPage()
        {
            InitializeComponent();
            _viewDispatcher = Dispatcher;
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            NearbyCheckBox.IsChecked = !(settings["NearbyEnabled"] is bool) || (bool)settings["NearbyEnabled"];
            QuickShareCheckBox.IsChecked = !(settings["QuickShareEnabled"] is bool) || (bool)settings["QuickShareEnabled"];
            NearbyCheckBox.Checked += OnProtocolsChanged;
            NearbyCheckBox.Unchecked += OnProtocolsChanged;
            QuickShareCheckBox.Checked += OnProtocolsChanged;
            QuickShareCheckBox.Unchecked += OnProtocolsChanged;
            Window.Current.Closed += OnWindowClosed;
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

        internal void BeginRegularSession()
        {
            _shareTargetSession = false;
            _pendingShareOperation = null;
            _pendingOffer = null;
            _shareOperationStarted = false;
            MoreButton.Visibility = Visibility.Visible;
            ShareButton.Visibility = Visibility.Visible;
            SendButton.Visibility = Visibility.Collapsed;
            SendButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Collapsed;
            SelectedFileText.Text = string.Empty;
            SelectedFileText.Visibility = Visibility.Collapsed;
        }

        internal void BeginShareTargetSession()
        {
            _shareTargetSession = true;
            _pendingShareOperation = null;
            _pendingOffer = null;
            _shareOperationStarted = false;
            MoreButton.Visibility = Visibility.Collapsed;
            ShareButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            SelectedFileText.Visibility = Visibility.Collapsed;
            SendButton.Visibility = Visibility.Visible;
            SendButton.IsEnabled = false;
            SendButton.Content = "Send selected share";
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
            _loaded = true;
            await ApplyProtocolsAsync();
        }

        private async void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            await ApplyProtocolsAsync();
        }

        private async void OnWindowClosed(object sender, CoreWindowEventArgs e)
        {
            _viewClosed = true;
            _loaded = false;
            TryReportShareError("Share canceled.");
            if (_sendCancellation != null) _sendCancellation.Cancel();
            await ApplyProtocolsAsync();
        }

        private async void OnProtocolsChanged(object sender, RoutedEventArgs e)
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            settings["NearbyEnabled"] = NearbyCheckBox.IsChecked == true;
            settings["QuickShareEnabled"] = QuickShareCheckBox.IsChecked == true;
            if (_loaded) await ApplyProtocolsAsync();
        }

        private async void OnShareClicked(object sender, RoutedEventArgs e)
        {
            if (_shareTargetSession || _filePickerPending) return;
            _filePickerPending = true;
            try
            {
                var picker = new FileOpenPicker
                {
                    ViewMode = PickerViewMode.Thumbnail,
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary
                };
                picker.FileTypeFilter.Add("*");
                StatusText.Text = "Choose a file from Photos or File Explorer.";
                if (IsWindowsPhonePicker())
                {
#pragma warning disable 0618
                    picker.ContinuationData["LiveDrop.FilePicker"] = true;
                    picker.PickSingleFileAndContinue();
#pragma warning restore 0618
                    return;
                }

                await CompletePickedFileAsync(await picker.PickSingleFileAsync());
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not open the file picker: " + ex.Message;
            }
            finally
            {
                if (!IsWindowsPhonePicker())
                    _filePickerPending = false;
            }
        }

        private static bool IsWindowsPhonePicker()
        {
            return string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Mobile", StringComparison.OrdinalIgnoreCase) ||
                ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons");
        }

        internal async void CompleteFilePicker(FileOpenPickerContinuationEventArgs args)
        {
            _filePickerPending = false;
            await CompletePickedFileAsync(args == null || args.Files == null ? null : args.Files.FirstOrDefault());
        }

        private async Task CompletePickedFileAsync(StorageFile file)
        {
            if (file == null)
            {
                StatusText.Text = "No file selected.";
                return;
            }

            _pendingOffer = await ShareOfferFactory.FromPickedFileAsync(file);
            SelectedFileText.Text = file.Name;
            SelectedFileText.Visibility = Visibility.Visible;
            ShareButton.Visibility = Visibility.Collapsed;
            SendButton.Visibility = Visibility.Visible;
            SendButton.Content = "Send selected share";
            SendButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            StatusText.Text = "Select a peer, then send the file.";
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            if (_shareTargetSession) return;
            _pendingOffer = null;
            _pendingShareOperation = null;
            SelectedFileText.Text = string.Empty;
            SelectedFileText.Visibility = Visibility.Collapsed;
            SendButton.Visibility = Visibility.Collapsed;
            SendButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Collapsed;
            ShareButton.Visibility = Visibility.Visible;
            StatusText.Text = "Choose a file to share.";
        }

        private async Task ApplyProtocolsAsync()
        {
            await _lifecycle.WaitAsync();
            try
            {
                var previous = _coordinator;
                _coordinator = null;
                if (previous != null)
                {
                    previous.PeerDiscovered -= OnPeerDiscovered;
                    previous.OfferReceived -= OnOfferReceived;
                    previous.StatusChanged -= OnStatusChanged;
                    await previous.StopAsync();
                    previous.Dispose();
                }
                if (_viewClosed || !_loaded) return;
                PeersList.Items.Clear();
                SendButton.IsEnabled = false;
                _discoveryStatus.Clear();
                var nearby = NearbyCheckBox.IsChecked == true;
                var quickShare = QuickShareCheckBox.IsChecked == true;
                if (!nearby && !quickShare)
                {
                    StatusText.Text = "Discovery is off. Select a protocol to find peers.";
                    return;
                }
                _coordinator = new ShareCoordinator("LiveDrop", nearby, quickShare);
                _coordinator.PeerDiscovered += OnPeerDiscovered;
                _coordinator.OfferReceived += OnOfferReceived;
                _coordinator.StatusChanged += OnStatusChanged;
                await _coordinator.StartAsync();
            }
            catch (Exception ex)
            {
                await RunOnViewAsync(() => StatusText.Text = "Discovery could not start: " + ex.Message);
            }
            finally { _lifecycle.Release(); }
        }

        // A share-target view can disappear while discovery callbacks are still queued.
        // Capture its dispatcher once; never query a disposed XAML Page for Dispatcher.
        private async Task RunOnViewAsync(Action action)
        {
            if (_viewClosed) return;
            try
            {
                await _viewDispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    try { if (!_viewClosed) action(); }
                    catch (System.Runtime.InteropServices.InvalidComObjectException) { _viewClosed = true; }
                    catch (System.Runtime.InteropServices.COMException) { _viewClosed = true; }
                });
            }
            catch (System.Runtime.InteropServices.InvalidComObjectException) { }
            catch (System.Runtime.InteropServices.COMException) { }
            catch (TaskCanceledException) { }
        }

        internal async Task ReceiveShareAsync(ShareOperation operation)
        {
            try
            {
                operation.ReportStarted();
                _shareOperationStarted = true;
                var offer = await ShareOfferFactory.FromShareOperationAsync(operation);
                if (_viewClosed) return;
                operation.ReportDataRetrieved();
                _pendingShareOperation = operation;
                _pendingOffer = offer;
                StatusText.Text = offer.Files.Count == 0
                    ? "This share contains no supported files or text."
                    : offer.Files.Count + " file(s) are ready to share after selecting a peer.";
                SendButton.Visibility = Visibility.Visible;
                SendButton.IsEnabled = PeersList.SelectedItem != null && offer.Files.Count > 0;
            }
            catch (Exception ex)
            {
                _pendingShareOperation = null;
                _pendingOffer = null;
                await RunOnViewAsync(() => StatusText.Text = "Could not read the shared content: " + ex.Message);
                TryReportShareError(operation, "LiveDrop could not read the shared content. Please share it again.");
            }
        }

        private async void OnPeerDiscovered(object sender, PeerDiscoveredEventArgs e)
        {
            await RunOnViewAsync(() =>
            {
                if (!_loaded || !ReferenceEquals(sender, _coordinator)) return;
                if (!PeersList.Items.OfType<PeerListItem>().Any(x => x.Peer.StableId == e.Peer.StableId && x.Peer.Transport == e.Peer.Transport))
                    PeersList.Items.Add(new PeerListItem(e.Peer, NearbyCheckBox.IsChecked == true && QuickShareCheckBox.IsChecked == true));
            });
        }

        private async void OnStatusChanged(object sender, StatusChangedEventArgs e)
        {
            var adapter = sender as LiveDrop.Protocols.IShareProtocolAdapter;
            var coordinator = _coordinator;
            await RunOnViewAsync(() =>
            {
                if (!_loaded || !ReferenceEquals(coordinator, _coordinator)) return;
                _discoveryStatus[adapter == null ? "Discovery" : adapter.Name] = e.Message;
                if (!_updateCheckInProgress) StatusText.Text = string.Join("\n", _discoveryStatus.Values);
            });
        }

        private void OnPeerSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SendButton.IsEnabled = _pendingOffer != null && _pendingOffer.Files.Count > 0 &&
                (_shareTargetSession ? PeersList.SelectedItem != null : true);
        }

        private async void OnSendClicked(object sender, RoutedEventArgs e)
        {
            var selected = PeersList.SelectedItem as PeerListItem;
            if (_pendingOffer == null || _coordinator == null) return;
            if (selected == null)
            {
                StatusText.Text = "Select a peer first.";
                return;
            }
            SendButton.IsEnabled = false;
            NearbyCheckBox.IsEnabled = QuickShareCheckBox.IsEnabled = false;
            _sendCancellation = new CancellationTokenSource();
            try
            {
                var progress = new Progress<ShareProgress>(value =>
                {
                    if (!_viewClosed) StatusText.Text = value.FileName + ": " + value.BytesTransferred + "/" + value.TotalBytes + " bytes";
                });
                await _coordinator.SendAsync(selected.Peer, _pendingOffer, progress, _sendCancellation.Token);
                if (_viewClosed) return;
                TryReportShareCompleted();
                _pendingOffer = null;
                if (!_shareTargetSession)
                {
                    SelectedFileText.Text = string.Empty;
                    SelectedFileText.Visibility = Visibility.Collapsed;
                    SendButton.Visibility = Visibility.Collapsed;
                    CancelButton.Visibility = Visibility.Collapsed;
                    ShareButton.Visibility = Visibility.Visible;
                }
                StatusText.Text = "Share completed.";
            }
            catch (OperationCanceledException)
            {
                if (!_viewClosed) StatusText.Text = "Share canceled.";
            }
            catch (Exception ex)
            {
                if (!_viewClosed) StatusText.Text = "Share failed: " + ex.Message;
                TryReportShareError(ex.Message);
            }
            finally
            {
                if (_sendCancellation != null) { _sendCancellation.Dispose(); _sendCancellation = null; }
                if (!_viewClosed)
                {
                    NearbyCheckBox.IsEnabled = QuickShareCheckBox.IsEnabled = true;
                    SendButton.IsEnabled = _pendingOffer != null &&
                        (_shareTargetSession ? PeersList.SelectedItem != null : true);
                }
            }
        }

        private void TryReportShareCompleted()
        {
            var operation = _pendingShareOperation;
            _pendingShareOperation = null;
            _shareOperationStarted = false;
            if (operation == null) return;
            try { operation.ReportCompleted(); } catch { }
        }

        private void TryReportShareError(string message)
        {
            var operation = _pendingShareOperation;
            _pendingShareOperation = null;
            _shareOperationStarted = false;
            TryReportShareError(operation, message);
        }

        private void TryReportShareError(ShareOperation operation, string message)
        {
            if (operation == null || !_shareOperationStarted) return;
            try { operation.ReportError(message); } catch { }
            _shareOperationStarted = false;
        }

        private async void OnOfferReceived(object sender, ShareOfferReceivedEventArgs e)
        {
            var accepted = false;
            try
            {
                Task<IUICommand> dialogTask = null;
                await RunOnViewAsync(() =>
                {
                    if (!_loaded || !ReferenceEquals(sender, _coordinator)) return;
                    var dialog = new MessageDialog(e.Peer.DisplayName + " wants to send " + e.Offer.Files.Count + " file(s).", "Accept nearby transfer?");
                    dialog.Commands.Add(new UICommand("Accept"));
                    dialog.Commands.Add(new UICommand("Reject"));
                    dialog.DefaultCommandIndex = 1;
                    dialog.CancelCommandIndex = 1;
                    dialogTask = dialog.ShowAsync().AsTask();
                });
                if (dialogTask != null) accepted = (await dialogTask).Label == "Accept" && !_viewClosed;
            }
            catch { accepted = false; }
            try { await e.CompleteAsync(accepted); } catch { }
        }
    }

    internal sealed class PeerListItem
    {
        internal PeerDescriptor Peer { get; private set; }
        private readonly bool _showProtocol;
        internal PeerListItem(PeerDescriptor peer, bool showProtocol) { Peer = peer; _showProtocol = showProtocol; }
        public override string ToString()
        {
            return Peer.DisplayName + (_showProtocol ? " - " + (Peer.Transport == ShareTransport.GoogleQuickShare ? "Quick Share" : "Microsoft Nearby Share") : "");
        }
    }
}
