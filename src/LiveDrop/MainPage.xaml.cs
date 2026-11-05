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
        private ShareOfferReceivedEventArgs _incomingTransfer;
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
        private bool _incomingTelemetryPending;
        private ShareTransport _incomingTelemetryTransport;
        private ShareOffer _incomingTelemetryOffer;
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
            TelemetryService.Instance.TrackApplicationStarted();
            _moreMenu = new MenuFlyout();
            var hostnameItem = new MenuFlyoutItem { Text = AppText.Get("ChangeHostname") };
            hostnameItem.Click += OnChangeHostnameClicked;
            var githubItem = new MenuFlyoutItem { Text = AppText.Get("About") };
            githubItem.Click += OnAboutClicked;
#if !MICROSOFT_STORE
            var updateItem = new MenuFlyoutItem { Text = AppText.Get("CheckForUpdate") };
            updateItem.Click += OnCheckForUpdateClicked;
#endif
            _moreMenu.Items.Add(hostnameItem);
            _moreMenu.Items.Add(githubItem);
#if !MICROSOFT_STORE
            _moreMenu.Items.Add(updateItem);
#endif
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
            if (_incomingTransfer != null) return;
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
            SendButton.Content = AppText.Get("SendSelectedShare");
            ResetTransferProgress();
        }

        private async void OnAboutClicked(object sender, RoutedEventArgs e)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var version = Windows.ApplicationModel.Package.Current.Id.Version;
            text.Inlines.Add(new Windows.UI.Xaml.Documents.Run
            {
                Text = AppText.Format("AboutVersion", new Version(version.Major, version.Minor, version.Build, version.Revision).ToString(4))
            });
            text.Inlines.Add(new Windows.UI.Xaml.Documents.LineBreak());
            var credit = AppText.Get("DevelopedBy").Split(new[] { "{0}" }, StringSplitOptions.None);
            text.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = credit[0] });
            var link = new Windows.UI.Xaml.Documents.Hyperlink { NavigateUri = new Uri(GitHubUpdateService.RepositoryUrl) };
            link.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = "retiredlake" });
            text.Inlines.Add(link);
            if (credit.Length > 1) text.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = credit[1] });
            text.Inlines.Add(new Windows.UI.Xaml.Documents.LineBreak());
            text.Inlines.Add(new Windows.UI.Xaml.Documents.LineBreak());
            var privacyLink = new Windows.UI.Xaml.Documents.Hyperlink { NavigateUri = new Uri("https://retiredlake.com/privacy.html") };
            privacyLink.Inlines.Add(new Windows.UI.Xaml.Documents.Run { Text = AppText.Get("PrivacyPolicy") });
            text.Inlines.Add(privacyLink);
            await new ContentDialog { Title = AppText.Get("About"), Content = text, PrimaryButtonText = AppText.Get("Close") }.ShowAsync();
        }

        private async void OnChangeHostnameClicked(object sender, RoutedEventArgs e)
        {
            var nameBox = new TextBox
            {
                Text = GetBroadcastName(),
                MaxLength = HostNameMaximumLength,
                PlaceholderText = AppText.Get("LettersNumbersSpaces")
            };
            var errorText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new Windows.UI.Xaml.Media.SolidColorBrush(Windows.UI.Colors.DarkRed) };
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = AppText.Get("HostnameInstructions"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(nameBox);
            panel.Children.Add(errorText);
            var dialog = new ContentDialog
            {
                Title = AppText.Get("ChangeHostname"),
                Content = panel,
                PrimaryButtonText = AppText.Get("OK"),
                CloseButtonText = AppText.Get("Cancel")
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
            StatusText.Text = AppText.Format("RestartingDiscovery", nameBox.Text);
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
                error = AppText.Get("EnterAtLeastOneLetterOrNumber");
                return false;
            }
            if (value.Length > HostNameMaximumLength)
            {
                error = AppText.Format("HostnameMaximumLength", HostNameMaximumLength);
                return false;
            }
            if (value[0] == ' ' || value[value.Length - 1] == ' ')
            {
                error = AppText.Get("SpacesCannotBeAtTheBeginningOrEnd");
                return false;
            }
            foreach (var character in value)
            {
                if (!char.IsLetterOrDigit(character) && HostNameAllowedSpecialCharacters.IndexOf(character) < 0)
                {
                    error = AppText.Get("UseLettersNumbersSpacesAndOnly");
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
            TelemetryService.Instance.TrackUpdateCheck("started");
            try
            {
                StatusText.Text = AppText.Get("CheckingGitHubForUpdates");
                var service = new GitHubUpdateService();
                var release = await service.GetLatestReleaseAsync();
                if (release == null)
                {
                    TelemetryService.Instance.TrackUpdateCheck("no_release");
                    StatusText.Text = AppText.Get("NoPublishedUpdateIsAvailable");
                    await new MessageDialog(AppText.Get("NoPublishedUpdateIsAvailableYet"), AppText.Get("CheckForUpdate")).ShowAsync();
                    return;
                }
                var currentId = Windows.ApplicationModel.Package.Current.Id.Version;
                var current = new Version(currentId.Major, currentId.Minor, currentId.Build, currentId.Revision);
                if (release.Version <= current)
                {
                    TelemetryService.Instance.TrackUpdateCheck("up_to_date");
                    StatusText.Text = AppText.Get("LiveDropIsUpToDate");
                    await new MessageDialog(AppText.Get("YouAlreadyHaveTheLatestRelease"), AppText.Get("NoUpdateAvailable")).ShowAsync();
                    return;
                }

                TelemetryService.Instance.TrackUpdateCheck("update_available");
                TelemetryService.Instance.TrackUpdateAvailable(release.Version);
                StatusText.Text = AppText.Format("DownloadingVersion", release.Version.ToString(4));
                TelemetryService.Instance.TrackUpdateDownloadStarted(release.Version);
                var progress = new Progress<double>(value =>
                    StatusText.Text = AppText.Format("DownloadingVersionProgress", release.Version.ToString(4), value));
                var file = await service.DownloadAsync(release, progress);
                StatusText.Text = AppText.Get("OpeningTheWindowsInstaller");
                if (!await Launcher.LaunchFileAsync(file))
                {
                    StatusText.Text = AppText.Get("WindowsCouldNotOpenTheInstaller");
                    await Launcher.LaunchUriAsync(new Uri(release.ReleaseUrl));
                }
                else
                {
                    TelemetryService.Instance.TrackUpdateInstallHandoff(release.Version);
                }
            }
            catch (System.Net.Http.HttpRequestException)
            {
                TelemetryService.Instance.TrackUpdateFailed("network");
                StatusText.Text = AppText.Get("CouldNotReachGitHubCheckYourConnectionAnd");
            }
            catch (TaskCanceledException)
            {
                TelemetryService.Instance.TrackUpdateFailed("timeout");
                StatusText.Text = AppText.Get("TheUpdateCheckTimedOutPleaseTryAgain");
            }
            catch (Exception ex)
            {
                TelemetryService.Instance.TrackUpdateFailed(TelemetryService.ClassifyFailure(ex));
                StatusText.Text = AppText.Format("UpdateCheckFailed", ex.Message);
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
            if (_incomingTelemetryPending)
            {
                TelemetryService.Instance.TrackShareCanceled(_incomingTelemetryTransport, "receive", _incomingTelemetryOffer);
                _incomingTelemetryPending = false;
            }
            TryReportShareError(AppText.Get("ShareCanceledMessage"));
            if (_sendCancellation != null) _sendCancellation.Cancel();
            _incomingTransfer?.CancelTransfer();
            await ApplyProtocolsAsync();
        }

        private async void OnShareClicked(object sender, RoutedEventArgs e)
        {
            if (_incomingTransfer != null)
            {
                ShareButton.IsEnabled = false;
                StatusText.Text = AppText.Get("Canceling");
                _incomingTransfer.CancelTransfer();
                return;
            }
            if (_shareTargetSession || _filePickerPending) return;
            _filePickerPending = true;
            try
            {
                var phonePicker = IsWindowsPhonePicker();
                var picker = CreateFilePicker(phonePicker);
                StatusText.Text = AppText.Get("SelectAFileFromPhotosOrFileExplorer");
                // Use the multiple-file broker on every UWP target. This keeps
                // regular launches consistent with the Windows share target and
                // preserves every item selected by Quick Share-compatible sources.
                await CompletePickedFilesAsync(await picker.PickMultipleFilesAsync());
            }
            catch (Exception ex)
            {
                StatusText.Text = AppText.Format("PickerFailed", ex.Message);
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
            picker.FileTypeFilter.Add("*");
            return picker;
        }

        private static bool IsWindowsPhonePicker()
        {
            return string.Equals(AnalyticsInfo.VersionInfo.DeviceFamily, "Windows.Mobile", StringComparison.OrdinalIgnoreCase) ||
                ApiInformation.IsTypePresent("Windows.Phone.UI.Input.HardwareButtons");
        }

        private async Task CompletePickedFilesAsync(System.Collections.Generic.IReadOnlyList<StorageFile> files)
        {
            if (files == null || files.Count == 0)
            {
                StatusText.Text = AppText.Get("NoFileSelected");
                return;
            }

            _pendingOffer = await ShareOfferFactory.FromPickedFilesAsync(files);
            if (_pendingOffer.Files.Count == 0)
            {
                StatusText.Text = AppText.Get("NoFileSelected");
                return;
            }
            SelectedFileText.Text = GetOfferDisplayName(_pendingOffer);
            SelectedFileText.Visibility = Visibility.Visible;
            ResetTransferProgress();
            ShareButton.Visibility = Visibility.Collapsed;
            SendButton.Visibility = Visibility.Visible;
            SendButton.Content = AppText.Get("SendSelectedShare");
            SendButton.IsEnabled = true;
            CancelButton.Visibility = Visibility.Visible;
            StatusText.Text = AppText.Get("SelectAPeerThenSendTheFile");
        }

        private static string GetOfferDisplayName(ShareOffer offer)
        {
            if (offer != null && offer.WebLink != null) return offer.WebLink.AbsoluteUri;
            if (offer == null || offer.Files == null || offer.Files.Count == 0) return AppText.Get("FileLabel");
            if (offer.Files.Count == 1) return offer.Files[0].Name;
            return AppText.Format("FileCount", offer.Files.Count);
        }

        private void OnCancelClicked(object sender, RoutedEventArgs e)
        {
            if (_shareTargetSession) return;
            if (_sendCancellation != null)
            {
                CancelButton.IsEnabled = false;
                StatusText.Text = AppText.Get("Canceling");
                _sendCancellation.Cancel();
                return;
            }
            ResetSelectedShare(AppText.Get("ChooseAFileToShare"));
        }

        private void ResetSelectedShare(string status)
        {
            _pendingOffer = null;
            _pendingShareOperation = null;
            SelectedFileText.Text = string.Empty;
            SelectedFileText.Visibility = Visibility.Collapsed;
            SendButton.Visibility = Visibility.Collapsed;
            SendButton.IsEnabled = false;
            CancelButton.Visibility = Visibility.Collapsed;
            CancelButton.IsEnabled = true;
            ShareButton.Visibility = Visibility.Visible;
            ResetTransferProgress();
            StatusText.Text = status;
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
                TransferProgressText.Text = AppText.Get("EmptyTransferSize");
                return;
            }
            TransferProgressBar.IsIndeterminate = false;
            TransferProgressBar.Value = Math.Min(1.0, Math.Max(0.0, (double)progress.BytesTransferred / total));
            TransferProgressText.Text = TransferProgressBar.Value.ToString("P0", System.Globalization.CultureInfo.CurrentCulture);
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
                    StatusText.Text = AppText.Get("DiscoveryIsOffSelectAProtocolToFind");
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
                await RunOnViewAsync(() => StatusText.Text = AppText.Format("DiscoveryFailed", ex.Message));
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
                StatusText.Text = !offer.HasContent
                    ? AppText.Get("ThisShareContainsNoSupportedFilesOrText")
                    : offer.WebLink != null ? AppText.Get("WebLinkIsReadyToShareAfterSelecting") : (offer.Files.Count == 1 ? AppText.Get("OneFileReady") : AppText.Format("FilesReady", offer.Files.Count));
                SelectedFileText.Text = GetOfferDisplayName(offer);
                SelectedFileText.Visibility = Visibility.Visible;
                SendButton.Visibility = Visibility.Visible;
                SendButton.IsEnabled = PeersList.SelectedItem != null && offer.HasContent;
            }
            catch (Exception ex)
            {
                _pendingShareOperation = null;
                _pendingOffer = null;
                await RunOnViewAsync(() => StatusText.Text = AppText.Format("ReadShareFailed", ex.Message));
                TryReportShareError(operation, AppText.Get("LiveDropCouldNotReadTheSharedContentPlease"));
            }
        }

        private async void OnPeerDiscovered(object sender, PeerDiscoveredEventArgs e)
        {
            await RunOnViewAsync(() =>
            {
                if (!_loaded || !ReferenceEquals(sender, _coordinator)) return;
                if (e.Peer == null || LiveDrop.Protocols.ProtocolUtilities.IsLoopbackAddress(e.Peer.Address)) return;
                var key = PeerListItem.GetKey(e.Peer);
                var matches = PeersList.Items.OfType<PeerListItem>().Where(x => x.Matches(e.Peer)).ToList();
                var existing = matches.FirstOrDefault();
                // One row per IP, preferring Quick Share when both adapters
                // discover the same device. Nearby traffic must not keep a
                // vanished Quick Share endpoint alive.
                if (existing != null && existing.Peer.Transport == ShareTransport.GoogleQuickShare && e.Peer.Transport != ShareTransport.GoogleQuickShare) return;
                _peerLastSeen[key] = DateTime.UtcNow;
                foreach (var duplicate in matches.Skip(1))
                {
                    if (ReferenceEquals(PeersList.SelectedItem, duplicate)) PeersList.SelectedItem = existing;
                    _peerLastSeen.Remove(duplicate.Key);
                    PeersList.Items.Remove(duplicate);
                }
                if (existing == null)
                    PeersList.Items.Add(new PeerListItem(e.Peer, NearbyCheckBox.IsChecked == true && QuickShareCheckBox.IsChecked == true));
                else
                {
                    _peerLastSeen.Remove(existing.Key);
                    existing.Update(e.Peer);
                    _peerLastSeen[existing.Key] = DateTime.UtcNow;
                }
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
                _discoveryStatus[adapter == null ? AppText.Get("DiscoveryLabel") : adapter.Name] = e.Message;
                if (!_updateCheckInProgress) StatusText.Text = string.Join("\n", _discoveryStatus.Values);
            });
        }

        private void OnPeerSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SendButton.IsEnabled = _pendingOffer != null && _pendingOffer.HasContent &&
                (_shareTargetSession ? PeersList.SelectedItem != null : true);
        }

        private async void OnSendClicked(object sender, RoutedEventArgs e)
        {
            var selected = PeersList.SelectedItem as PeerListItem;
            if (_incomingTransfer != null || _pendingOffer == null || _coordinator == null) return;
            if (selected == null)
            {
                StatusText.Text = AppText.Get("SelectAPeerFirst");
                return;
            }
            SendButton.IsEnabled = false;
            NearbyCheckBox.IsEnabled = QuickShareCheckBox.IsEnabled = false;
            _sendCancellation = new CancellationTokenSource();
            var notification = TransferNotification.Begin(AppText.Get("Sending"), AppText.Format("SendingFileBody", GetOfferDisplayName(_pendingOffer), selected.Peer.DisplayName), GetOfferDisplayName(_pendingOffer), AppText.Format("SendingToDevice", selected.Peer.DisplayName));
            try
            {
                var outgoingName = GetOfferDisplayName(_pendingOffer);
                var outgoingOffer = _pendingOffer;
                TelemetryService.Instance.TrackShareStarted(selected.Peer.Transport, "send", outgoingOffer);
                StatusText.Text = AppText.Format("SendingFileStatus", outgoingName, selected.Peer.DisplayName);
                var progress = new Progress<ShareProgress>(value =>
                {
                    notification.Update(value);
                    if (!_viewClosed && ReferenceEquals(_pendingOffer, outgoingOffer) && _sendCancellation != null && !_sendCancellation.IsCancellationRequested)
                        ShowTransferProgress(value);
                });
                await _coordinator.SendAsync(selected.Peer, outgoingOffer, progress, _sendCancellation.Token);
                TelemetryService.Instance.TrackShareSucceeded(selected.Peer.Transport, "send", outgoingOffer);
                notification.Complete(true, AppText.Format("SentFile", outgoingName, selected.Peer.DisplayName));
                if (_viewClosed) return;
                TryReportShareCompleted();
                if (outgoingOffer.Files.Count == 1)
                {
                    var completedSize = outgoingOffer.Files[0].Size;
                    ShowTransferProgress(new ShareProgress(outgoingName, completedSize, completedSize));
                }
                _pendingOffer = null;
                if (!_shareTargetSession)
                {
                    SendButton.Visibility = Visibility.Collapsed;
                    CancelButton.Visibility = Visibility.Collapsed;
                    ShareButton.Visibility = Visibility.Visible;
                }
                StatusText.Text = AppText.Get("ShareCompleted");
            }
            catch (OperationCanceledException)
            {
                notification.Complete(false, AppText.Get("ShareCanceledMessage"));
                TelemetryService.Instance.TrackShareCanceled(selected.Peer.Transport, "send", _pendingOffer);
                if (!_viewClosed)
                {
                    if (!_shareTargetSession) ResetSelectedShare(AppText.Get("ShareCanceledMessage"));
                    else StatusText.Text = AppText.Get("ShareCanceledMessage");
                }
            }
            catch (Exception ex)
            {
                notification.Complete(false, ex.Message);
                TelemetryService.Instance.TrackShareFailed(selected.Peer.Transport, "send", _pendingOffer, TelemetryService.ClassifyFailure(ex));
                if (!_viewClosed)
                {
                    StatusText.Text = AppText.Format("ShareFailedDetail", ex.Message);
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
            var oldShareVisibility = Visibility.Collapsed;
            var oldSendVisibility = Visibility.Collapsed;
            var oldCancelVisibility = Visibility.Collapsed;
            object oldShareContent = null;
            var oldSendEnabled = false;
            var oldNearbyEnabled = false;
            var oldQuickShareEnabled = false;
            var fileName = GetOfferDisplayName(e.Offer);
            await RunOnViewAsync(() =>
            {
                if (!_loaded || !ReferenceEquals(sender, _coordinator) || _incomingTransfer != null ||
                    _sendCancellation != null || _filePickerPending) return;
                _incomingTransfer = e;
                oldShareVisibility = ShareButton.Visibility;
                oldShareContent = ShareButton.Content;
                oldSendVisibility = SendButton.Visibility;
                oldSendEnabled = SendButton.IsEnabled;
                oldCancelVisibility = CancelButton.Visibility;
                oldNearbyEnabled = NearbyCheckBox.IsEnabled;
                oldQuickShareEnabled = QuickShareCheckBox.IsEnabled;
                ShareButton.Content = AppText.Get("Cancel");
                ShareButton.IsEnabled = true;
                ShareButton.Visibility = Visibility.Visible;
                SendButton.Visibility = CancelButton.Visibility = Visibility.Collapsed;
                NearbyCheckBox.IsEnabled = QuickShareCheckBox.IsEnabled = false;
                SelectedFileText.Text = fileName;
                SelectedFileText.Visibility = Visibility.Visible;
                ResetTransferProgress();
                TransferProgressPanel.Visibility = Visibility.Visible;
                TransferProgressText.Text = AppText.Get("Accepting");
                StatusText.Text = AppText.Format("ReceivingContent", fileName);
            });
            if (!ReferenceEquals(_incomingTransfer, e))
            {
                if (e.CompleteAsync != null) await e.CompleteAsync(false);
                return;
            }

            Uri receivedLink = null;
            var notification = TransferNotification.Begin(AppText.Get("Receiving"), AppText.Format("ReceivingFileBody", fileName, e.Peer.DisplayName), fileName, AppText.Format("ReceivingFromDevice", e.Peer.DisplayName));
            _incomingTelemetryTransport = e.Peer == null ? ShareTransport.GoogleQuickShare : e.Peer.Transport;
            _incomingTelemetryOffer = e.Offer;
            _incomingTelemetryPending = true;
            TelemetryService.Instance.TrackShareStarted(_incomingTelemetryTransport, "receive", e.Offer);
            e.TransferCompleted += (succeeded, message) =>
            {
                notification.Complete(succeeded, message);
                if (_incomingTelemetryPending)
                {
                    _incomingTelemetryPending = false;
                    if (succeeded) TelemetryService.Instance.TrackShareSucceeded(_incomingTelemetryTransport, "receive", e.Offer);
                    else if (e.IsCancellationRequested || message == AppText.Get("ShareCanceledMessage"))
                        TelemetryService.Instance.TrackShareCanceled(_incomingTelemetryTransport, "receive", e.Offer);
                    else TelemetryService.Instance.TrackShareFailed(_incomingTelemetryTransport, "receive", e.Offer, "transfer_failed");
                }
                _ = RunOnViewAsync(() =>
                {
                    if (!ReferenceEquals(_incomingTransfer, e)) return;
                    _incomingTransfer = null;
                    ShareButton.Content = oldShareContent;
                    ShareButton.IsEnabled = true;
                    ShareButton.Visibility = oldShareVisibility;
                    SendButton.Visibility = oldSendVisibility;
                    SendButton.IsEnabled = oldSendEnabled;
                    CancelButton.Visibility = oldCancelVisibility;
                    NearbyCheckBox.IsEnabled = oldNearbyEnabled;
                    QuickShareCheckBox.IsEnabled = oldQuickShareEnabled;
                    if (_pendingOffer != null) SelectedFileText.Text = GetOfferDisplayName(_pendingOffer);
                    if (!succeeded) ResetTransferProgress();
                    StatusText.Text = succeeded ? AppText.Get(receivedLink == null ? "FilesAreSavedInPicturesLiveDrop" : "WebLinkReceived") : message;
                    // The receiver only reports URL metadata here, after the full
                    // transfer succeeds. A regular .txt file never enters this path.
                    if (succeeded && receivedLink != null) _ = OpenReceivedLinkAsync(receivedLink);
                });
            };
            e.ProgressChanged += progress =>
            {
                if (progress.ReceivedWebLink != null && receivedLink == null) receivedLink = progress.ReceivedWebLink;
                notification.Update(progress);
                _ = RunOnViewAsync(() =>
                {
                    if (ReferenceEquals(_incomingTransfer, e) && !e.IsCancellationRequested) ShowTransferProgress(progress);
                });
            };
            try
            {
                if (e.CompleteAsync != null) await e.CompleteAsync(!_viewClosed && !e.IsCancellationRequested);
            }
            catch
            {
                e.CancelTransfer();
                try { if (e.CompleteAsync != null) await e.CompleteAsync(false); } catch { }
            }
        }

        private async Task OpenReceivedLinkAsync(Uri link)
        {
            try
            {
                if (!link.IsAbsoluteUri || (link.Scheme != "https" && link.Scheme != "http")) return;
                if (!await Launcher.LaunchUriAsync(link)) StatusText.Text = AppText.Get("CouldNotOpenReceivedLink");
            }
            catch { StatusText.Text = AppText.Get("CouldNotOpenReceivedLink"); }
        }

    }

    [Windows.UI.Xaml.Data.Bindable]
    internal sealed class PeerListItem : System.ComponentModel.INotifyPropertyChanged
    {
        private readonly bool _showProtocol;
        internal PeerDescriptor Peer { get; private set; }
        internal PeerListItem(PeerDescriptor peer, bool showProtocol) { Peer = peer; _showProtocol = showProtocol; }
        internal string Key { get { return GetKey(Peer); } }
        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;
        internal void Update(PeerDescriptor peer)
        {
            if (peer == null) return;
            Peer = peer;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs("DisplayText"));
        }
        internal static string GetKey(PeerDescriptor peer)
        {
            if (peer == null) return string.Empty;
            var identity = !string.IsNullOrWhiteSpace(peer.Address) ? peer.Address.ToLowerInvariant() : peer.StableId;
            return identity;
        }
        internal bool Matches(PeerDescriptor peer)
        {
            if (peer == null || Peer == null) return false;
            if (Peer.Transport == peer.Transport && !string.IsNullOrWhiteSpace(Peer.StableId) && !string.IsNullOrWhiteSpace(peer.StableId) &&
                string.Equals(Peer.StableId, peer.StableId, StringComparison.Ordinal)) return true;
            return !string.IsNullOrWhiteSpace(Peer.Address) && string.Equals(Peer.Address, peer.Address, StringComparison.OrdinalIgnoreCase);
        }
        public string DisplayText
        {
            get { return Peer.DisplayName + (_showProtocol ? " - " + (Peer.Transport == ShareTransport.GoogleQuickShare ? AppText.Get("QuickShareName") : AppText.Get("MicrosoftNearbyShare")) : ""); }
        }
        public override string ToString() { return DisplayText; }
    }
}
