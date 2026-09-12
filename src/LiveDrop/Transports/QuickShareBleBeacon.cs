using System;
using System.Security.Cryptography;
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
                var serviceData = new byte[26];
                serviceData[0] = 0x2C;
                serviceData[1] = 0xFE;
                System.Buffer.BlockCopy(new byte[] { 0xFC, 0x12, 0x8E, 0x01, 0x42 }, 0, serviceData, 2, 5);
                using (var random = RandomNumberGenerator.Create())
                {
                    var randomBytes = new byte[10];
                    random.GetBytes(randomBytes);
                    System.Buffer.BlockCopy(randomBytes, 0, serviceData, 16, randomBytes.Length);
                }
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
