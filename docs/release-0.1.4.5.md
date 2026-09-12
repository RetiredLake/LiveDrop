LiveDrop 0.1.4.5 is the Windows 10 Mobile picker, identity, and transfer-stability update for the ARM/x64 testing bundle.

Changes:

- Windows 10 Mobile uses `PickSingleFileAndContinue` continuation activation. Desktop Windows uses `PickSingleFileAsync`; the async picker API is not supported by the Windows Phone platform.
- Peer display names now use the host machine name while the product remains named LiveDrop.
- Quick Share filters local IPv4 addresses and loopback records so another LiveDrop process on the same machine is not presented as a remote peer.
- LiveDrop-to-LiveDrop Quick Share transfers now keep the receiver session open until the sender's encrypted disconnection frame arrives. Consent continuations run asynchronously, and incoming file cleanup is idempotent.
- Windows 10 Mobile reports Microsoft Nearby Share as unavailable and does not attempt to bind the PC Nearby ports. Google Quick Share remains available on Windows 10 Mobile.

Platform note:

- Microsoft documents Nearby Sharing for Windows 10 PCs on version 1803 or later. Windows 10 Mobile 1709 was the last Windows 10 Mobile release, so W10M never received the PC Nearby Sharing feature. The fixed TCP 5040/UDP 5050 listener is therefore skipped on W10M. Desktop builds continue to report a port conflict when the Windows Connected Devices service owns those ports.

Validation:

- ARM and x64 Release builds use the WpBlueBubbles VS 2019/SDK toolchain.
- The combined bundle is signed and verified with the RetiredLake certificate thumbprint `B90DCB33BC1AB90DBF79B8BBF34A2592D25315C0`.
- The package is installed locally before publication. Protocol smoke tests and package signature verification are run against the release artifacts.
- Real Android and Windows 10 Mobile hardware remains required for final cross-device transfer acceptance.

The release includes `RetiredLake.cer` so Windows 10 Mobile test devices can trust the package signer.
