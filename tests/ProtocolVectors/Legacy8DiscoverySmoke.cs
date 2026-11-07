using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveDrop.Transports;

internal static class Legacy8DiscoverySmoke
{
    private static void Main()
    {
        TestResources.Initialize();
        try { RunAsync().GetAwaiter().GetResult(); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
    }
    private static async Task RunAsync()
    {
        const string serviceType = "_livedrop8-test._udp.local.";
        var host = MdnsMulticastService.GetLanHosts().FirstOrDefault();
        if (host == null) throw new Exception("No IPv4 LAN available for integration test.");
        var id = Guid.NewGuid().ToString("N");
        for (int iteration = 0; iteration < 3; iteration++)
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
            using (var first = new MdnsMulticastService(serviceType))
            using (var second = new MdnsMulticastService(serviceType))
            {
                var received = new TaskCompletionSource<bool>();
                second.ServicesReceived += (s, e) =>
                {
                    if (e.Any(r => r.InstanceName.StartsWith(id, StringComparison.Ordinal) && r.Address == host.RawName && r.Port == 54321)) received.TrySetResult(true);
                };
                first.ReceiveFailed += ex => received.TrySetException(ex);
                second.ReceiveFailed += ex => received.TrySetException(ex);
                using (timeout.Token.Register(() => received.TrySetCanceled()))
                {
                    // Both must bind 5353 alongside existing OS/application listeners.
                    await first.StartAsync(timeout.Token);
                    await second.StartAsync(timeout.Token);
                    await first.AnnounceAsync(id, id + ".local.", host.RawName, 54321, new byte[18], timeout.Token);
                    await received.Task;
                    await second.QueryAsync(timeout.Token);
                }
            }
        }
        Console.WriteLine("PASS: 8.1 mDNS shares UDP 5353, receives/decodes real multicast announcements, and restarts three times.");
    }
}
