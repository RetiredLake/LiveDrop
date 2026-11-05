# Localization plan for 0.1.9.0

The English resource foundation is implemented in 0.1.8.8. Edit `src/LiveDrop/Strings/en-US/Resources.resw` in Visual Studio's resource editor (Name, Value, Comment), rather than a strings.txt file. The catalog covers XAML, accessibility labels, dialogs, About, notifications, package metadata, discovery/status messages and app-authored errors. The file selection button now reads **Select Files**. Translations are planned for 0.1.9.0; no translated languages are advertised yet.

## Implementation and translation contract

- XAML uses `x:Uid` and property resource names. Attached accessibility properties use Microsoft's `[using:Windows.UI.Xaml.Automation]AutomationProperties.Name` syntax. Package metadata uses `ms-resource:` references.
- `AppText` is a small platform-neutral formatting/lookup boundary. The UWP host supplies `ResourceLoader.GetForViewIndependentUse()` before initializing UI or services, so background discovery/transfer callbacks do not require a CoreWindow. Windows selects resources from the user's preferred languages, falling back to the project's en-US default.
- Full sentences use numbered placeholders, with culture-aware display formatting. The linked developer credit is one format resource so translators can reposition the name. Singular file readiness has its own resource. Add additional plural forms where the target language requires them before shipping that translation.
- Transfer outcome tracking uses completion callbacks, not English status-message substring matching. Wire bytes, crypto labels, service identifiers, filenames, activation tokens and analytics event/property identifiers are not translated. External OS exception details remain supplied by Windows.
- Preserve resource keys after translations begin. Translate values, read comments, preserve placeholders and format specifiers, and keep brand/protocol names unchanged. Do not create English copies in other language folders and claim those languages are translated.
- Run `python tests/Localization/validate_resources.py` before packaging. It checks reference resolution, duplicate keys, XAML/manifest resources and translation placeholder consistency. Test long strings, right-to-left layout, English fallback and both modern and legacy notifications on devices before publishing translations.

## Planned language sets

Keep English (`en-US`) as the default. The discussed translation candidates are German (`de-DE`), Italian (`it-IT`), Spanish (`es`), Czech (`cs-CZ`), Japanese (`ja-JP`), Brazilian Portuguese (`pt-BR`), Indonesian (`id-ID`), Polish (`pl-PL`), French (`fr-FR`), Russian (`ru-RU`), Ukrainian (`uk-UA`), Arabic (`ar`), Urdu (`ur-PK`), Hungarian (`hu-HU`), Vietnamese (`vi-VN`), Hindi (`hi-IN`) and Chinese in Simplified script (`zh-Hans`). Mandarin is spoken; Chinese UI resources must specify script. Traditional Chinese (`zh-Hant`) would be a separate translation/review decision. Country data does not establish preferred language or guarantee complete user coverage.

Microsoft reference: https://learn.microsoft.com/en-us/windows/uwp/app-resources/localize-strings-ui-manifest

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
