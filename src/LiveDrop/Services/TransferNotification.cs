using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Xml.Dom;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.System;
using Windows.UI.Notifications;

namespace LiveDrop.Services
{
    internal static class TransferNotification
    {
        private const string Group = "LiveDrop";
        private const string OpenPrefix = "open-saved:";
        private static readonly object TargetLock = new object();

        internal static void Show(string title, string body)
        {
            try { ShowToast(title, body, null, "transfer", false); } catch { }
        }

        internal static Session Begin(string title, string body, string fileName = null, string heading = null) { return new Session(title, body, fileName, heading); }

        internal static bool IsTransferActivation(string argument)
        {
            return argument == "transfer" || (argument ?? "").StartsWith(OpenPrefix, StringComparison.Ordinal);
        }

        private static void ShowToast(string title, string body, string tag, string launch, bool progress, string fileName = null, string heading = null)
        {
            var xml = BuildXml(title, body, launch, progress, heading);
            var toast = new ToastNotification(xml) { Group = Group };
            if (tag != null) toast.Tag = tag;
            if (progress) InitializeNativeProgress(toast, fileName ?? body);
            ToastNotificationManager.CreateToastNotifier().Show(toast);
        }

        internal static XmlDocument BuildXml(string title, string body, string launch, bool progress, string heading = null)
        {
            var xml = progress ? new XmlDocument() : ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            if (progress)
            {
                xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text/></binding></visual></toast>");
                xml.GetElementsByTagName("text")[0].InnerText = string.IsNullOrWhiteSpace(heading) ? title ?? "LiveDrop" : heading;
            }
            xml.DocumentElement.SetAttribute("launch", launch);
            if (!progress)
            {
                var text = xml.GetElementsByTagName("text");
                text[0].InnerText = title ?? "LiveDrop";
                text[1].InnerText = body ?? "";
            }
            if (progress)
            {
                var bar = xml.CreateElement("progress");
                bar.SetAttribute("title", "{fileName}");
                bar.SetAttribute("value", "{progressValue}");
                bar.SetAttribute("valueStringOverride", "{percentage}");
                bar.SetAttribute("status", (title ?? AppText.Get("Transferring")).TrimEnd('.'));
                xml.GetElementsByTagName("binding")[0].AppendChild(bar);
            }
            return xml;
        }

        private static void InitializeNativeProgress(ToastNotification toast, string body)
        {
            toast.Data = new NotificationData();
            toast.Data.Values["fileName"] = body ?? "";
            toast.Data.Values["progressValue"] = "indeterminate";
            toast.Data.Values["percentage"] = "";
        }

        private static void UpdateNativeProgress(Models.ShareProgress progress, uint sequence, string tag)
        {
            var fraction = progress.TotalBytes <= 0 ? 0 : Math.Max(0, Math.Min(1, (double)progress.BytesTransferred / progress.TotalBytes));
            var data = new NotificationData { SequenceNumber = sequence };
            data.Values["fileName"] = progress.FileName;
            data.Values["progressValue"] = fraction.ToString(CultureInfo.InvariantCulture);
            data.Values["percentage"] = fraction.ToString("P0", CultureInfo.CurrentCulture);
            ToastNotificationManager.CreateToastNotifier().Update(data, tag, Group);
        }

        private static string RegisterSavedTarget(string path, bool folder)
        {
            if (string.IsNullOrWhiteSpace(path)) return "transfer";
            return RegisterTarget((folder ? "folder|" : "file|") + path);
        }

        private static string RegisterTarget(string target)
        {
            lock (TargetLock)
            {
            var tokens = ApplicationData.Current.LocalSettings.CreateContainer("SavedNotificationTargets", ApplicationDataCreateDisposition.Always).Values;
            while (tokens.Count >= 64) tokens.Remove(tokens.Keys.First());
            var token = Guid.NewGuid().ToString("N");
            tokens[token] = target;
            return OpenPrefix + token;
            }
        }

        internal static async Task HandleActivationAsync(string argument)
        {
            if (!(argument ?? "").StartsWith(OpenPrefix, StringComparison.Ordinal)) return;
            Guid token;
            if (!Guid.TryParseExact(argument.Substring(OpenPrefix.Length), "N", out token)) return;
            try
            {
                var tokens = ApplicationData.Current.LocalSettings.CreateContainer("SavedNotificationTargets", ApplicationDataCreateDisposition.Always).Values;
                object value;
                if (!tokens.TryGetValue(token.ToString("N"), out value)) return;
                var target = value as string;
                bool opened;
                Uri link;
                if (TryGetWebLinkTarget(target, out link))
                    opened = await Launcher.LaunchUriAsync(link);
                else if (target != null && target.StartsWith("folder|", StringComparison.Ordinal))
                    opened = await Launcher.LaunchFolderAsync(await StorageFolder.GetFolderFromPathAsync(target.Substring(7)));
                else if (target != null && target.StartsWith("file|", StringComparison.Ordinal))
                    opened = await Launcher.LaunchFileAsync(await StorageFile.GetFileFromPathAsync(target.Substring(5)));
                else return;
                if (!opened) Show(AppText.Get("CouldNotOpenSavedContent"), AppText.Get("NoApplicationCouldOpenThisFileOrFolder"));
            }
            catch { Show(AppText.Get("CouldNotOpenSavedContent"), AppText.Get("TheSavedFileOrFolderIsNoLonger")); }
        }

        internal static bool TryGetWebLinkTarget(string target, out Uri link)
        {
            link = null;
            return target != null && target.StartsWith("link|", StringComparison.Ordinal) &&
                Uri.TryCreate(target.Substring(5), UriKind.Absolute, out link) &&
                (link.Scheme == "http" || link.Scheme == "https");
        }

        internal sealed class Session
        {
            private readonly object _sync = new object();
            private readonly string _tag = Guid.NewGuid().ToString("N").Substring(0, 16);
            private readonly bool _nativeProgress;
            private readonly HashSet<string> _saved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private DateTime _lastUpdate = DateTime.MinValue;
            private bool _finished;
            private Uri _receivedLink;
            private string _launchArgument = "transfer";
            private uint _sequence;

            internal Session(string title, string body, string fileName, string heading)
            {
                try
                {
                    _nativeProgress = ApiInformation.IsTypePresent("Windows.UI.Notifications.NotificationData") &&
                        ApiInformation.IsPropertyPresent("Windows.UI.Notifications.ToastNotification", "Data");
                    ShowToast(title, body, _tag, "transfer", _nativeProgress, fileName, heading);
                }
                catch
                {
                    _nativeProgress = false;
                    try { ShowToast(title, body, _tag, "transfer", false); } catch { }
                }
            }

            internal void Update(Models.ShareProgress progress)
            {
                if (progress == null) return;
                lock (_sync)
                {
                    if (_finished) return;
                    if (progress.ReceivedWebLink != null && _receivedLink == null) _receivedLink = progress.ReceivedWebLink;
                    if (!string.IsNullOrWhiteSpace(progress.SavedFilePath)) _saved.Add(progress.SavedFilePath);
                    if (!_nativeProgress || DateTime.UtcNow - _lastUpdate < TimeSpan.FromSeconds(1)) return;
                    _lastUpdate = DateTime.UtcNow;
                    try
                    {
                        UpdateNativeProgress(progress, ++_sequence, _tag);
                    }
                    catch { }
                }
            }

            internal void Complete(bool succeeded, string body)
            {
                lock (_sync)
                {
                    if (_finished) return;
                    _finished = true;
                    try
                    {
                        var launch = "transfer";
                        try
                        {
                            if (succeeded && _saved.Count == 1) launch = RegisterSavedTarget(_saved.First(), false);
                            else if (succeeded && _saved.Count > 1) launch = RegisterSavedTarget(Path.GetDirectoryName(_saved.First()), true);
                            else if (succeeded && _receivedLink != null && (_receivedLink.Scheme == "http" || _receivedLink.Scheme == "https"))
                                launch = RegisterTarget("link|" + _receivedLink.AbsoluteUri);
                        }
                        catch { }
                        _launchArgument = launch;
                        if (succeeded && _saved.Count > 0) body = AppText.Get("PressToViewFiles");
                        else if (succeeded && _receivedLink != null) body = AppText.Get("PressToViewLink");
                        ShowToast(succeeded ? AppText.Get("ShareComplete") : body == AppText.Get("ShareCanceledMessage") ? AppText.Get("ShareCanceledTitle") : AppText.Get("ShareFailed"), body, _tag, launch, false);
                    }
                    catch { }
                }
            }

            internal Task OpenSavedContentAsync()
            {
                string launchArgument;
                lock (_sync) launchArgument = _launchArgument;
                return HandleActivationAsync(launchArgument);
            }
        }
    }
}
