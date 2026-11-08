# LiveDrop8

Universal 8.1 C#/XAML solution for Windows 8.1 and Windows Phone 8.1 (WinRT AppX, not Silverlight). Open `LiveDrop8.sln` in Visual Studio 2015 with the 8.1 tools installed.

## Current scope

Development build with LAN discovery, multi-file sending/receiving, PIN consent, queued incoming offers, progress, cancellation, and received-file viewing connected to the existing protocol engine. No automatic updater is included. Native transfer notifications are not yet ported.

The shared page uses a small LIVEDROP heading, a larger discovery heading, large bordered peer rows and a native bottom command bar. Desktop centers the same layout with a maximum content width. The command selects files, sends selected files, or cancels an active transfer. Peers expire after ten seconds; the UWP identity/address deduplication and local-address guards are retained.

Shared settings cover supported protocols and broadcast name; changing either restarts discovery. Google Quick Share is enabled by default. The About dialog links to GitHub and the privacy policy. Existing UWP resource files are included. New shell labels still need resource mapping before release.

Windows Phone uses picker continuation activation, including after process termination. Both manifests register a share target for arbitrary files, text, and links. The LiveDrop Folder command lists saved files in-app and opens a selected file, since 8.1 lacks the UWP folder-launch API. Received files use the same Pictures/LiveDrop destination and app-storage fallback as UWP. Protocol-declared web links open after a successful accepted transfer; ordinary text files remain files.

Protocols, framing, cryptography, storage and transport code are linked from `src/LiveDrop`, rather than maintained as a second protocol implementation. Small source compatibility changes retain the UWP path. The 8.1 host supplies Windows cryptography adapters and the address parser absent from its .NET profile. LAN/mDNS is available; the UWP BLE advertising and DNS-SD registration APIs are unavailable. Microsoft Nearby remains subject to its existing TCP 5040 restrictions; no alternative wire protocol or port was introduced.

## Build

From the repository root, using MSBuild 14:

```powershell
& .\src\LiveDrop8\build.ps1
```

Produces signed phone ARM, phone emulator x86, and desktop x86 AppX packages in `builds/LiveDrop8/<manifest-version>`, along with the existing public certificate. Requires the existing signing private key in CurrentUser/My. No upload/publishing is performed.

For Windows Phone Application Deployment (8.1), use the unsigned developer packages in the `SDKDeployment` subfolder: ARM for a physical phone, x86 for the emulator. The legacy deployment tool rejects the signed phone packages during its local packaging check. Desktop installation still uses the signed desktop package in the parent directory.

## Validation and remaining device checks

Signed release builds pass for phone ARM and desktop x86. The Windows cryptography adapter passes SHA-256, SHA-512, HMAC-SHA256, AES-CBC and AES-ECB known vectors. Existing P-256/Quick Share/CDP identity vectors and isolated Quick Share multi-file/link transfer and bidirectional cancellation fixtures pass with the 8.1 adapters and 8.1 metadata. Loopback remains restricted to test fixtures.

The desktop package installs and starts on the development PC. Actual Windows 8.1/WP8.1 hardware transfers, UI appearance, picker continuation across termination, LAN discovery with other mDNS listeners, and official-client interoperability still require device testing. A W10M phone can check compatibility, but does not establish behavior on actual WP8.1.

## Discovery transport

The 8.1 host uses supported Winsock calls via a small C# adapter to share UDP 5353 with SO_REUSEADDR, select the LAN multicast interface, and join 224.0.0.251. The UWP host retains DatagramSocket with MulticastOnly. Both use the same mDNS codec, cache and peer filters. Native sockets remain inside the app's normal networking permissions; no firewall/service changes are made.

A bounded local `LocalState/discovery.log` records startup errors (including the original socket error) and discovered peers; it is never uploaded. DNS-SD fallback registration remains unavailable on 8.1, so failures report the actual multicast cause.

`Legacy8DiscoverySmoke.cs`, compiled with LIVEDROP8 and the 8.1 metadata, exercises two multicast listeners sharing UDP 5353 alongside existing host listeners and checks announcement decoding and repeated teardown/restart.

## Current testing build (0.1.0.6)

Peer borders and text stay white. Unselected peers have black interiors; selected peers use the system accent color inside the border, without a separate check mark. Desktop settings remain in the left command group. Phone uses an app-owned dark gray menu above a lighter command strip, with 48px menu rows and a circular action button. This avoids native CommandBar popup color/padding behavior. Hardware Back closes the menu. The completed receive exposes a Done toolbar action to dismiss the notice without an empty command row.

Telemetry is linked from the existing UWP service. It reports installation, application-open and transfer started/succeeded/failed/canceled events to the same PostHog project. `app_variant=winrt81` distinguishes this project from `uwp`; `language` is the app language tag. Payloads retain protocol, direction, file count and size bucket, add file/link type, and omit names, paths, peer addresses and PINs. Reporting and offline queue errors remain silent. No updater events are emitted because LiveDrop8 has no updater.

ARM/x86 phone and x86 desktop packages have been built. The desktop installation starts discovery and finds peers. Phone appearance needs device verification. Full UI localization and native transfer notifications remain pending.
