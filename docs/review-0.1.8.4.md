# LiveDrop 0.1.8.4

The visible main-screen header reads LiveDrop 2. Package identity, app display name, executable, signing certificate and update identity remain unchanged.

## Transfer notifications

Each outgoing/incoming transfer has its own notification tag. On Windows build 15063 and newer, progress uses NotificationData bindings and rate-limited native progress updates. On older supported builds, the existing ToastText02 start/completion notification style remains in use. Notification APIs added after RTM are isolated behind API-presence checks. Completion replaces the progress entry with Share complete; cancellation/failure also terminates it. Notification errors never propagate into transfer code.

Protocols report the final local saved-file path only after a successful rename. Completion notifications store an opaque activation token in a bounded app-settings map. Tapping a single-file receipt calls Launcher.LaunchFileAsync; a batch opens the containing folder. Both toast and legacy launch activation are handled, including after app restart. A click never supplies an arbitrary filesystem path; the app looks up a locally registered token. Incoming content is not launched automatically. Windows still needs a suitable handler for the received file type.

Progress and completion reporting were connected for both adapters without changing their wire formats. The Quick Share fixture verifies that saved-file targets exist after transfer finalization, including native URL receipts. Desktop tests validate the legacy XML fallback, native progress bindings, filename escaping and completion activation payloads. Actual W10M toast rendering and handler availability still require device testing.

## Device names

Discovery previously published SRV/A endpoint records before TXT name metadata arrived. The UI then displayed a fallback label via ToString, which did not notify the ListView when the model's name changed. Discovery now waits for endpoint TXT metadata and requires a name for LiveDrop's own announcements. The row uses a bindable DisplayText property with change notification. Other clients can still use the Android device fallback, as requested. Malformed partial name data no longer replaces a previously valid name with a generic label.

Regression checks cover separate A/SRV/TXT replies and visible label change notifications. Existing stale-address, duplicate identity, file/link transfer and lifecycle tests remain available.

## Release series and localization

The original signed 0.1.8.0–0.1.8.3 bundles are archived development previews. Those intermediate per-version source snapshots were not committed at build time; their archive tags record artifact metadata and explicitly describe this limitation. The v0.1.8.4 tag contains the complete current development source.

All five versions are development prereleases ahead of the planned 0.2.0.0 stable milestone. GitHub's latest stable endpoint and the current stable updater channel therefore remain on the existing stable release.

There is no strings.txt in the current project. See localization-0.1.9.0.md for the proposed Resources.resw layout, English fallback, translator workflow and testing plan. No localization system was implemented in this build.
