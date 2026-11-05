# Localization plan for 0.1.9.0

There is no strings.txt or .resw translation file in the current project. Text is hardcoded in MainPage.xaml, MainPage.xaml.cs, Services/TransferNotification.cs, Services/GitHubUpdateService.cs, and protocol status/error messages. This document is a plan; 0.1.8.4 does not introduce a translation system.

## Resource layout

- Add src/LiveDrop/Strings/en-US/Resources.resw as the English source of truth. Add one matching Resources.resw under each translated language folder.
- Use stable semantic keys, for example HeaderTitle.Text, SelectFile.Content, TransferSending, TransferComplete, FileOpenFailed, and DiscoveryEveryoneHint. Use x:Uid for XAML properties and one small app-layer resource helper for C#.
- There is no need for a language menu: use the Windows preferred language list and English fallback. Preserve the package identity, app name, certificate and update path.
- Keep protocol codecs and transport state machines portable. Pass structured status codes and formatting arguments to the app layer rather than adding Windows ResourceLoader calls throughout protocol code. Raw diagnostic details can remain separate from translated user-facing messages.

## Translator guidance

Provide a translation guide with resource comments, UI screenshots, placeholder meanings, and approximate space constraints. Preserve placeholders such as {0}, protocol identifiers, PINs, URLs, file extensions, and product names. Use format strings rather than concatenating sentence fragments. Handle singular/plural file counts explicitly and format byte sizes, percentages and numbers with the user's culture.

Do not ask translators to edit packet schemas, service IDs, API errors, notification activation tokens, telemetry event identifiers, or code. Keep the About privacy link destination unchanged while translating its visible label.

## Delivery order

1. Inventory user-visible strings and extract an English-only .resw without changing behavior.
2. Convert XAML labels, dialogs and About text, then notifications and ordinary status/error messages.
3. Give protocol status reporting the small app-rendering boundary described above.
4. Add translator documentation and accept translations by matching resource keys. Initial language selection can follow volunteer contributions rather than inventing incomplete translations.
5. Check missing/extra keys and format placeholders during builds. Validate long text and right-to-left layouts on PC and a W10M-sized viewport, plus share-target activation, picker errors, About, progress notifications, and saved-file activation.
6. Validate the English fallback and a non-English package on the minimum supported W10M build. Preserve the existing native-progress API guards and old-client notification fallback.

WP8.1 can reuse the .resw translations in its future app project, with a platform-specific resource accessor. No protocol strings.txt parser is required.
