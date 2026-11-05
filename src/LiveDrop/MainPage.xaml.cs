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
        private ShareOfferReceivedEventArgs _pendingIncomingOffer;
        private ShareOfferReceivedEventArgs _rejectingIncomingOffer;
        private ShareOfferReceivedEventArgs _incomingTransfer;
        private CancellationTokenSource _sendCancellation;
        private CancellationTokenSource _updateCancellation;
        private TaskCompletionSource<bool> _sendCompletion;
        private TaskCompletionSource<bool> _incomingCompletion;
        private readonly System.Collections.Generic.Queue<ShareOfferReceivedEventArgs> _incomingOfferQueue = new System.Collections.Generic.Queue<ShareOfferReceivedEventArgs>();
        private TransferNotification.Session _lastReceivedNotification;
        private string _updateFileName;
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
        private bool _receiveCompleteVisible;
        private ShareTransport _incomingTelemetryTransport;
        private ShareOffer _incomingTelemetryOffer;
        private readonly System.Collections.Generic.Dictionary<string, string> _discoveryStatus = new System.Collections.Generic.Dictionary<string, string>();
        private readonly System.Collections.Generic.Dictionary<string, DateTime> _peerLastSeen = new System.Collections.Generic.Dictionary<string, DateTime>();
        private readonly DispatcherTimer _peerExpiryTimer;
        private const int HostNameMinimumLength = 1;
        private const int HostNameMaximumLength = 32;
        private const string HostNameAllowedSpecialCharacters = " '._-()&+";
        private const int PeerTimeoutSeconds = 10;
        private const int MaximumQueuedIncomingOffers = 4;
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
            var folderItem = new MenuFlyoutItem { Text = AppText.Get("LiveDropFolder") };
            folderItem.Click += OnOpenLiveDropFolderClicked;
            var hostnameItem = new MenuFlyoutItem { Text = AppText.Get("ChangeHostname") };
            hostnameItem.Click += OnChangeHostnameClicked;
            var githubItem = new MenuFlyoutItem { Text = AppText.Get("About") };
            githubItem.Click += OnAboutClicked;
#if !MICROSOFT_STORE
            var updateItem = new MenuFlyoutItem { Text = AppText.Get("CheckForUpdate") };
            updateItem.Click += OnCheckForUpdateClicked;
#endif
            _moreMenu.Items.Add(folderItem);
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

        private async void OnOpenLiveDropFolderClicked(object sender, RoutedEventArgs e)
        {
            DismissReceiveCompleteNotice();
            try
            {
                var folder = await TransferFileStore.GetReceiveFolderAsync(null);
                if (!await Launcher.LaunchFolderAsync(folder))
                    StatusText.Text = AppText.Get("CouldNotOpenSavedContent");
            }
            catch
            {
                StatusText.Text = AppText.Get("CouldNotOpenSavedContent");
            }
        }

        internal void BeginRegularSession()
        {
            if (_incomingTransfer != null) return;
            DismissReceiveCompleteNotice();
            _shareTargetSession = false;
            _pendingShareOperation = null;
            _pendingOffer = null;
            _shareOperationStarted = false;
            MoreButton.Visibility = Visibility.Visible;
            SelectedFileText.Text = string.Empty;
            SelectedFileText.Visibility = Visibility.Collapsed;
            ResetTransferProgress();
            RefreshTransferActions();
        }

        internal void BeginShareTargetSession()
        {
            DismissReceiveCompleteNotice();
            _shareTargetSession = true;
            _pendingShareOperation = null;
            _pendingOffer = null;
            _shareOperationStarted = false;
            MoreButton.Visibility = Visibility.Collapsed;
            SendButton.Content = AppText.Get("SendSelectedShare");
            ResetTransferProgress();
            RefreshTransferActions();
        }

        private bool IsTransferInProgress
        {
            get { return _sendCancellation != null || _incomingTransfer != null || _updateCancellation != null; }
        }

        private void RefreshTransferActions()
        {
            PromoteIncomingOfferIfIdle();
            var transferInProgress = IsTransferInProgress;
            var incomingDecisionVisible = _pendingIncomingOffer != null && !transferInProgress &&
                !_updateCheckInProgress && !_filePickerPending;
            var incomingOfferQueued = _pendingIncomingOffer != null || _incomingOfferQueue.Count > 0 ||
                _rejectingIncomingOffer != null;

            ShareButton.Visibility = !_shareTargetSession && !transferInProgress && !incomingOfferQueued && !_receiveCompleteVisible
                ? Visibility.Visible : Visibility.Collapsed;
            ShareButton.IsEnabled = !_filePickerPending && !_updateCheckInProgress && !transferInProgress &&
                !incomingOfferQueued && !_receiveCompleteVisible;

            SendButton.Visibility = (_shareTargetSession || _pendingOffer != null) && !transferInProgress && !incomingOfferQueued && !_receiveCompleteVisible
                ? Visibility.Visible : Visibility.Collapsed;
            SendButton.IsEnabled = !transferInProgress && !_updateCheckInProgress && !incomingOfferQueued && _pendingOffer != null &&
                _pendingOffer.HasContent && (!_shareTargetSession || PeersList.SelectedItem != null);

            CancelButton.Visibility = transferInProgress ? Visibility.Visible : Visibility.Collapsed;
            var cancellationRequested =
                (_sendCancellation != null && _sendCancellation.IsCancellationRequested) ||
                (_incomingTransfer != null && _incomingTransfer.IsCancellationRequested) ||
                (_updateCancellation != null && _updateCancellation.IsCancellationRequested);
            CancelButton.IsEnabled = transferInProgress && !cancellationRequested;

            IncomingDecisionPanel.Visibility = incomingDecisionVisible ? Visibility.Visible : Visibility.Collapsed;
            if (incomingDecisionVisible)
            {
                IncomingDetailsText.Text = string.IsNullOrWhiteSpace(_pendingIncomingOffer.SecurityPin)
                    ? AppText.Get("IncomingShareRequest")
                    : AppText.Format("QuickShareSecurityPin", _pendingIncomingOffer.SecurityPin);
                IncomingDetailsText.Visibility = Visibility.Visible;
                SelectedFileText.Text = string.Empty;
                SelectedFileText.Visibility = Visibility.Collapsed;
            }
            else if (_incomingTransfer != null)
            {
                IncomingDetailsText.Text = string.Empty;
                IncomingDetailsText.Visibility = Visibility.Collapsed;
            }
            else
            {
                IncomingDetailsText.Text = string.Empty;
                IncomingDetailsText.Visibility = Visibility.Collapsed;
            }

            StatusText.Visibility = incomingDecisionVisible ? Visibility.Collapsed : Visibility.Visible;
            ViewFilesButton.Visibility = _receiveCompleteVisible && !transferInProgress ? Visibility.Visible : Visibility.Collapsed;
            DismissReceivedButton.Visibility = _receiveCompleteVisible && !transferInProgress ? Visibility.Visible : Visibility.Collapsed;
            ReceiveCompleteText.Visibility = _receiveCompleteVisible && !transferInProgress ? Visibility.Visible : Visibility.Collapsed;

            if (_updateCancellation != null)
            {
                SelectedFileText.Text = _updateFileName ?? string.Empty;
                SelectedFileText.Visibility = string.IsNullOrEmpty(_updateFileName) ? Visibility.Collapsed : Visibility.Visible;
            }
            else if (_incomingTransfer != null)
            {
                SelectedFileText.Text = GetOfferDisplayName(_incomingTransfer.Offer);
                SelectedFileText.Visibility = Visibility.Visible;
            }
            else if (incomingDecisionVisible)
            {
                SelectedFileText.Text = string.Empty;
                SelectedFileText.Visibility = Visibility.Collapsed;
            }
            else if (_pendingOffer != null)
            {
                SelectedFileText.Text = GetOfferDisplayName(_pendingOffer);
                SelectedFileText.Visibility = Visibility.Visible;
            }
            else
            {
                SelectedFileText.Text = string.Empty;
                SelectedFileText.Visibility = Visibility.Collapsed;
            }
        }

        private void PromoteIncomingOfferIfIdle()
        {
            if (_pendingIncomingOffer != null || _incomingOfferQueue.Count == 0 || IsTransferInProgress ||
                _updateCheckInProgress || _filePickerPending || _rejectingIncomingOffer != null) return;
            _pendingIncomingOffer = _incomingOfferQueue.Dequeue();
            DismissReceiveCompleteNotice();
        }

        private void DismissReceiveCompleteNotice()
        {
            _receiveCompleteVisible = false;
            _lastReceivedNotification = null;
            if (ReceiveCompleteText != null) ReceiveCompleteText.Text = string.Empty;
            if (ViewFilesButton != null) ViewFilesButton.Visibility = Visibility.Collapsed;
            if (DismissReceivedButton != null) DismissReceivedButton.Visibility = Visibility.Collapsed;
            if (ReceiveCompleteText != null) ReceiveCompleteText.Visibility = Visibility.Collapsed;
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
            DismissReceiveCompleteNotice();
            _updateCheckInProgress = true;
            RefreshTransferActions();
            TelemetryService.Instance.TrackUpdateCheck("started");
            try
            {
                await CancelActiveTransfersAsync();
                if (_viewClosed) return;
                StatusText.Text = AppText.Get("CheckingGitHubForUpdates");
                var service = new GitHubUpdateService();
                var release = await service.GetLatestReleaseAsync();
                if (_viewClosed) return;
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
                _updateFileName = release.BundleName;
                _updateCancellation = new CancellationTokenSource();
                TransferProgressPanel.Visibility = Visibility.Visible;
                TransferProgressBar.IsIndeterminate = true;
                TransferProgressText.Text = string.Empty;
                RefreshTransferActions();
                var progress = new Progress<double>(value =>
                {
                    if (_viewClosed || _updateCancellation == null || _updateCancellation.IsCancellationRequested) return;
                    TransferProgressPanel.Visibility = Visibility.Visible;
                    TransferProgressBar.IsIndeterminate = false;
                    TransferProgressBar.Value = Math.Min(1.0, Math.Max(0.0, value));
                    TransferProgressText.Text = TransferProgressBar.Value.ToString("P0", System.Globalization.CultureInfo.CurrentCulture);
                    StatusText.Text = AppText.Format("DownloadingVersionProgress", release.Version.ToString(4), value);
                });
                var file = await service.DownloadAsync(release, progress, _updateCancellation.Token);
                _updateCancellation.Dispose();
                _updateCancellation = null;
                _updateFileName = null;
                if (!_viewClosed)
                {
                    ResetTransferProgress();
                    RefreshTransferActions();
                    StatusText.Text = AppText.Get("OpeningTheWindowsInstaller");
                }
                if (!await Launcher.LaunchFileAsync(file))
                {
                    if (!_viewClosed) StatusText.Text = AppText.Get("WindowsCouldNotOpenTheInstaller");
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
                if (!_viewClosed) StatusText.Text = AppText.Get("CouldNotReachGitHubCheckYourConnectionAnd");
            }
            catch (OperationCanceledException)
            {
                if (_updateCancellation != null && _updateCancellation.IsCancellationRequested)
                {
                    if (!_viewClosed) StatusText.Text = AppText.Get("UpdateDownloadCanceled");
                }
                else
                {
                    TelemetryService.Instance.TrackUpdateFailed("timeout");
                    if (!_viewClosed) StatusText.Text = AppText.Get("TheUpdateCheckTimedOutPleaseTryAgain");
                }
            }
            catch (Exception ex)
            {
                TelemetryService.Instance.TrackUpdateFailed(TelemetryService.ClassifyFailure(ex));
                if (!_viewClosed) StatusText.Text = AppText.Format("UpdateCheckFailed", ex.Message);
            }
            finally
            {
                if (_updateCancellation != null) { _updateCancellation.Dispose(); _updateCancellation = null; }
                _updateFileName = null;
                _updateCheckInProgress = false;
                if (!_viewClosed)
                {
                    ResetTransferProgress();
                    RefreshTransferActions();
                }
            }
        }

        private async Task CancelActiveTransfersAsync()
        {
            var completions = new System.Collections.Generic.List<Task>();
            if (_sendCancellation != null)
            {
                if (_sendCompletion != null) completions.Add(_sendCompletion.Task);
                try { _sendCancellation.Cancel(); } catch (ObjectDisposedException) { }
            }
            if (_incomingTransfer != null)
            {
                if (_incomingCompletion != null) completions.Add(_incomingCompletion.Task);
                _incomingTransfer.CancelTransfer();
            }
            if (!_viewClosed) RefreshTransferActions();
            if (completions.Count > 0) await Task.WhenAll(completions);
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
            if (_updateCancellation != null) _updateCancellation.Cancel();
            _incomingTransfer?.CancelTransfer();
            _incomingCompletion?.TrySetResult(true);
            await ApplyProtocolsAsync();
        }

        private async void OnShareClicked(object sender, RoutedEventArgs e)
        {
            if (_shareTargetSession || _filePickerPending || _updateCheckInProgress || IsTransferInProgress) return;
            DismissReceiveCompleteNotice();
            _filePickerPending = true;
            RefreshTransferActions();
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
                RefreshTransferActions();
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
                _pendingOffer = null;
                SelectedFileText.Text = string.Empty;
                SelectedFileText.Visibility = Visibility.Collapsed;
                RefreshTransferActions();
                StatusText.Text = AppText.Get("NoFileSelected");
                return;
            }

            _pendingOffer = await ShareOfferFactory.FromPickedFilesAsync(files);
            if (_pendingOffer.Files.Count == 0)
            {
                _pendingOffer = null;
                SelectedFileText.Text = string.Empty;
                SelectedFileText.Visibility = Visibility.Collapsed;
                RefreshTransferActions();
                StatusText.Text = AppText.Get("NoFileSelected");
                return;
            }
            SelectedFileText.Text = GetOfferDisplayName(_pendingOffer);
            ResetTransferProgress();
            SendButton.Content = AppText.Get("SendSelectedShare");
            RefreshTransferActions();
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
            if (_updateCancellation != null)
            {
                CancelButton.IsEnabled = false;
                StatusText.Text = AppText.Get("Canceling");
                try { _updateCancellation.Cancel(); } catch (ObjectDisposedException) { }
                RefreshTransferActions();
                return;
            }
            if (_incomingTransfer != null)
            {
                CancelButton.IsEnabled = false;
                StatusText.Text = AppText.Get("Canceling");
                _incomingTransfer.CancelTransfer();
                RefreshTransferActions();
                return;
            }
            if (_sendCancellation != null)
            {
                CancelButton.IsEnabled = false;
                StatusText.Text = AppText.Get("Canceling");
                try { _sendCancellation.Cancel(); } catch (ObjectDisposedException) { }
                RefreshTransferActions();
                return;
            }
        }

        private void ResetSelectedShare(string status)
        {
            _pendingOffer = null;
            _pendingShareOperation = null;
            ResetTransferProgress();
            RefreshTransferActions();
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
                RefreshTransferActions();
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
                RefreshTransferActions();
            }
            catch (Exception ex)
            {
                _pendingShareOperation = null;
                _pendingOffer = null;
                await RunOnViewAsync(() =>
                {
                    RefreshTransferActions();
                    StatusText.Text = AppText.Format("ReadShareFailed", ex.Message);
                });
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
                if (!_updateCheckInProgress && _pendingIncomingOffer == null && !_receiveCompleteVisible &&
                    !string.Equals(e.Message, AppText.Get("FilesSavedToLiveDropFolder"), StringComparison.Ordinal))
                    StatusText.Text = GetDiscoveryStatusText();
            });
        }

        private string GetDiscoveryStatusText()
        {
            var savedMessage = AppText.Get("FilesSavedToLiveDropFolder");
            return string.Join("\n", _discoveryStatus.Values.Where(message =>
                !string.Equals(message, savedMessage, StringComparison.Ordinal)));
        }

        private void OnPeerSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            RefreshTransferActions();
        }

        private async void OnSendClicked(object sender, RoutedEventArgs e)
        {
            var selected = PeersList.SelectedItem as PeerListItem;
            if (IsTransferInProgress || _updateCheckInProgress || _pendingOffer == null || _coordinator == null) return;
            if (selected == null)
            {
                StatusText.Text = AppText.Get("SelectAPeerFirst");
                return;
            }
            var outgoingOffer = _pendingOffer;
            var outgoingName = GetOfferDisplayName(outgoingOffer);
            DismissReceiveCompleteNotice();
            var sendCompletion = new TaskCompletionSource<bool>();
            _sendCompletion = sendCompletion;
            _sendCancellation = new CancellationTokenSource();
            NearbyCheckBox.IsEnabled = QuickShareCheckBox.IsEnabled = false;
            RefreshTransferActions();
            TransferNotification.Session notification = null;
            try
            {
                notification = TransferNotification.Begin(AppText.Get("Sending"), AppText.Format("SendingFileBody", outgoingName, selected.Peer.DisplayName), outgoingName, AppText.Format("SendingToDevice", selected.Peer.DisplayName));
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
                _pendingOffer = null;
                ResetTransferProgress();
                RefreshTransferActions();
                StatusText.Text = AppText.Get("ShareCompleted");
            }
            catch (OperationCanceledException)
            {
                notification?.Complete(false, AppText.Get("ShareCanceledMessage"));
                TelemetryService.Instance.TrackShareCanceled(selected.Peer.Transport, "send", outgoingOffer);
                TryReportShareError(AppText.Get("ShareCanceledMessage"));
                if (!_viewClosed)
                {
                    ResetSelectedShare(AppText.Get("ShareCanceledMessage"));
                }
            }
            catch (Exception ex)
            {
                notification?.Complete(false, ex.Message);
                TelemetryService.Instance.TrackShareFailed(selected.Peer.Transport, "send", outgoingOffer, TelemetryService.ClassifyFailure(ex));
                TryReportShareError(ex.Message);
                if (!_viewClosed)
                {
                    ResetSelectedShare(AppText.Format("ShareFailedDetail", ex.Message));
                }
            }
            finally
            {
                if (_sendCancellation != null) { _sendCancellation.Dispose(); _sendCancellation = null; }
                if (!_viewClosed)
                {
                    NearbyCheckBox.IsEnabled = QuickShareCheckBox.IsEnabled = true;
                    RefreshTransferActions();
                }
                if (ReferenceEquals(_sendCompletion, sendCompletion)) _sendCompletion = null;
                sendCompletion.TrySetResult(true);
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

        private async void OnAcceptIncomingClicked(object sender, RoutedEventArgs e)
        {
            var offer = _pendingIncomingOffer;
            if (offer == null || IsTransferInProgress || _updateCheckInProgress) return;
            _pendingIncomingOffer = null;
            DismissReceiveCompleteNotice();
            await StartIncomingReceiveAsync(offer);
        }

        private async void OnDenyIncomingClicked(object sender, RoutedEventArgs e)
        {
            var offer = _pendingIncomingOffer;
            if (offer == null || IsTransferInProgress || _updateCheckInProgress) return;
            _pendingIncomingOffer = null;
            _rejectingIncomingOffer = offer;
            DismissReceiveCompleteNotice();
            StatusText.Text = AppText.Get("ShareCanceledMessage");
            RefreshTransferActions();
            try
            {
                if (offer.CompleteAsync != null) await offer.CompleteAsync(false);
            }
            catch
            {
                if (ReferenceEquals(_rejectingIncomingOffer, offer)) _rejectingIncomingOffer = null;
                RefreshTransferActions();
            }
        }

        private async Task StartIncomingReceiveAsync(ShareOfferReceivedEventArgs e)
        {
            var oldNearbyEnabled = NearbyCheckBox.IsEnabled;
            var oldQuickShareEnabled = QuickShareCheckBox.IsEnabled;
            var incomingCompletion = new TaskCompletionSource<bool>();
            var fileName = GetOfferDisplayName(e.Offer);
            var peerName = e.Peer == null ? AppText.Get("UnknownPeer") : e.Peer.DisplayName;
            _incomingTransfer = e;
            _incomingCompletion = incomingCompletion;
            NearbyCheckBox.IsEnabled = QuickShareCheckBox.IsEnabled = false;
            ResetTransferProgress();
            TransferProgressPanel.Visibility = Visibility.Visible;
            TransferProgressBar.IsIndeterminate = true;
            TransferProgressText.Text = AppText.Get("Accepting");
            StatusText.Text = AppText.Format("ReceivingContent", fileName);
            RefreshTransferActions();

            var notification = TransferNotification.Begin(AppText.Get("Receiving"), AppText.Format("ReceivingFileBody", fileName, peerName), fileName, AppText.Format("ReceivingFromDevice", peerName));
            _incomingTelemetryTransport = e.Peer == null ? ShareTransport.GoogleQuickShare : e.Peer.Transport;
            _incomingTelemetryOffer = e.Offer;
            _incomingTelemetryPending = true;
            TelemetryService.Instance.TrackShareStarted(_incomingTelemetryTransport, "receive", e.Offer);
            Uri receivedLink = null;
            var completionHandled = 0;
            Action<bool, string> completed = null;
            completed = (succeeded, message) =>
            {
                if (System.Threading.Interlocked.Exchange(ref completionHandled, 1) != 0) return;
                e.TransferCompleted -= completed;
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
                    if (!ReferenceEquals(_incomingTransfer, e))
                    {
                        incomingCompletion.TrySetResult(true);
                        return;
                    }
                    _incomingTransfer = null;
                    if (ReferenceEquals(_incomingCompletion, incomingCompletion)) _incomingCompletion = null;
                    NearbyCheckBox.IsEnabled = oldNearbyEnabled;
                    QuickShareCheckBox.IsEnabled = oldQuickShareEnabled;
                    ResetTransferProgress();
                    if (succeeded && e.Offer.Files.Count > 0 && receivedLink == null)
                    {
                        _lastReceivedNotification = notification;
                        _receiveCompleteVisible = true;
                        ReceiveCompleteText.Text = AppText.Get("FilesSavedToLiveDropFolder");
                    }
                    StatusText.Text = succeeded
                        ? (receivedLink == null ? GetDiscoveryStatusText() : AppText.Get("WebLinkReceived"))
                        : message;
                    RefreshTransferActions();
                    // Open only protocol-declared web links after a successful transfer.
                    if (succeeded && receivedLink != null) _ = OpenReceivedLinkAsync(receivedLink);
                    incomingCompletion.TrySetResult(true);
                });
            };
            e.TransferCompleted += completed;
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
                if (e.CompleteAsync != null) await e.CompleteAsync(true);
            }
            catch (Exception ex)
            {
                e.CancelTransfer();
                try { if (e.CompleteAsync != null) await e.CompleteAsync(false); } catch { }
                completed(false, ex.Message);
            }
        }

        private async void OnViewFilesClicked(object sender, RoutedEventArgs e)
        {
            var notification = _lastReceivedNotification;
            if (notification != null) await notification.OpenSavedContentAsync();
        }

        private void OnDismissReceivedClicked(object sender, RoutedEventArgs e)
        {
            DismissReceiveCompleteNotice();
            StatusText.Text = GetDiscoveryStatusText();
            RefreshTransferActions();
        }

        private async void OnOfferReceived(object sender, ShareOfferReceivedEventArgs e)
        {
            var reject = true;
            var queueFull = false;
            await RunOnViewAsync(() =>
            {
                if (!_loaded || _viewClosed || !ReferenceEquals(sender, _coordinator)) return;
                if (_incomingOfferQueue.Count + (_pendingIncomingOffer == null ? 0 : 1) + (_rejectingIncomingOffer == null ? 0 : 1) >= MaximumQueuedIncomingOffers)
                {
                    queueFull = true;
                    return;
                }
                reject = false;
                DismissReceiveCompleteNotice();
                Action<bool, string> completion = null;
                completion = (succeeded, message) =>
                {
                    e.TransferCompleted -= completion;
                    _ = RunOnViewAsync(() => OnQueuedIncomingOfferCompleted(e, succeeded, message));
                };
                e.TransferCompleted += completion;
                _incomingOfferQueue.Enqueue(e);
                RefreshTransferActions();
            });
            if (reject)
            {
                try { if (e.CompleteAsync != null) await e.CompleteAsync(false); } catch { }
                if (queueFull)
                    await RunOnViewAsync(() => StatusText.Text = AppText.Get("IncomingShareQueueFull"));
            }
        }

        private void OnQueuedIncomingOfferCompleted(ShareOfferReceivedEventArgs offer, bool succeeded, string message)
        {
            if (ReferenceEquals(_incomingTransfer, offer)) return;
            var wasRejected = ReferenceEquals(_rejectingIncomingOffer, offer);
            if (ReferenceEquals(_pendingIncomingOffer, offer)) _pendingIncomingOffer = null;
            if (wasRejected) _rejectingIncomingOffer = null;
            if (_incomingOfferQueue.Count > 0)
            {
                var retained = new System.Collections.Generic.Queue<ShareOfferReceivedEventArgs>();
                while (_incomingOfferQueue.Count > 0)
                {
                    var queued = _incomingOfferQueue.Dequeue();
                    if (!ReferenceEquals(queued, offer)) retained.Enqueue(queued);
                }
                while (retained.Count > 0) _incomingOfferQueue.Enqueue(retained.Dequeue());
            }
            if (!succeeded && !wasRejected && !_viewClosed) StatusText.Text = message;
            RefreshTransferActions();
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
