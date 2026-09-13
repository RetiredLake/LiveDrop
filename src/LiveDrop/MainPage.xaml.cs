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
        private readonly System.Collections.Generic.Dictionary<string, DateTime> _peerLastSeen = new System.Collections.Generic.Dictionary<string, DateTime>();
        private readonly DispatcherTimer _peerExpiryTimer;
        private const int HostNameMinimumLength = 1;
        private const int HostNameMaximumLength = 32;
        private const string HostNameAllowedSpecialCharacters = " '._-()&+";
        private const int PeerTimeoutSeconds = 10;
        private const bool DefaultNearbyEnabled = false;
        private const bool DefaultQuickShareEnabled = true;

        public MainPage()
        {
            InitializeComponent();
            _viewDispatcher = Dispatcher;
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            NearbyCheckBox.IsChecked = ReadProtocolSetting(settings, "NearbyEnabled", DefaultNearbyEnabled);
            QuickShareCheckBox.IsChecked = ReadProtocolSetting(settings, "QuickShareEnabled", DefaultQuickShareEnabled);
            NearbyCheckBox.Checked += OnProtocolsChanged;
            NearbyCheckBox.Unchecked += OnProtocolsChanged;
            QuickShareCheckBox.Checked += OnProtocolsChanged;
            QuickShareCheckBox.Unchecked += OnProtocolsChanged;
            Window.Current.Closed += OnWindowClosed;
            _moreMenu = new MenuFlyout();
            var hostnameItem = new MenuFlyoutItem { Text = "Change Hostname" };
            hostnameItem.Click += OnChangeHostnameClicked;
            var githubItem = new MenuFlyoutItem { Text = "About" };
            githubItem.Click += OnAboutClicked;
            var updateItem = new MenuFlyoutItem { Text = "Check for Update" };
            updateItem.Click += OnCheckForUpdateClicked;
            _moreMenu.Items.Add(hostnameItem);
            _moreMenu.Items.Add(githubItem);
            _moreMenu.Items.Add(updateItem);
            _peerExpiryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _peerExpiryTimer.Tick += OnPeerExpiryTimerTick;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private static bool ReadProtocolSetting(
            Windows.Foundation.Collections.IPropertySet settings,
            string key,
            bool defaultValue)
        {
            var value = settings[key];
            return value is bool ? (bool)value : defaultValue;
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
            ResetTransferProgress();
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
            ResetTransferProgress();
        }

        private async void OnAboutClicked(object sender, RoutedEventArgs e)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            text.Inlines.Add(new Windows.UI.Xaml.Documents.Run
            {
                Text = "Version " + version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision
            });
            text.Inlines.Add(new Windows.UI.Xaml.Documents.LineBreak());
            text.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = "Developed by " });
            var link = new Windows.UI.Xaml.Documents.Hyperlink { NavigateUri = new Uri(GitHubUpdateService.RepositoryUrl) };
            link.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = "retiredlake" });
            text.Inlines.Add(link);
            await new ContentDialog { Title = "About", Content = text, PrimaryButtonText = "Close" }.ShowAsync();
        }

        private async void OnChangeHostnameClicked(object sender, RoutedEventArgs e)
        {
            var nameBox = new TextBox
            {
                Text = GetBroadcastName(),
                MaxLength = HostNameMaximumLength,
                PlaceholderText = "Letters, numbers, spaces, ' _ - . ( ) & +"
            };
            var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.DarkRed) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = "Choose the name other devices will see (1-32 letters, numbers, spaces, and ' _ - . ( ) & +).", TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(nameBox);
            panel.Children.Add(errorText);
            var dialog = new ContentDialog
            {
                Title = "Change Hostname",
                Content = panel,
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel"
            };
            dialog.PrimaryButtonClick += (d, args) =>
            {
                string error;
                if (!TryValidateHostName(nameBox.Text, out error))
                {
                    errorText.Text = error;
                    args.Cancel = true;
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            Windows.Storage.ApplicationData.Current.LocalSettings.Values["BroadcastHostName"] = nameBox.Text;
            StatusText.Text = "Restarting discovery as " + nameBox.Text + "...";
            await ApplyProtocolsAsync();
        }

        private string GetBroadcastName()
        {
            var value = Windows.Storage.ApplicationData.Current.LocalSettings.Values["BroadcastHostName"] as string;
            string error;
            if (TryValidateHostName(value, out error)) return value;
            return NormalizeDefaultHostName(ShareCoordinator.GetHostDisplayName("LiveDrop"));
        }

        private static string NormalizeDefaultHostName(string value)
        {
            var result = new System.Text.StringBuilder();
            foreach (var character in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(character) || HostNameAllowedSpecialCharacters.IndexOf(character) >= 0)
                {
                    result.Append(character);
                    if (result.Length == HostNameMaximumLength) break;
                }
            }
            return result.Length == 0 ? "LiveDrop" : result.ToString();
        }

        private static bool TryValidateHostName(string value, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(value) || value.Length < HostNameMinimumLength)
            {
                error = "Enter at least one letter or number.";
                return false;
            }
            if (value.Length > HostNameMaximumLength)
            {
                error = "Use no more than " + HostNameMaximumLength + " characters.";
                return false;
            }
            if (value[0] == ' ' || value[value.Length - 1] == ' ')
            {
                error = "Spaces cannot be at the beginning or end.";
                return false;
            }
            foreach (var character in value)
            {
                if (!char.IsLetterOrDigit(character) && HostNameAllowedSpecialCharacters.IndexOf(character) < 0)
                {
                    error = "Use letters, numbers, spaces, and ' _ - . ( ) & + only.";
                    return false;
                }
            }
            return true;
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
            _peerExpiryTimer.Start();
            await ApplyProtocolsAsync();
        }

        private async void OnUnloaded(object sender, RoutedEventArgs e)
        {
            _loaded = false;
            _peerExpiryTimer.Stop();
            await ApplyProtocolsAsync();
        }

        private async void OnProtocolsChanged(object sender, RoutedEventArgs e)
        {
            var settings = Windows.Storage.ApplicationData.Current.LocalSettings.Values;
            settings["NearbyEnabled"] = NearbyCheckBox.IsChecked == true;
            settings["QuickShareEnabled"] = QuickShareCheckBox.IsChecked == true;
            if (_loaded) await ApplyProtocolsAsync();
        }

        private async void OnWindowClosed(object sender, CoreWindowEventArgs e)
        {
            _viewClosed = true;
            _loaded = false;
            _peerExpiryTimer.Stop();
            TryReportShareError("Share canceled.");
            if (_sendCancellation != null) _sendCancellation.Cancel();
            await ApplyProtocolsAsync();
        }

        private async void OnShareClicked(object sender, RoutedEventArgs e)
        {
            if (_shareTargetSession || _filePickerPending) return;
            _filePickerPending = true;
            try
            {
                var phonePicker = IsWindowsPhonePicker();
                var picker = CreateFilePicker(phonePicker);
                StatusText.Text = "Select a file from Photos or File Explorer.";
                if (phonePicker)
                {
                    // WpBlueBubbles uses the multiple-file broker on Windows 10 Mobile.
                    // Pick the first item here because LiveDrop sends one selected file.
                    var files = await picker.PickMultipleFilesAsync();
                    await CompletePickedFileAsync(files == null ? null : files.FirstOrDefault());
                }
                else
                {
                    await CompletePickedFileAsync(await picker.PickSingleFileAsync());
                }
            }
            catch (Exception ex)
            {
                StatusText.Text = "Could not open the file picker: " + ex.Message;
            }
            finally
            {
                _filePickerPending = false;
            }
        }

        private static FileOpenPicker CreateFilePicker(bool phonePicker)
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            if (!phonePicker) picker.ViewMode = PickerViewMode.Thumbnail;
            var extensions = phonePicker ? new[]
            {
                // Keep the Windows 10 Mobile broker filter aligned with WpBlueBubbles.
                ".jpg", ".jpeg", ".png", ".heic", ".gif",
                ".mp4", ".m4v", ".mov", ".wmv", ".pdf"
            } : new[]
            {
                ".jpg", ".jpeg", ".png", ".heic", ".gif", ".bmp", ".webp",
                ".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv",
                ".mp3", ".m4a", ".wav", ".flac", ".pdf", ".txt",
                ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx",
                ".csv", ".json", ".xml", ".zip", ".7z", ".rar"
            };
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);
            return picker;
        }

        private static bool IsWindowsPhonePicker()
        {
            return string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Mobile", StringComparison.OrdinalIgnoreCase) ||
                ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons");
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
            ResetTransferProgress();
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
            ResetTransferProgress();
            StatusText.Text = "Choose a file to share.";
        }

        private void ResetTransferProgress()
        {
            TransferProgressPanel.Visibility = Visibility.Collapsed;
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressBar.Value = 0;
            TransferProgressText.Text = string.Empty;
        }

        private void ShowTransferProgress(ShareProgress progress)
        {
            if (progress == null) return;
            TransferProgressPanel.Visibility = Visibility.Visible;
            var total = progress.TotalBytes;
            if (total <= 0)
            {
                TransferProgressBar.IsIndeterminate = false;
                TransferProgressBar.Value = 1;
                TransferProgressText.Text = "0 B";
                return;
            }
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressBar.Value = Math.Min(1.0, Math.Max(0.0, (double)progress.BytesTransferred / total));
            TransferProgressText.Text = ((int)(TransferProgressBar.Value * 100)) + "%";
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
                _peerLastSeen.Clear();
                SendButton.IsEnabled = false;
                _discoveryStatus.Clear();
                var nearby = NearbyCheckBox.IsChecked == true;
                var quickShare = QuickShareCheckBox.IsChecked == true;
                if (!nearby && !quickShare)
                {
                    StatusText.Text = "Discovery is off. Select a protocol to find peers.";
                    return;
                }
                _coordinator = new ShareCoordinator(GetBroadcastName(), nearby, quickShare);
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
                var key = PeerListItem.GetKey(e.Peer);
                _peerLastSeen[key] = DateTime.UtcNow;
                var existing = PeersList.Items.OfType<PeerListItem>().FirstOrDefault(x => x.Matches(e.Peer));
                if (existing == null)
                    PeersList.Items.Add(new PeerListItem(e.Peer, NearbyCheckBox.IsChecked == true && QuickShareCheckBox.IsChecked == true));
                else
                    existing.Update(e.Peer);
            });
        }

        private void OnPeerExpiryTimerTick(object sender, object e)
        {
            if (!_loaded || _viewClosed) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromSeconds(PeerTimeoutSeconds);
            var stale = PeersList.Items.OfType<PeerListItem>().Where(item =>
            {
                DateTime lastSeen;
                return !_peerLastSeen.TryGetValue(item.Key, out lastSeen) || lastSeen <= cutoff;
            }).ToList();
            foreach (var item in stale)
            {
                if (ReferenceEquals(PeersList.SelectedItem, item)) PeersList.SelectedItem = null;
                PeersList.Items.Remove(item);
                _peerLastSeen.Remove(item.Key);
            }
        }

        private async void OnStatusChanged(object sender, StatusChangedEventArgs e)
        {
            var adapter = sender as LiveDrop.Protocols.IShareProtocolAdapter;
            var coordinator = _coordinator;
            await RunOnViewAsync(() =>
            {
                if (!_loaded || !ReferenceEquals(coordinator, _coordinator)) return;
                _discoveryStatus[adapter == null ? "Discovery" : adapter.Name] = e.Message;
                if (adapter != null)
                {
                    if (e.Message.IndexOf("transfer received", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TransferNotification.Show("Share complete", e.Message);
                    }
                    else if (e.Message.IndexOf("transfer failed", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TransferNotification.Show("Share failed", e.Message);
                    }
                }
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
                var outgoingName = _pendingOffer.Files.Count == 0 ? "file" : _pendingOffer.Files[0].Name;
                TransferNotification.Show("Sending", "Sending " + outgoingName + " to " + selected.Peer.DisplayName + ".");
                StatusText.Text = "Sending " + outgoingName + " to " + selected.Peer.DisplayName + "...";
                var progress = new Progress<ShareProgress>(value =>
                {
                    if (!_viewClosed) ShowTransferProgress(value);
                });
                await _coordinator.SendAsync(selected.Peer, _pendingOffer, progress, _sendCancellation.Token);
                if (_viewClosed) return;
                TryReportShareCompleted();
                var completedSize = _pendingOffer.Files.Count == 0 ? 0 : _pendingOffer.Files[0].Size;
                ShowTransferProgress(new ShareProgress(outgoingName, completedSize, completedSize));
                TransferNotification.Show("Share complete", "Sent " + outgoingName + " to " + selected.Peer.DisplayName + ".");
                _pendingOffer = null;
                if (!_shareTargetSession)
                {
                    SendButton.Visibility = Visibility.Collapsed;
                    CancelButton.Visibility = Visibility.Collapsed;
                    ShareButton.Visibility = Visibility.Visible;
                }
                StatusText.Text = "Share completed.";
            }
            catch (OperationCanceledException)
            {
                if (!_viewClosed)
                {
                    StatusText.Text = "Share canceled.";
                    TransferNotification.Show("Share canceled", "The file share was canceled.");
                }
            }
            catch (Exception ex)
            {
                if (!_viewClosed)
                {
                    StatusText.Text = "Share failed: " + ex.Message;
                    TransferNotification.Show("Share failed", ex.Message);
                }
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
            var fileName = e.Offer == null || e.Offer.Files.Count == 0 ? "file" : e.Offer.Files[0].Name;
            e.ProgressChanged += progress =>
            {
                _ = RunOnViewAsync(() => ShowTransferProgress(progress));
            };
            try
            {
                await RunOnViewAsync(() =>
                {
                    if (!_loaded || !ReferenceEquals(sender, _coordinator)) return;
                    SelectedFileText.Text = fileName;
                    SelectedFileText.Visibility = Visibility.Visible;
                    ResetTransferProgress();
                    TransferProgressPanel.Visibility = Visibility.Visible;
                    TransferProgressText.Text = "Accepting";
                    TransferNotification.Show("Incoming share", "Receiving " + fileName + " from " + e.Peer.DisplayName + ".");
                    StatusText.Text = "Receiving " + fileName + ". Files are saved automatically in Downloads/LiveDrop.";
                });
                // LiveDrop sessions accept automatically so two LiveDrop
                // clients cannot wait forever for a consent UI. The protocol
                // layer writes each completed file to Downloads/LiveDrop.
                if (e.CompleteAsync != null) await e.CompleteAsync(!_viewClosed);
            }
            catch
            {
                try { if (e.CompleteAsync != null) await e.CompleteAsync(false); } catch { }
                await RunOnViewAsync(() => ResetTransferProgress());
            }
        }
    }

    internal sealed class PeerListItem
    {
        private readonly bool _showProtocol;
        internal PeerDescriptor Peer { get; private set; }
        internal PeerListItem(PeerDescriptor peer, bool showProtocol) { Peer = peer; _showProtocol = showProtocol; }
        internal string Key { get { return GetKey(Peer); } }
        internal void Update(PeerDescriptor peer) { if (peer != null) Peer = peer; }
        internal static string GetKey(PeerDescriptor peer)
        {
            if (peer == null) return string.Empty;
            var identity = !string.IsNullOrWhiteSpace(peer.StableId)
                ? peer.StableId
                : peer.Address + ":" + peer.Port;
            return peer.Transport + "|" + identity;
        }
        internal bool Matches(PeerDescriptor peer)
        {
            if (peer == null || Peer == null || Peer.Transport != peer.Transport) return false;
            if (!string.IsNullOrWhiteSpace(Peer.StableId) && !string.IsNullOrWhiteSpace(peer.StableId))
                return string.Equals(Peer.StableId, peer.StableId, StringComparison.OrdinalIgnoreCase);
            return string.Equals(Peer.Address, peer.Address, StringComparison.OrdinalIgnoreCase) && Peer.Port == peer.Port;
        }
        public override string ToString()
        {
            return Peer.DisplayName + (_showProtocol ? " - " + (Peer.Transport == ShareTransport.GoogleQuickShare ? "Quick Share" : "Microsoft Nearby Share") : "");
        }
    }
}
