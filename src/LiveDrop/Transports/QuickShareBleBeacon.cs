using System;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Foundation.Metadata;
using Windows.Storage.Streams;

namespace LiveDrop.Transports
{
    internal sealed class QuickShareBleBeacon : IDisposable
    {
        private BluetoothLEAdvertisementPublisher _publisher;

        internal bool IsStarted { get { return _publisher != null; } }

        internal async System.Threading.Tasks.Task<bool> StartAsync()
        {
            if (!ApiInformation.IsTypePresent("Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher"))
                return false;

            try
            {
                var advertisement = new BluetoothLEAdvertisement();
                // This is the known Quick Share trigger advertisement. It
                // wakes the Android discovery surface; identity and the TCP
                // endpoint still come from mDNS.
                var serviceData = new byte[]
                {
                    0x2C, 0xFE,
                    0xFC, 0x12, 0x8E, 0x01, 0x42,
                    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
                    0xBF, 0x2D, 0x5B, 0xA0, 0xE1, 0xD8, 0x75, 0x24, 0xCA, 0x00
                };
                using (var writer = new DataWriter())
                {
                    writer.WriteBytes(serviceData);
                    advertisement.DataSections.Add(new BluetoothLEAdvertisementDataSection(0x16, writer.DetachBuffer()));
                }

                _publisher = new BluetoothLEAdvertisementPublisher(advertisement);
                var started = new System.Threading.Tasks.TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<BluetoothLEAdvertisementPublisher, BluetoothLEAdvertisementPublisherStatusChangedEventArgs> handler = (sender, args) =>
                {
                    if (args.Status == BluetoothLEAdvertisementPublisherStatus.Started) started.TrySetResult(true);
                    else if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted) started.TrySetResult(false);
                };
                _publisher.StatusChanged += handler;
                _publisher.Start();
                await System.Threading.Tasks.Task.WhenAny(started.Task, System.Threading.Tasks.Task.Delay(3000));
                _publisher.StatusChanged -= handler;
                var active = _publisher.Status == BluetoothLEAdvertisementPublisherStatus.Started;
                if (!active) Dispose();
                return active;
            }
            catch
            {
                Dispose();
                return false;
            }
        }

        public void Dispose()
        {
            if (_publisher == null) return;
            try { _publisher.Stop(); } catch { }
            _publisher = null;
        }
    }
}
