# Second-pass local review

Local review version: 0.1.3.2. This version is published as the GitHub testing release after local review.

## Crash evidence and correction

The installed 0.1.2.0 app saved `InvalidComObjectException` in `LocalState/startup-error.txt`. Windows Application events 1000/1001 recorded repeated failures. Resolving the native release offsets against its PDB identified `MainPage.OnPeerDiscovered.MoveNext` (offset 0x21f655). That callback queried a disposed XAML page's dispatcher after a share-target view closed.

The page now captures its dispatcher while valid, detaches adapter events before shutdown, rejects queued callbacks belonging to an old coordinator, and guards COM teardown failures. Window closure cancels the share operation and discovery. Discovery loops and coordinator shutdown do not depend on the closing view's message pump. Share operations belong to the activated page, and failures reading shared content are handled rather than escaping `async void`.

## Verified behavior

- Both selected: the observed peer reads `Nico's PC - Quick Share`.
- Quick Share only: it reads `Nico's PC`, without a protocol suffix.
- Neither selected: no peers and no UDP sockets owned by LiveDrop.
- Nearby only: only the existing TCP 5040 conflict is reported on this PC.
- Selection survives a cold launch.
- A real Windows share-sheet activation supplies Unicode text; selecting the discovered peer enables Send. No test data was sent to a network peer.
- Closing that share window leaves the main app alive; closing and reopening the main window works. The original crash-log timestamp remains unchanged.
- A deferred share provider that supplies no content produces a handled read error, without terminating the main app.
- Concurrent main and share-target views do not list each other as Quick Share peers.
- The production coordinator passes tests for all four selections and asynchronous shutdown without a functioning UI synchronization context. DNS regression tests pass.

ARM and x64 use the WpBlueBubbles VS 2019/SDK toolchain and retain the 10.0.10586.0 minimum OS. The bundle is signed with LiveBubbles certificate thumbprint `B90DCB33BC1AB90DBF79B8BBF34A2592D25315C0`. The local update preserves existing app data.

These checks validate the second-pass UI and reported desktop share-window crash. They do not establish Android/Windows file-transfer interoperability or actual W10M RTM hardware behavior.
