LiveDrop 0.1.4.7 is the Windows Phone file-picker and share-target activation update for the ARM/x64 testing bundle.

Changes:

- Renamed the regular-launch `Share` action to `Select file`.
- Windows 10 Mobile uses the asynchronous multiple-file picker broker used by WpBlueBubbles, starts at the Pictures location, and accepts common photo, video, and PDF types. The first selected file is used by LiveDrop.
- Desktop Windows continues to use `PickSingleFileAsync` with a broad file-type filter.
- Added the Pictures and Videos library capabilities used by the phone picker path.
- Share-target activation prepares the main page before window activation and then reports the incoming operation to that page.
- Expanded manifest share-target file types to match the picker and common Windows share-sheet content.

Validation:

- ARM and x64 Release builds use the WpBlueBubbles VS 2019/SDK toolchain and retain the 10.0.10586.0 minimum OS.
- The package is signed with the RetiredLake/LiveBubbles certificate thumbprint `B90DCB33BC1AB90DBF79B8BBF34A2592D25315C0`.
- The x64 package is installed locally and the regular launch exposes `Select file`.
- The installed package is registered as a Windows Share Target; physical Windows 10 Mobile picker behavior still requires the ARM package on a real phone.
