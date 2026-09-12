LiveDrop 0.1.4.6 is the hostname and About dialog update for the ARM/x64 testing bundle.

Changes:

- Added `Change Hostname` above `About` in the More menu.
- Hostnames accept ASCII letters and numbers only, with a 1–32 character limit.
- The selected hostname is saved locally and discovery restarts immediately so Quick Share and Microsoft Nearby advertisements use it.
- Added the installed package version above the developer link in About.

Validation:

- ARM and x64 Release builds use the WpBlueBubbles VS 2019/SDK toolchain.
- The combined ARM/x64 bundle is signed and verified with RetiredLake certificate thumbprint `B90DCB33BC1AB90DBF79B8BBF34A2592D25315C0`.
- The package is installed locally before publication and protocol smoke tests are run against the source.
- Real Android and Windows 10 Mobile hardware remains required for final cross-device transfer acceptance.

The release includes `RetiredLake.cer` so Windows 10 Mobile test devices can trust the package signer.
