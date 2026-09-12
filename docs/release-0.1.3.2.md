LiveDrop 0.1.3.2 is the second-pass ARM/x64 testing release.

Changes:

- Added selectable Microsoft Nearby Share and Google Quick Share checkboxes, including an all-off state, with selections saved locally.
- Friendly Quick Share peer labels now read “- Quick Share” when both protocols are enabled; the suffix is omitted when only one protocol is selected.
- Fixed the share-target crash caused by discovery callbacks using a disposed XAML view after the share window closed.
- Added guarded share activation and shared-data error handling.
- Made adapter shutdown independent of a closing UI message pump.
- Filtered LiveDrop's own Quick Share endpoints from peer results.

Validation:

- Release ARM and x64 builds completed with the WpBlueBubbles VS 2019 toolchain.
- Combined bundle signature verification passed with certificate thumbprint `B90DCB33BC1AB90DBF79B8BBF34A2592D25315C0`.
- Real Windows share-sheet activation, Unicode shared text, unavailable shared data, view closure, and app reopening were exercised locally.
- Protocol-selection, shutdown, and mDNS regression tests passed.

Known limitations:

- Cross-device file transfer and Windows 10 Mobile RTM hardware behavior remain unverified.
- Windows Connected Devices may already own TCP 5040/UDP 5050, preventing LiveDrop's Microsoft Nearby listener from starting on desktop Windows.
