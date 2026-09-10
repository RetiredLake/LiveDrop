
# LiveDrop: Windows Phone–first nearby sharing

## Summary

LiveDrop will be a standalone GPL-compatible UWP project, independent of LiveBubbles.

Windows Phone supported proximity APIs, NFC “tap” scenarios, Bluetooth, and app-level peer sockets, but never had native Apple AirDrop or Google Nearby Share/Quick Share. Microsoft Nearby Sharing is primarily a Windows 10/11 PC feature, while Google’s current Quick Share Windows app targets 64-bit PCs. [Windows Phone proximity](https://blogs.windows.com/windowsdeveloper/2013/07/24/proximity-in-windows-phone-8/), [Microsoft Nearby sharing](https://support.microsoft.com/en-us/windows/share-things-with-nearby-devices-in-windows-0efbfe40-e3e2-581b-13f4-1a0e9936c2d9), [Quick Share requirements](https://support.google.com/android/answer/13801258?hl=en)

The first viability milestone must support both:

- Windows Phone ↔ Windows 10/11 PC using Microsoft Nearby-compatible protocols.
- Windows Phone ↔ Android using a LAN-first Google Quick Share implementation.

Native AirDrop interoperability will remain research-only because UWP cannot access the AWDL raw Wi-Fi link layer, monitor mode, or frame injection required by AirDrop. [OWL](https://github.com/seemoo-lab/owl), [OpenDrop](https://github.com/matiskov/opendrop)

## Implementation changes

- Target Windows 10 Mobile RTM-era UWP first, using only the currently available SDK/toolchain. Feature-detect newer APIs and preserve a desktop UWP target for testing.
- Build protocol adapters behind stable interfaces:
  - `PeerDescriptor`: identity, display name, transport, endpoint, and capabilities.
  - `ShareOffer`: files, names, sizes, MIME types, and metadata.
  - `IShareProtocolAdapter`: advertise, discover, negotiate, send, receive, cancel, and report progress.
  - `ITransport`: BLE advertisement, mDNS/DNS-SD, TCP sockets, Wi-Fi Direct, and Bluetooth RFCOMM.
- Implement Microsoft Nearby first from the [MS-CDP](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cdp/f5a15c56-ac3a-48f9-8c51-07b2eadbe9b4) and [MS-NFPS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-nfps/421eed84-90a5-426d-8217-0a7f7667e9ee) specifications, porting the practical behavior from [`nearby-sharing/android`](https://github.com/nearby-sharing/android). That repository is GPL-3.0 and targets modern .NET, so its protocol core will need a UWP-compatible port.
- Implement Google Quick Share as a separate LAN-first adapter using [NearDrop’s protocol documentation](https://github.com/grishka/NearDrop/blob/master/PROTOCOL.md), [`Packet`](https://github.com/nozwock/packet), [`rquickshare`](https://github.com/Martichou/rquickshare), [`pyquickshare`](https://github.com/teaishealthy/pyquickshare), and Google’s [UKEY2 reference](https://chromium.googlesource.com/external/github.com/google/ukey2/).
- Use `DatagramSocket` for deterministic multicast DNS-SD support. Treat `DnssdServiceWatcher` as an optional probe only because Microsoft marks it unsupported. [DnssdServiceWatcher](https://learn.microsoft.com/en-us/uwp/api/windows.networking.servicediscovery.dnssd.dnssdservicewatcher?view=winrt-26100)
- Add Windows Share Sheet integration:
  - Source side through `DataTransferManager`.
  - Receive side through the `windows.shareTarget` manifest extension and `OnShareTargetActivated`.
  - Use Microsoft’s [ShareTarget sample](https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/ShareTarget) as the baseline.
- Use Microsoft UWP samples for implementation patterns:
  - [StreamSocket](https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/StreamSocket)
  - [WiFiDirect](https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/WiFiDirect)
  - [BluetoothAdvertisement](https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/BluetoothAdvertisement)
  - [NetworkHelper](https://github.com/microsoft/Windows-appsample-networkhelper)
  - [Socket background activity](https://learn.microsoft.com/en-us/windows/apps/develop/networking/network-communications-in-the-background)
- Add atomic file writes, SHA-256 verification, cancellation, retry handling, safe filename normalization, and explicit accept/reject UI.
- Release the project under GPLv3 or another license compatible with the selected GPLv3 dependencies, preserving upstream notices and source obligations.

## AirDrop research boundary

Study [OWL](https://github.com/seemoo-lab/owl), [OpenDrop](https://github.com/matiskov/opendrop), [`opendrop-rs`](https://github.com/ayourtch-llm/opendrop-rs), and [PrivateDrop](https://github.com/seemoo-lab/privatedrop) for protocol and security lessons.

Do not promise native AirDrop support in the UWP milestone. A Windows-only UWP app cannot reproduce the AWDL/BLE/radio requirements. Google’s newer Quick Share-to-AirDrop support is limited to selected Android devices and does not expose a public Windows or UWP API. [Google Quick Share/AirDrop](https://www.android.com/quick-share/with-iphone/)

## Test and acceptance plan

- Verify the existing build environment without installing additional tooling.
- Test on real Windows 10 Mobile hardware, beginning with the RTM-era image.
- Microsoft Nearby:
  - Windows Phone → Windows PC.
  - Windows PC → Windows Phone.
  - BLE discovery, IP discovery, authentication, progress, cancellation, and large files.
- Google Quick Share:
  - Windows Phone → Android.
  - Android → Windows Phone.
  - Same-LAN foreground operation first; QR/manual bootstrap is acceptable if RTM BLE discovery cannot wake the Android receiver.
- Exercise Bluetooth-off, Wi-Fi-only, Wi-Fi Direct unavailable, app suspension, network loss, duplicate filenames, multiple files, rejected transfers, and malformed protocol messages.
- Treat AirDrop as a documented feasibility result, not an acceptance requirement.

## Assumptions and defaults

- “AWL” is interpreted as AWDL/OWL. The unrelated [`anywherelan/awl`](https://github.com/anywherelan/awl) mesh-VPN project is not part of the AirDrop implementation.
- “Windows-only app” means no new Android, iOS, or macOS companion application.
- Windows Phone is the primary device; desktop Windows is the principal reference peer.
- Full background discovery and perfect Quick Share visibility are stretch goals after foreground LAN interoperability is proven.
- If the existing toolchain cannot build an RTM-compatible UWP package, the first deliverable will be the protocol/compatibility proof and a clearly documented device-build blocker.

