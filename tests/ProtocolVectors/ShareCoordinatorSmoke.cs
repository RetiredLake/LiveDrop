using System;
using System.Threading;
using System.Threading.Tasks;
using LiveDrop.Models;
using LiveDrop.Protocols;
using LiveDrop.Services;

// Compile with the production ShareCoordinator and ShareProtocol. Only network
// adapters/models are replaced, allowing shutdown after a view stops pumping UI.
namespace LiveDrop.Models
{
    public enum ShareTransport { MicrosoftNearby, GoogleQuickShare }
    public class PeerDescriptor { public ShareTransport Transport; }
    public class ShareOffer { }
    public class ShareProgress { }
    public class PeerDiscoveredEventArgs : EventArgs { }
    public class ShareOfferReceivedEventArgs : EventArgs { }
    public class StatusChangedEventArgs : EventArgs { public StatusChangedEventArgs(string text) { } }
}

internal abstract class FakeAdapter : IShareProtocolAdapter
{
    internal static int Created, Started, Stopped;
    protected FakeAdapter() { Interlocked.Increment(ref Created); }
    public string Name { get { return "Fake adapter"; } }
    public abstract ShareTransport Transport { get; }
    public event EventHandler<PeerDiscoveredEventArgs> PeerDiscovered { add { } remove { } }
    public event EventHandler<ShareOfferReceivedEventArgs> OfferReceived { add { } remove { } }
    public event EventHandler<StatusChangedEventArgs> StatusChanged { add { } remove { } }
    public Task StartAsync(CancellationToken token) { Interlocked.Increment(ref Started); return Task.CompletedTask; }
    public Task StopAsync() { return Task.Run(() => { Thread.Sleep(30); Interlocked.Increment(ref Stopped); }); }
    public Task SendAsync(PeerDescriptor peer, ShareOffer offer, IProgress<ShareProgress> progress, CancellationToken token) { return Task.CompletedTask; }
    public void Dispose() { }
}
namespace LiveDrop.Protocols.Nearby
{
    internal sealed class NearbyCdpAdapter : FakeAdapter
    {
        internal NearbyCdpAdapter(string name) { }
        public override ShareTransport Transport { get { return ShareTransport.MicrosoftNearby; } }
    }
}
namespace LiveDrop.Protocols.QuickShare
{
    internal sealed class QuickShareAdapter : FakeAdapter
    {
        internal QuickShareAdapter(string name) { }
        public override ShareTransport Transport { get { return ShareTransport.GoogleQuickShare; } }
    }
}
internal static class ShareCoordinatorSmoke
{
    private sealed class ClosedViewContext : SynchronizationContext
    {
        internal int Posted;
        public override void Post(SendOrPostCallback callback, object state) { Interlocked.Increment(ref Posted); }
    }
    private static void Main()
    {
        for (var mask = 0; mask < 4; mask++)
        {
            FakeAdapter.Created = FakeAdapter.Started = FakeAdapter.Stopped = 0;
            var expected = ((mask & 1) != 0 ? 1 : 0) + ((mask & 2) != 0 ? 1 : 0);
            using (var coordinator = new ShareCoordinator("Test", (mask & 1) != 0, (mask & 2) != 0))
            {
                coordinator.StartAsync().GetAwaiter().GetResult();
                if (FakeAdapter.Created != expected || FakeAdapter.Started != expected) throw new Exception("Disabled adapter started");
                var context = new ClosedViewContext();
                SynchronizationContext.SetSynchronizationContext(context);
                var stop = coordinator.StopAsync();
                SynchronizationContext.SetSynchronizationContext(null);
                if (!stop.Wait(3000) || context.Posted != 0 || FakeAdapter.Stopped != expected)
                    throw new Exception("Shutdown depends on a closed view's synchronization context");
            }
        }
        Console.WriteLine("PASS: all four protocol selections and shutdown without a UI message pump");
    }
}
