# LiveDrop

<img width="256" height="256" alt="LiveDrop_white_transparent_2048" src="https://github.com/user-attachments/assets/b2a17170-5510-46a3-890b-482048eb8518" />

LiveDrop is an unofficial Windows 10/Mobile Google Quick Share client. It runs on Windows 10/Mobile RTM and newer (`10.0.10586.0+`).

## Usage

LiveDrop connects to most Quick Share clients, including unofficial ones. This means you can connect to Windows, Android, Chrome OS, macOS (NearDrop), and linux (unofficial clients)

Quick Share devices must be on the same network and should be set to "Everyone" mode for maximum reliability. Files can be shared in batches from inside the app or the share sheet. 

All incoming file types are supported and files are always saved to Pictures\LiveDrop\

LiveDrop does not support Apple Airdrop and does not support Microsoft Nearby Sharing on mobile.

## Install

#### App Stores
<p>
  <a href="https://apps.microsoft.com/detail/9PNQST6DJV6P">
    <img src="https://get.microsoft.com/images/en-us%20dark.svg" alt="Get it from Microsoft" height="48">
  </a>
  <a href="https://store.live.net.co/app/842">
    <img src="https://edge.live.net.co/images/store/2025_GetButton_SmallBlack.png" alt="Get LiveBubbles from Live Store" height="48">
  </a>
</p>

#### Sideloading
Open the [latest release](https://github.com/RetiredLake/LiveDrop/releases/tag/v0.1.5.6), install the .cer certificate (to local machine > trusted people on pc), and install the .appxbundle
LiveDrop uses the same dependencies and the same certificate as [LiveBubbles](https://github.com/RetiredLake/LiveBubbles)

## Build

LiveDrop uses the same LiveBubbles toolchain:

1. Visual Studio 2019 with Universal Windows Platform development.
2. Windows 10 SDK 10.0.19041.
3. The classic UWP C#/XAML targets and the pinned NuGet package source in `NuGet.config`.
4. `LiveDrop.sln`, building `Debug` or `Release` for `ARM`, `x86`, or `x64`.

`TargetDeviceFamily` is set to `10.0.10586.0`. ARM is the Windows 10 Mobile target. x86 and x64 provide PC validation builds.

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

## Disclaimer

Licensed under the GPL 3.0.

AirDrop, Google Quick Share and Microsoft Nearby Sharing are names belonging to their respective owners. This project is unofficial and is not affiliated with Apple, Google, or Microsoft
