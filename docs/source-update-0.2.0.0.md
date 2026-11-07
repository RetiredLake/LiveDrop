# Pending source update

UWP GitHub and Store manifests are set to 0.2.0.0. This update is source-only: no app builds, release tags, GitHub releases, or Store submissions were created.

- Regenerate transparent UWP tile/list/Store icons from the existing vector, including 200% and 400% assets.
- Remove the leading margin on View Files.
- Use the LiveDrop8 phone continuation picker flow for mobile UWP, with list view and all-file support. If the broker rejects that obsolete API as unsupported, fall back to the async picker starting in Documents. Desktop keeps the async thumbnail picker. Activation restores the file selection, cancellation clears it, and Send is disabled while selection is pending.
- Add the Windows 8.1 / Windows Phone 8.1 project in `src/LiveDrop8`, sharing the protocol implementation and adapting crypto, address parsing and multicast port sharing to 8.1. Last tested port build was 0.1.0.4; current source is 0.1.0.5.
- Use explicit white selected-peer text/outline, with gray unselected peers, without the stock accent fill or checkmark. Return desktop management commands to the left group. Use a consistent charcoal phone command bar, compact menu rows, smaller About content and a Done command after receiving.
- Share the existing best-effort PostHog batch implementation with LiveDrop8. Keep installation/open/share outcome events; LiveDrop8 emits no updater events. Add `language`, `app_variant` (`uwp` or `winrt81`) and file/link content type. Capture timestamps and stable queued-event UUIDs preserve event identity across retries. No filenames, file paths, device names, addresses or PINs are added to telemetry.

Source validation: resource references/placeholders, XAML/project/manifest XML, version consistency, and whitespace checks. No compilation, device tests or telemetry traffic were performed for these pending UI/picker/analytics changes. Earlier port transport tests do not validate this source update.

Picker continuation/fallback behavior and the revised native phone menu need verification on W10M and WP8.1 respectively before packaging. Full LiveDrop8 UI localization and native transfer notifications remain pending.
