using System;
using LiveDrop;
using LiveDrop.Models;

internal static class PeerIdentitySmoke
{
    private static void Main()
    {
        TestResources.Initialize();
        var oldPeer = new PeerDescriptor("old-id", "Pixel", ShareTransport.GoogleQuickShare, "192.168.1.42", 12345, "");
        var newPeer = new PeerDescriptor("new-id", "Pixel", ShareTransport.GoogleQuickShare, "192.168.1.42", 23456, "");
        var item = new PeerListItem(oldPeer, false);
        if (!item.Matches(newPeer) || item.Key != PeerListItem.GetKey(newPeer))
            throw new Exception("Changed ID and port on the same IP must reuse one peer and expiry key.");
        var labelChanged = false;
        item.PropertyChanged += (sender, args) => { if (args.PropertyName == "DisplayText") labelChanged = true; };
        item.Update(newPeer);
        if (!labelChanged || item.DisplayText != newPeer.DisplayName) throw new Exception("A resolved device name must update the bound label.");
        if (item.Peer.Port != 23456) throw new Exception("The existing row must use the current port.");
        if (item.Matches(new PeerDescriptor("other", "Pixel", ShareTransport.GoogleQuickShare, "192.168.1.43", 23456, "")))
            throw new Exception("Different IPs must not merge because their names match.");
        if (!item.Matches(new PeerDescriptor("new-id", "Pixel", ShareTransport.MicrosoftNearby, "192.168.1.42", 5040, "")))
            throw new Exception("Two protocols on the same IP must use one device row.");
        Console.WriteLine("PASS: peer identity across changed ID/port, current endpoint, distinct devices and shared protocol rows");
    }
}
