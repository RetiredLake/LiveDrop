using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Linq;
using Windows.UI.Core;
using LiveDrop.Models;
using LiveDrop.Services;
using LiveDrop.Protocols;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Popups;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using LiveDrop;

namespace LiveDrop8
{
    public sealed partial class MainPage : Page
    {
        private Windows.ApplicationModel.DataTransfer.ShareTarget.ShareOperation _shareOperation;
        private CancellationTokenSource _transfer;
        private ShareCoordinator _coordinator;
        private Task _outboundTask;
        private ShareOffer _selected;
        private ShareOfferReceivedEventArgs _pending, _receiving;
        private readonly Queue<ShareOfferReceivedEventArgs> _offers = new Queue<ShareOfferReceivedEventArgs>();
        private readonly HashSet<ShareOfferReceivedEventArgs> _trackedIncoming = new HashSet<ShareOfferReceivedEventArgs>();
        private readonly HashSet<ShareOfferReceivedEventArgs> _finished = new HashSet<ShareOfferReceivedEventArgs>();
        private readonly Dictionary<ShareOfferReceivedEventArgs, List<string>> _saved = new Dictionary<ShareOfferReceivedEventArgs, List<string>>();
        private readonly Dictionary<ShareOfferReceivedEventArgs, Uri> _links = new Dictionary<ShareOfferReceivedEventArgs, Uri>();
        private readonly Dictionary<string, DateTime> _seen = new Dictionary<string, DateTime>();
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        private readonly DispatcherTimer _expiry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        private List<string> _lastSaved = new List<string>();
        private bool _active, _preparing;
        public MainPage()
        {
            InitializeComponent();
#if WINDOWS_PHONE_APP
            foreach (var command in ((CommandBar)BottomAppBar).SecondaryCommands.OfType<AppBarButton>())
            {
                command.FontSize = 24;
                command.Padding = new Thickness(12, 6, 12, 6);
                command.Margin = new Thickness(0);
                command.MinHeight = 48;
            }
#endif
            TelemetryService.Instance.TrackApplicationStarted();
            FolderCommand.Label = AppText.Get("LiveDropFolder");
            HostnameCommand.Label = AppText.Get("ChangeHostname");
            AboutCommand.Label = AppText.Get("About");
            Loaded += async (s, e) => { _active = true; await RestartAsync(); };
            Unloaded += async (s, e) => { _active = false; await ShutdownAsync(); };
            _expiry.Tick += (s, e) => ExpirePeers();
            RefreshActions();
        }
        private Task OnView(Action action) { return Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action()).AsTask(); }
        private bool Busy { get { return _transfer != null || _receiving != null; } }
        private void RefreshActions()
        {
            SendCommand.Visibility = _pending != null ? Visibility.Collapsed : Visibility.Visible;
            SendCommand.IsEnabled = !_preparing && _coordinator != null;
            SendCommand.Icon = new SymbolIcon(Busy ? Symbol.Cancel : ReceivedPanel.Visibility == Visibility.Visible ? Symbol.Accept : Symbol.Upload);
            SendCommand.Label = Busy ? "cancel" : ReceivedPanel.Visibility == Visibility.Visible ? "done" : _selected != null ? "send" : "select files";
            PeersCommand.IsEnabled = HostnameCommand.IsEnabled = !Busy && _pending == null && !_preparing;
        }
        private async Task RestartAsync()
        {
            await _lifecycle.WaitAsync();
            try
            {
                await StopCoreAsync();
                if (!_active) return;
                PeersList.Items.Clear(); _seen.Clear();
                var values = ApplicationData.Current.LocalSettings.Values;
                var name = values.ContainsKey("DisplayName") ? (string)values["DisplayName"] : ShareCoordinator.GetHostDisplayName("LiveDrop");
                var nearby = values.ContainsKey("NearbyEnabled") && (bool)values["NearbyEnabled"];
                var quick = !values.ContainsKey("QuickShareEnabled") || (bool)values["QuickShareEnabled"];
                var host = new ShareCoordinator(name, nearby, quick);
                _coordinator = host;
                host.PeerDiscovered += OnPeer;
                host.OfferReceived += OnOffer;
                host.StatusChanged += OnStatus;
                StatusText.Text = "Starting discovery...";
                await host.StartAsync();
                if (!nearby && !quick) StatusText.Text = "Discovery is disabled. Enable a supported peer protocol in the menu.";
                _expiry.Start();
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { _lifecycle.Release(); RefreshActions(); }
        }
        internal async Task SuspendAsync() { _active = false; await ShutdownAsync(); }
        internal async Task ResumeAsync() { _active = true; await RestartAsync(); }
        internal async Task ShutdownAsync()
        {
            await _lifecycle.WaitAsync();
            try { await StopCoreAsync(); }
            finally { _lifecycle.Release(); }
        }
        private async Task StopCoreAsync()
        {
            _expiry.Stop();
            if (_transfer != null) _transfer.Cancel();
            if (_outboundTask != null) await _outboundTask;
            if (_receiving != null) _receiving.CancelTransfer();
            _receiving = null; TransferProgress.Visibility = Visibility.Collapsed;
            if (_pending != null) _pending.CancelTransfer();
            foreach (var offer in _offers) offer.CancelTransfer();
            _offers.Clear(); _pending = null; ConsentPanel.Visibility = Visibility.Collapsed;
            var host = _coordinator; _coordinator = null;
            if (host != null)
            {
                host.PeerDiscovered -= OnPeer; host.OfferReceived -= OnOffer; host.StatusChanged -= OnStatus;
                await host.StopAsync(); host.Dispose();
            }
        }
        private readonly SemaphoreSlim _logLock = new SemaphoreSlim(1, 1);
        private async Task RecordDiscoveryAsync(string message)
        {
            await _logLock.WaitAsync();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync("discovery.log", CreationCollisionOption.OpenIfExists);
                if ((await file.GetBasicPropertiesAsync()).Size > 128 * 1024) file = await ApplicationData.Current.LocalFolder.CreateFileAsync("discovery.log", CreationCollisionOption.ReplaceExisting);
                await FileIO.AppendTextAsync(file, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine);
            }
            catch { }
            finally { _logLock.Release(); }
        }
        private async void OnStatus(object sender, StatusChangedEventArgs e)
        {
            await OnView(() => { if (_coordinator != null && _pending == null) StatusText.Text = e.Message; var log = RecordDiscoveryAsync(e.Message); });
        }
        private async void OnPeer(object sender, PeerDiscoveredEventArgs e)
        {
            await OnView(() =>
            {
                if (!ReferenceEquals(sender, _coordinator) || e.Peer == null || ProtocolUtilities.IsLoopbackAddress(e.Peer.Address)) return;
                var matches = PeersList.Items.OfType<PeerListItem>().Where(x => x.Matches(e.Peer)).ToList();
                var item = matches.FirstOrDefault();
                if (item != null && item.Peer.Transport == ShareTransport.GoogleQuickShare && e.Peer.Transport != ShareTransport.GoogleQuickShare) return;
                foreach (var duplicate in matches.Skip(1)) { PeersList.Items.Remove(duplicate); _seen.Remove(duplicate.Key); }
                if (item == null) { PeersList.Items.Add(item = new PeerListItem(e.Peer, false)); var log = RecordDiscoveryAsync("Discovered " + e.Peer.DisplayName + " at " + e.Peer.Address); }
                else { _seen.Remove(item.Key); item.Update(e.Peer); }
                _seen[item.Key] = DateTime.UtcNow;
            });
        }
        private void ExpirePeers()
        {
            foreach (var peer in PeersList.Items.OfType<PeerListItem>().ToList())
            {
                DateTime last;
                if (!_seen.TryGetValue(peer.Key, out last) || DateTime.UtcNow - last > TimeSpan.FromSeconds(10))
                { PeersList.Items.Remove(peer); _seen.Remove(peer.Key); }
            }
        }
        private void OnPeerSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var item in e.RemovedItems.OfType<PeerListItem>()) item.SetSelected(false);
            foreach (var item in e.AddedItems.OfType<PeerListItem>()) item.SetSelected(true);
        }
        private async void OnMainCommand(object sender, RoutedEventArgs e)
        {
            if (ReceivedPanel.Visibility == Visibility.Visible && !Busy) { OnDismiss(sender, e); return; }
            if (_transfer != null) { _transfer.Cancel(); return; }
            if (_receiving != null) { _receiving.CancelTransfer(); return; }
            if (_pending != null || _preparing) return;
            if (_selected != null) { _outboundTask = SendAsync(); await _outboundTask; _outboundTask = null; return; }
            _preparing = true; RefreshActions();
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            picker.FileTypeFilter.Add("*");
            try
            {
#if WINDOWS_PHONE_APP
                picker.ContinuationData["Action"] = "Select";
                picker.PickMultipleFilesAndContinue();
#else
                await ApplySelectionAsync(await picker.PickMultipleFilesAsync());
#endif
            }
            catch (Exception ex) { StatusText.Text = AppText.Format("PickerFailed", ex.Message); }
            finally { _preparing = false; RefreshActions(); ShowNextOffer(); }
        }
        internal async Task ApplySelectionAsync(IReadOnlyList<StorageFile> files)
        {
            _preparing = true; RefreshActions();
            try
            {
                ClearReceived(); await ClearSelectedAsync();
                _selected = await ShareOfferFactory.FromPickedFilesAsync(files);
                if (!_selected.HasContent) _selected = null;
                FileNameText.Text = _selected == null ? "" : OfferName(_selected);
                StatusText.Text = _selected == null ? "" : AppText.Get("SelectAPeerThenSendTheFile");
            }
            catch (Exception ex) { StatusText.Text = ex.Message; await ClearSelectedAsync(); }
            finally { _preparing = false; RefreshActions(); ShowNextOffer(); }
        }
        internal async Task LoadShareAsync(Windows.ApplicationModel.DataTransfer.ShareTarget.ShareOperation operation)
        {
            _shareOperation = operation; _preparing = true; RefreshActions();
            FolderCommand.Visibility = PeersCommand.Visibility = HostnameCommand.Visibility = AboutCommand.Visibility = Visibility.Collapsed;
            try
            {
                await ClearSelectedAsync();
                _selected = await ShareOfferFactory.FromShareOperationAsync(operation);
                operation.ReportDataRetrieved();
                FileNameText.Text = OfferName(_selected);
                if (!_selected.HasContent) { _selected = null; FileNameText.Text = ""; StatusText.Text = "No supported content was shared."; }
                else StatusText.Text = AppText.Get("SelectAPeerThenSendTheFile");
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
            finally { _preparing = false; RefreshActions(); }
        }
        private static string OfferName(ShareOffer offer)
        { return offer.WebLink != null ? offer.WebLink.AbsoluteUri : offer.Files.Count == 1 ? offer.Files[0].Name : AppText.Format("FileCount", offer.Files.Count); }
        private async Task ClearSelectedAsync()
        {
            var offer = _selected; _selected = null; FileNameText.Text = "";
            if (offer != null) foreach (var file in offer.Files)
                if (file.SourceFile != null) try { await file.SourceFile.DeleteAsync(); } catch { }
        }
        private async Task SendAsync()
        {
            var peer = PeersList.SelectedItem as PeerListItem;
            if (peer == null) { StatusText.Text = AppText.Get("SelectAPeerFirst"); return; }
            if (_coordinator == null || Busy) return;
            ClearReceived();
            var source = new CancellationTokenSource(); _transfer = source;
            TransferProgress.IsIndeterminate = true; TransferProgress.Visibility = Visibility.Visible; RefreshActions();
            try
            {
                TelemetryService.Instance.TrackShareStarted(peer.Peer.Transport, "send", _selected);
                await _coordinator.SendAsync(peer.Peer, _selected, new Progress<ShareProgress>(p => { if (ReferenceEquals(source, _transfer)) ShowProgress(p); }), source.Token);
                TelemetryService.Instance.TrackShareSucceeded(peer.Peer.Transport, "send", _selected);
                StatusText.Text = "Share complete";
                if (_shareOperation != null) _shareOperation.ReportCompleted();
            }
            catch (OperationCanceledException)
            { TelemetryService.Instance.TrackShareCanceled(peer.Peer.Transport, "send", _selected); StatusText.Text = AppText.Get("ShareCanceledMessage"); }
            catch (Exception ex)
            { TelemetryService.Instance.TrackShareFailed(peer.Peer.Transport, "send", _selected, TelemetryService.ClassifyFailure(ex)); StatusText.Text = ex.Message; }
            finally
            {
                _transfer = null; source.Dispose(); TransferProgress.Visibility = Visibility.Collapsed;
                await ClearSelectedAsync(); RefreshActions(); ShowNextOffer();
            }
        }
        private void ShowProgress(ShareProgress progress)
        {
            FileNameText.Text = progress.FileName;
            TransferProgress.IsIndeterminate = progress.TotalBytes <= 0;
            TransferProgress.Value = progress.TotalBytes <= 0 ? 0 : Math.Min(100, 100.0 * progress.BytesTransferred / progress.TotalBytes);
        }
        private async void OnOffer(object sender, ShareOfferReceivedEventArgs offer)
        {
            await OnView(() =>
            {
                if (!ReferenceEquals(sender, _coordinator) || _offers.Count >= 4)
                { offer.CancelTransfer(); return; }
                _saved[offer] = new List<string>();
                offer.ProgressChanged += p => { var ignored = OnView(() =>
                {
                    List<string> paths;
                    if (!_saved.TryGetValue(offer, out paths)) return;
                    if (!string.IsNullOrEmpty(p.SavedFilePath) && !paths.Contains(p.SavedFilePath)) paths.Add(p.SavedFilePath);
                    if (p.ReceivedWebLink != null) _links[offer] = p.ReceivedWebLink;
                    if (ReferenceEquals(_receiving, offer)) ShowProgress(p);
                }); };
                offer.TransferCompleted += (ok, message) => { var ignored = OnView(() => FinishIncoming(offer, ok, message)); };
                _offers.Enqueue(offer); ShowNextOffer();
            });
        }
        private void ShowNextOffer()
        {
            if (Busy || _pending != null || _preparing || !_active) return;
            while (_offers.Count > 0)
            {
                var offer = _offers.Dequeue();
                if (_finished.Remove(offer) || offer.IsCancellationRequested) continue;
                ClearReceived(); _pending = offer;
                FileNameText.Text = "";
                StatusText.Text = string.IsNullOrEmpty(offer.SecurityPin) ? "Incoming share. Accept or deny?" : AppText.Format("QuickShareSecurityPin", offer.SecurityPin);
                ConsentPanel.Visibility = Visibility.Visible; RefreshActions(); return;
            }
        }
        private async void OnAccept(object sender, RoutedEventArgs e)
        {
            var offer = _pending; if (offer == null) return;
            _pending = null; _receiving = offer; ConsentPanel.Visibility = Visibility.Collapsed;
            await ClearSelectedAsync();
            FileNameText.Text = OfferName(offer.Offer);
            TransferProgress.IsIndeterminate = true; TransferProgress.Visibility = Visibility.Visible;
            StatusText.Text = "Receiving..."; RefreshActions();
            _trackedIncoming.Add(offer);
            TelemetryService.Instance.TrackShareStarted(offer.Peer.Transport, "receive", offer.Offer);
            try { await offer.CompleteAsync(true); }
            catch (Exception ex) { offer.CancelTransfer(); FinishIncoming(offer, false, ex.Message); }
        }
        private async void OnDeny(object sender, RoutedEventArgs e)
        {
            var offer = _pending; if (offer == null) return;
            _pending = null; ConsentPanel.Visibility = Visibility.Collapsed;
            try { await offer.CompleteAsync(false); } catch { offer.CancelTransfer(); }
            await ClearSelectedAsync(); StatusText.Text = "Share declined"; RefreshActions(); ShowNextOffer();
        }
        private void FinishIncoming(ShareOfferReceivedEventArgs offer, bool ok, string message)
        {
            if (_trackedIncoming.Remove(offer))
            {
                if (ok) TelemetryService.Instance.TrackShareSucceeded(offer.Peer.Transport, "receive", offer.Offer);
                else if (offer.IsCancellationRequested || message == AppText.Get("ShareCanceledMessage"))
                    TelemetryService.Instance.TrackShareCanceled(offer.Peer.Transport, "receive", offer.Offer);
                else TelemetryService.Instance.TrackShareFailed(offer.Peer.Transport, "receive", offer.Offer, "transfer_failed");
            }
            if (_offers.Contains(offer)) _finished.Add(offer);
            var current = ReferenceEquals(_pending, offer) || ReferenceEquals(_receiving, offer);
            List<string> paths; _saved.TryGetValue(offer, out paths); _saved.Remove(offer);
            Uri link; _links.TryGetValue(offer, out link); _links.Remove(offer);
            if (!current) return;
            _finished.Remove(offer); _pending = null; _receiving = null;
            ConsentPanel.Visibility = TransferProgress.Visibility = Visibility.Collapsed;
            FileNameText.Text = _selected == null ? "" : OfferName(_selected); StatusText.Text = ok ? "Share complete" : message;
            if (ok && link != null) { var ignored = OpenLinkAsync(link); }
            else if (ok && paths != null && paths.Count > 0) { _lastSaved = paths; ReceivedPanel.Visibility = Visibility.Visible; }
            RefreshActions(); ShowNextOffer();
        }
        private async Task OpenLinkAsync(Uri link)
        { try { await Launcher.LaunchUriAsync(link); } catch (Exception ex) { StatusText.Text = ex.Message; } }
        private async void OnViewFiles(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_lastSaved.Count == 1) await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(_lastSaved[0]));
                else
                {
                    var panel = new StackPanel();
                    foreach (var path in _lastSaved)
                    {
                        var savedPath = path; var button = new Button { Content = System.IO.Path.GetFileName(path) };
                        button.Click += async (s, args) => { try { await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(savedPath)); } catch (Exception ex) { StatusText.Text = ex.Message; } };
                        panel.Children.Add(button);
                    }
                    await new ContentDialog { Title = "Received files", Content = panel, PrimaryButtonText = "Close" }.ShowAsync();
                }
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }
        private void ClearReceived() { ReceivedPanel.Visibility = Visibility.Collapsed; _lastSaved.Clear(); }
        private void OnDismiss(object sender, RoutedEventArgs e) { ClearReceived(); RefreshActions(); }
        private async void OnSupportedPeers(object sender, RoutedEventArgs e)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            var quick = new CheckBox { Content = "Google Quick Share", IsChecked = !values.ContainsKey("QuickShareEnabled") || (bool)values["QuickShareEnabled"] };
            var nearby = new CheckBox { Content = "Microsoft Nearby Share", IsChecked = values.ContainsKey("NearbyEnabled") && (bool)values["NearbyEnabled"] };
            var panel = new StackPanel(); panel.Children.Add(nearby); panel.Children.Add(quick);
            var dialog = new ContentDialog { Title = "Supported peers", Content = panel, PrimaryButtonText = "OK", SecondaryButtonText = "Cancel" };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            { values["QuickShareEnabled"] = quick.IsChecked == true; values["NearbyEnabled"] = nearby.IsChecked == true; await RestartAsync(); }
        }
        private async void OnChangeHostname(object sender, RoutedEventArgs e)
        {
            var values = ApplicationData.Current.LocalSettings.Values;
            var input = new TextBox { Text = values.ContainsKey("DisplayName") ? (string)values["DisplayName"] : ShareCoordinator.GetHostDisplayName("LiveDrop"), MaxLength = 32 };
            var dialog = new ContentDialog { Title = AppText.Get("ChangeHostname"), Content = input, PrimaryButtonText = "OK", SecondaryButtonText = "Cancel" };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var name = input.Text.Trim();
            if (name.Length == 0) { StatusText.Text = AppText.Get("EnterAtLeastOneLetterOrNumber"); return; }
            foreach (var c in name)
                if (!char.IsLetterOrDigit(c) && " '._-()&+".IndexOf(c) < 0)
                { StatusText.Text = AppText.Get("UseLettersNumbersSpacesAndOnly"); return; }
            values["DisplayName"] = name;
            await RestartAsync();
        }
        private async void OnOpenFolder(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = await TransferFileStore.GetReceiveFolderAsync(null);
                var files = await folder.GetFilesAsync();
                var panel = new StackPanel();
                if (files.Count == 0) panel.Children.Add(new TextBlock { Text = "No received files yet." });
                foreach (var file in files.Where(f => !f.Name.Contains(".livedrop-part")))
                {
                    var saved = file;
                    var button = new Button { Content = file.Name };
                    button.Click += async (s, args) => { try { await Launcher.LaunchFileAsync(saved); } catch (Exception ex) { StatusText.Text = ex.Message; } };
                    panel.Children.Add(button);
                }
                await new ContentDialog { Title = AppText.Get("LiveDropFolder"), Content = new ScrollViewer { Content = panel, MaxHeight = 360 }, PrimaryButtonText = "Close" }.ShowAsync();
            }
            catch (Exception ex) { StatusText.Text = ex.Message; }
        }
        private async void OnAbout(object sender, RoutedEventArgs e)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { FontSize = 14, Text = "Version " + Windows.ApplicationModel.Package.Current.Id.Version.Major + "." + Windows.ApplicationModel.Package.Current.Id.Version.Minor + "." + Windows.ApplicationModel.Package.Current.Id.Version.Build + "." + Windows.ApplicationModel.Package.Current.Id.Version.Revision });
            panel.Children.Add(new HyperlinkButton { FontSize = 14, Padding = new Thickness(0, 4, 0, 4), Content = "Developed by retiredlake", NavigateUri = new Uri("https://github.com/RetiredLake/LiveDrop") });
            panel.Children.Add(new HyperlinkButton { FontSize = 14, Padding = new Thickness(0, 4, 0, 4), Content = AppText.Get("PrivacyPolicy"), NavigateUri = new Uri("https://retiredlake.com/privacy.html") });
            await new ContentDialog { Title = AppText.Get("About"), Content = panel, PrimaryButtonText = "Close" }.ShowAsync();
        }
    }
}
