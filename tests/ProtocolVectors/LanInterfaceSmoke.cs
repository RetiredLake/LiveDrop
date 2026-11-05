using System;
using System.Linq;
using Windows.Networking.Connectivity;
using LiveDrop.Transports;

internal static class LanInterfaceSmoke
{
    private static void Main()
    {
        TestResources.Initialize();
        var hosts = MdnsMulticastService.GetLanHosts();
        var primary = NetworkInformation.GetInternetConnectionProfile();
        if (hosts.Count == 0) throw new Exception("No LAN interface selected.");
        if (primary != null && primary.NetworkAdapter != null && !primary.IsWwanConnectionProfile && hosts.Any(host =>
            host.IPInformation.NetworkAdapter.NetworkAdapterId != primary.NetworkAdapter.NetworkAdapterId))
            throw new Exception("A non-primary interface leaked into LAN announcements.");
        Console.WriteLine("PASS: LAN announcement addresses: " + string.Join(", ", hosts.Select(host => host.RawName)));
    }
}
