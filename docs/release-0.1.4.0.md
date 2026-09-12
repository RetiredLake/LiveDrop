LiveDrop 0.1.4.0 is the UI and file-selection pass for the ARM/x64 testing bundle.

Changes:

- Regular launches now start with a `Share` action that opens the system file picker, including Photos and File Explorer locations.
- The selected file name is shown above the enabled `Send selected share` action.
- Regular launches include `Cancel`, which clears the selected file and returns to the starting state.
- Windows Share Target launches keep their existing share operation and hide the management menu and regular-launch picker controls.
- W10M picker continuation activation is included for the RTM target; desktop builds use the asynchronous picker API.
- A fresh regular or share-target session clears stale pending-share state.

Validation:

- ARM and x64 Release builds completed with the WpBlueBubbles VS 2019/SDK toolchain.
- The combined ARM/x64 bundle is signed and verified with certificate thumbprint `B90DCB33BC1AB90DBF79B8BBF34A2592D25315C0`.
- The signed bundle was installed locally and the regular launch state showed `Share`; invoking it opened the Windows file picker and selecting a file populated the file name and send/cancel controls.
- Same-PC loopback sending remains a transport test limitation; cross-device Quick Share and Nearby Share pairing require device testing.

The release includes `RetiredLake.cer` so Windows 10 Mobile test devices can trust the package signer.
