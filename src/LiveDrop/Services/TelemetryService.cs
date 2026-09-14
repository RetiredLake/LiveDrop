using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Data.Json;
using Windows.Storage;
using Windows.System.Profile;
using LiveDrop.Models;
using LiveDrop.Protocols;

namespace LiveDrop.Services
{
    // Best-effort anonymous product telemetry. Every public entry point is
    // deliberately no-throw so reporting can never affect sharing or updates.
    internal sealed class TelemetryService
    {
        private const string BatchUrl = "https://us.i.posthog.com/batch/";
        // PostHog project tokens are write-only client ingestion tokens.
        private const string ProjectToken = "phc_zBgzCssgAhmykdjXFk6UUoQP5BEHpMVrsXoyFspaCntP";
        private const string InstallationIdKey = "LiveDrop.TelemetryInstallationId";
        private const string InstallReportedKey = "LiveDrop.TelemetryInstallReported";
        private const string EnabledKey = "LiveDrop.TelemetryEnabled";
        private const string QueueFileName = "LiveDrop.TelemetryQueue.json";
        private const int BatchSize = 20;
        private const int MaximumQueueSize = 100;

        private static readonly HttpClient Client = CreateClient();
        private readonly object _queueLock = new object();
        private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
        private readonly List<string> _pendingEvents = new List<string>();
        private bool _initialized;
        private bool _queueLoaded;
        private bool _enabled;
        private string _installationId;
        private string _appVersion;
        private string _deviceFamily;

        internal static TelemetryService Instance { get; } = new TelemetryService();

        private TelemetryService() { }

        internal void TrackApplicationStarted()
        {
            try
            {
                Initialize();
                if (!_enabled) return;

                var settings = ApplicationData.Current.LocalSettings.Values;
                var installReported = settings[InstallReportedKey] is bool && (bool)settings[InstallReportedKey];
                if (!installReported)
                {
                    settings[InstallReportedKey] = true;
                    Track("installation created", null);
                }
                Track("application opened", null);
            }
            catch { }
        }

        internal void TrackShareStarted(ShareTransport transport, string direction, ShareOffer offer)
        {
            TrackShareEvent("share started", transport, direction, offer, null);
        }

        internal void TrackShareSucceeded(ShareTransport transport, string direction, ShareOffer offer)
        {
            TrackShareEvent("share succeeded", transport, direction, offer, null);
        }

        internal void TrackShareFailed(ShareTransport transport, string direction, ShareOffer offer, string errorCategory)
        {
            TrackShareEvent("share failed", transport, direction, offer, errorCategory);
        }

        internal void TrackShareCanceled(ShareTransport transport, string direction, ShareOffer offer)
        {
            TrackShareEvent("share canceled", transport, direction, offer, "canceled");
        }

        internal void TrackUpdateCheck(string result)
        {
            Track("update checked", new Dictionary<string, string>
            {
                { "result", result ?? "unknown" }
            });
        }

        internal void TrackUpdateAvailable(Version version)
        {
            Track("update available", new Dictionary<string, string>
            {
                { "version", version == null ? "unknown" : version.ToString(4) }
            });
        }

        internal void TrackUpdateDownloadStarted(Version version)
        {
            Track("update download started", new Dictionary<string, string>
            {
                { "version", version == null ? "unknown" : version.ToString(4) }
            });
        }

        internal void TrackUpdateInstallHandoff(Version version)
        {
            Track("update install handed off", new Dictionary<string, string>
            {
                { "version", version == null ? "unknown" : version.ToString(4) }
            });
        }

        internal void TrackUpdateFailed(string errorCategory)
        {
            Track("update failed", new Dictionary<string, string>
            {
                { "error_category", errorCategory ?? "unknown" }
            });
        }

        internal static string ClassifyFailure(Exception exception)
        {
            if (exception is TimeoutException || exception is TaskCanceledException) return "timeout";
            if (exception is OperationCanceledException) return "canceled";
            if (exception is HttpRequestException) return "network";
            if (exception is IOException) return "storage";
            if (exception is ShareProtocolException) return "protocol";
            return "unknown";
        }

        private void TrackShareEvent(string eventName, ShareTransport transport, string direction, ShareOffer offer, string errorCategory)
        {
            try
            {
                var properties = new Dictionary<string, string>
                {
                    { "protocol", ProtocolName(transport) },
                    { "direction", string.IsNullOrWhiteSpace(direction) ? "unknown" : direction },
                    { "file_count", FileCount(offer).ToString(CultureInfo.InvariantCulture) },
                    { "size_bucket", SizeBucket(TotalBytes(offer)) }
                };
                if (!string.IsNullOrWhiteSpace(errorCategory)) properties["error_category"] = errorCategory;
                Track(eventName, properties);
            }
            catch { }
        }

        private void Initialize()
        {
            if (_initialized) return;
            try
            {
                var settings = ApplicationData.Current.LocalSettings.Values;
                var enabledValue = settings[EnabledKey];
                _enabled = !(enabledValue is bool) || (bool)enabledValue;
                _installationId = settings[InstallationIdKey] as string;
                Guid parsed;
                if (!Guid.TryParse(_installationId, out parsed))
                {
                    _installationId = Guid.NewGuid().ToString("D");
                    settings[InstallationIdKey] = _installationId;
                }
                _appVersion = GetAppVersion();
                _deviceFamily = GetDeviceFamily();
            }
            catch
            {
                _enabled = false;
            }
            _initialized = true;
        }

        private void Track(string eventName, IDictionary<string, string> additionalProperties)
        {
            try
            {
                Initialize();
                if (!_enabled || string.IsNullOrWhiteSpace(eventName)) return;

                var properties = new JsonObject();
                properties["distinct_id"] = JsonValue.CreateStringValue(_installationId);
                properties["$process_person_profile"] = JsonValue.CreateBooleanValue(false);
                properties["app_version"] = JsonValue.CreateStringValue(_appVersion ?? "unknown");
                properties["device_family"] = JsonValue.CreateStringValue(_deviceFamily ?? "unknown");
                if (additionalProperties != null)
                {
                    foreach (var property in additionalProperties)
                    {
                        if (string.IsNullOrWhiteSpace(property.Key) || property.Key == "distinct_id") continue;
                        properties[property.Key] = JsonValue.CreateStringValue(property.Value ?? string.Empty);
                    }
                }

                var item = new JsonObject();
                item["event"] = JsonValue.CreateStringValue(eventName);
                item["properties"] = properties;
                lock (_queueLock)
                {
                    _pendingEvents.Add(item.Stringify());
                    TrimQueueLocked();
                }
                _ = FlushSilentlyAsync();
            }
            catch { }
        }

        private async Task FlushSilentlyAsync()
        {
            try
            {
                await _flushLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    await LoadQueueSilentlyAsync().ConfigureAwait(false);
                    while (true)
                    {
                        List<string> batch;
                        lock (_queueLock)
                        {
                            batch = _pendingEvents.Take(BatchSize).ToList();
                        }
                        if (batch.Count == 0)
                        {
                            await SaveQueueSilentlyAsync().ConfigureAwait(false);
                            return;
                        }

                        var result = await SendBatchSilentlyAsync(batch).ConfigureAwait(false);
                        if (result == SendResult.RetryLater)
                        {
                            await SaveQueueSilentlyAsync().ConfigureAwait(false);
                            return;
                        }

                        lock (_queueLock)
                        {
                            _pendingEvents.RemoveRange(0, Math.Min(batch.Count, _pendingEvents.Count));
                        }
                        await SaveQueueSilentlyAsync().ConfigureAwait(false);
                        if (result == SendResult.Drop) return;
                    }
                }
                finally
                {
                    _flushLock.Release();
                }
            }
            catch { }
        }

        private async Task LoadQueueSilentlyAsync()
        {
            if (_queueLoaded) return;
            _queueLoaded = true;
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(QueueFileName).AsTask().ConfigureAwait(false);
                var root = JsonArray.Parse(await FileIO.ReadTextAsync(file).AsTask().ConfigureAwait(false));
                lock (_queueLock)
                {
                    foreach (var value in root)
                    {
                        if (value.ValueType == JsonValueType.Object) _pendingEvents.Add(value.Stringify());
                    }
                    TrimQueueLocked();
                }
            }
            catch (FileNotFoundException) { }
            catch { }
        }

        private async Task SaveQueueSilentlyAsync()
        {
            try
            {
                string[] snapshot;
                lock (_queueLock) snapshot = _pendingEvents.ToArray();
                if (snapshot.Length == 0)
                {
                    try
                    {
                        var oldFile = await ApplicationData.Current.LocalFolder.GetFileAsync(QueueFileName).AsTask().ConfigureAwait(false);
                        await oldFile.DeleteAsync().AsTask().ConfigureAwait(false);
                    }
                    catch { }
                    return;
                }

                var root = new JsonArray();
                foreach (var item in snapshot) root.Add(JsonObject.Parse(item));
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    QueueFileName, CreationCollisionOption.ReplaceExisting).AsTask().ConfigureAwait(false);
                await FileIO.WriteTextAsync(file, root.Stringify()).AsTask().ConfigureAwait(false);
            }
            catch { }
        }

        private static async Task<SendResult> SendBatchSilentlyAsync(IList<string> batch)
        {
            try
            {
                var root = new JsonObject();
                root["api_key"] = JsonValue.CreateStringValue(ProjectToken);
                var events = new JsonArray();
                foreach (var item in batch) events.Add(JsonObject.Parse(item));
                root["batch"] = events;

                using (var content = new StringContent(root.Stringify(), Encoding.UTF8, "application/json"))
                using (var response = await Client.PostAsync(BatchUrl, content).ConfigureAwait(false))
                {
                    if (response.IsSuccessStatusCode) return SendResult.Succeeded;
                    var status = (int)response.StatusCode;
                    return status == 408 || status == 429 || status >= 500
                        ? SendResult.RetryLater
                        : SendResult.Drop;
                }
            }
            catch { return SendResult.RetryLater; }
        }

        private void TrimQueueLocked()
        {
            if (_pendingEvents.Count <= MaximumQueueSize) return;
            _pendingEvents.RemoveRange(0, _pendingEvents.Count - MaximumQueueSize);
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("LiveDrop/0.1.5.6");
            return client;
        }

        private static string GetAppVersion()
        {
            try
            {
                var version = Package.Current.Id.Version;
                return version.Major + "." + version.Minor + "." + version.Build + "." + version.Revision;
            }
            catch { return "unknown"; }
        }

        private static string GetDeviceFamily()
        {
            try { return AnalyticsInfo.VersionInfo.DeviceFamily ?? "unknown"; }
            catch { return "unknown"; }
        }

        private static string ProtocolName(ShareTransport transport)
        {
            switch (transport)
            {
                case ShareTransport.GoogleQuickShare: return "google_quick_share";
                case ShareTransport.MicrosoftNearby: return "microsoft_nearby_share";
                default: return "unknown";
            }
        }

        private static int FileCount(ShareOffer offer)
        {
            return offer == null || offer.Files == null ? 0 : offer.Files.Count;
        }

        private static long TotalBytes(ShareOffer offer)
        {
            if (offer == null || offer.Files == null) return 0;
            long total = 0;
            foreach (var file in offer.Files)
            {
                if (file == null || file.Size <= 0 || long.MaxValue - total < file.Size) return long.MaxValue;
                total += file.Size;
            }
            return total;
        }

        private static string SizeBucket(long bytes)
        {
            if (bytes <= 0) return "0";
            if (bytes < 1024 * 1024) return "under_1mb";
            if (bytes < 10L * 1024 * 1024) return "1mb_to_10mb";
            if (bytes < 100L * 1024 * 1024) return "10mb_to_100mb";
            if (bytes < 1024L * 1024 * 1024) return "100mb_to_1gb";
            return "1gb_or_more";
        }

        private enum SendResult
        {
            Succeeded,
            RetryLater,
            Drop
        }
    }
}
