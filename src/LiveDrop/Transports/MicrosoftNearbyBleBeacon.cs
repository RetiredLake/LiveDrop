using System;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Foundation.Metadata;
using Windows.Storage.Streams;

namespace LiveDrop.Transports
{
    internal sealed class MicrosoftNearbyBleBeacon : IDisposable
    {
        private BluetoothLEAdvertisementPublisher _publisher;

        internal async Task<bool> StartAsync(string deviceName)
        {
            if (!ApiInformation.IsTypePresent("Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher") ||
                !ApiInformation.IsTypePresent("Windows.Devices.Bluetooth.BluetoothAdapter"))
                return false;

            try
            {
                var adapter = await BluetoothAdapter.GetDefaultAsync();
                if (adapter == null || !adapter.IsPeripheralRoleSupported)
                    return false;

                var address = BitConverter.GetBytes(adapter.BluetoothAddress);
                var name = Encoding.UTF8.GetBytes(deviceName ?? "LiveDrop");
                var nameLength = Math.Min(17, name.Length);
                var data = new byte[10 + nameLength];
                data[0] = 1;
                data[1] = 11;
                data[2] = 0x21;
                data[3] = 0x0A;
                System.Buffer.BlockCopy(address, 0, data, 4, 6);
                System.Buffer.BlockCopy(name, 0, data, 10, nameLength);

                using (var writer = new DataWriter())
                {
                    writer.WriteBytes(data);
                    var advertisement = new BluetoothLEAdvertisement();
                    advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData(0x0006, writer.DetachBuffer()));
                    _publisher = new BluetoothLEAdvertisementPublisher(advertisement);
                }
                var started = new TaskCompletionSource<bool>();
                Windows.Foundation.TypedEventHandler<BluetoothLEAdvertisementPublisher, BluetoothLEAdvertisementPublisherStatusChangedEventArgs> handler = (sender, args) =>
                {
                    if (args.Status == BluetoothLEAdvertisementPublisherStatus.Started) started.TrySetResult(true);
                    else if (args.Status == BluetoothLEAdvertisementPublisherStatus.Aborted) started.TrySetResult(false);
                };
                _publisher.StatusChanged += handler;
                _publisher.Start();
                await Task.WhenAny(started.Task, Task.Delay(3000));
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
