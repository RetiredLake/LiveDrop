# LiveDrop

LiveDrop is a Windows 10 Mobile first nearby sharing client. Its target is Windows 10 Mobile RTM era UWP (`10.0.10586.0`) with desktop Windows builds used as the reference test peer.

The first milestone has two independent protocol adapters:

- Microsoft Nearby Share compatibility for Windows PCs, based on the Connected Devices Platform discovery and session specifications.
- Google Quick Share compatibility for Android, beginning with the documented LAN mDNS path and the UKEY2/ Nearby Connections session.

AirDrop is outside this project milestone. No AirDrop code or compatibility promise belongs in LiveDrop.

## Current execution state

The repository currently contains the RTM-floor UWP shell, Windows Share Target activation, peer and offer models, safe file-storage primitives, Microsoft CDP v3 discovery plus authenticated NearShare send/receive sessions, and Quick Share mDNS discovery plus UKEY2/encrypted send/receive sessions. Real Windows 10 Mobile, Windows PC, and Android peers still need to validate the wire paths before an RTM build can be called interoperable.

## Build

Use the WpBlueBubbles toolchain:

1. Visual Studio 2019 with Universal Windows Platform development.
2. Windows 10 SDK 10.0.19041.
3. The classic UWP C#/XAML targets and the pinned NuGet package source in `NuGet.config`.
4. `LiveDrop.sln`, building `Debug` or `Release` for `ARM`, `x86`, or `x64`.

The package manifest keeps `TargetDeviceFamily` at `10.0.10586.0`. ARM is the Windows 10 Mobile target. x86 and x64 provide PC validation builds.

## Protocol references

The adapter design and wire comments are based on these public projects and specifications:

- [Microsoft MS-CDP](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cdp/929c2238-6d49-4ba4-a36a-37e732c4f736)
- [Microsoft MS-NFPS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-nfps/421eed84-90a5-426d-8217-0a7f7667e9ee)
- [nearby-sharing/android](https://github.com/nearby-sharing/android)
- [NearDrop protocol notes](https://github.com/grishka/NearDrop/blob/master/PROTOCOL.md)
- [NearDrop implementation](https://github.com/grishka/NearDrop)
- [Packet](https://github.com/nozwock/packet)
- [rquickshare](https://github.com/Martichou/rquickshare)
- [pyquickshare](https://github.com/teaishealthy/pyquickshare)
- [Google UKEY2](https://chromium.googlesource.com/external/github.com/google/ukey2/)
- [Microsoft UWP networking samples](https://github.com/microsoft/Windows-universal-samples/tree/main/Samples/StreamSocket)

The practical behavior from GPL-3.0 protocol projects is being ported behind LiveDrop interfaces rather than copying modern .NET or platform-specific application code into the RTM UWP project. Any future ported source must retain its upstream notices and GPL obligations.

The checked-in protocol smoke test covers P-256, Quick Share encryption, CDP certificate authentication, CDP NearShare ValueSets, and CDP packet encryption. The local source-level UWP compile uses the cached WpBlueBubbles UWP reference assemblies when the machine lacks the classic XAML build targets.

## Testing boundary

Testing artifacts may be produced for Windows PC and real Windows 10 Mobile hardware. The acceptance matrix begins with same-LAN foreground transfer, then adds BLE-triggered discovery, Wi-Fi Direct, suspension, network loss, duplicate names, cancellation, malformed messages, and large files. Real Lumia hardware is required for the RTM claim; an emulator is not treated as evidence.

Version 0.1.3.2 is the second-pass testing release with an ARM/x64 app bundle and in-app update checking. Quick Share peer discovery has been observed on the development PC; cross-device transfers and W10M RTM behavior remain unverified. On desktop Windows, the built-in Connected Devices service can occupy TCP 5040/UDP 5050 and prevent LiveDrop's Microsoft Nearby listener from starting; the app reports this alongside Quick Share status.

The second-pass local review build makes the supported protocols selectable, including an all-off state, and saves the selection. Peer rows show friendly protocol labels only while both protocols are selected. Share-target views own their share operations, and discovery callbacks tolerate a closed view. See [second-pass validation](docs/second-pass-validation.md).
