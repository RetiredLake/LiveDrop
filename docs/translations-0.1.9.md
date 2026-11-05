# LiveDrop 0.1.9 translation draft

Based on development/0.1.8 commit 79f34f6bf83f0fce4a60223b27c4a78bcc24b0c9
(0.1.8.12). No package version, update channel, signing identity, or protocol
behavior is changed by this translation commit.

## Languages

German (de-DE), Italian (it-IT), Spanish (es), Hindi (hi-IN), Simplified
Chinese (zh-Hans), Czech (cs-CZ), Japanese (ja-JP), Brazilian Portuguese
(pt-BR), Indonesian (id-ID), Polish (pl-PL), French (fr-FR), Urdu (ur-PK),
Russian (ru-RU), Ukrainian (uk-UA), Arabic (ar), Hungarian (hu-HU), and
Vietnamese (vi-VN).

These are AI-generated translation drafts, not native-speaker-reviewed releases.
Chinese is Simplified Chinese UI text, not a separate spoken Mandarin locale.
Spanish uses a language-wide resource folder rather than a country-specific one.

## Coverage and fallback

Each language contains 135 of the 267 resource keys. These cover the principal
interface, sharing consent, sending/receiving, completion notifications, common
errors, updates, privacy labels, and hostname entry. Brand labels and the byte
unit are intentionally unchanged. Because several English keys share the same
value, they receive the same translation.

The other 132 keys intentionally fall back to en-US. They are mostly detailed
CDP, protobuf, crypto, transport, and protocol diagnostics, with some startup
and validation messages. This is not a claim of complete string coverage.
Do not copy English values into each language to hide incomplete coverage.
The exact translated/fallback key sets are recorded in
tests/Localization/translation_coverage.json. The validator requires those sets
exactly, so an accidental omission, extra key, or new untranslated English
resource cannot silently pass.

Windows selects resources from its preferred-language list with en-US fallback.
The existing PRIResource glob and x-generate manifests include the new folders;
there is no country-based language switch or additional telemetry.

Count wording in Czech, Polish, Russian, Ukrainian, and Arabic uses count labels
where appropriate to avoid pretending that one plural form covers every count.
Do not change numbered placeholders or format specifiers such as {1:P0}.
Quick Share Everyone guidance includes the English mode name in parentheses to
help users match it on another device.

## Validation and remaining release checks

Run `python tests/Localization/validate_resources.py` from the repository root.
It checks duplicate keys, comments, source references, XAML/manifest references,
the language/coverage contract, and placeholder names and format specifiers.

Before shipping, build ARM/x86/x64 packages with the Windows UWP toolchain and
check the generated PRI language candidates and en-US per-key fallback. Test
on the minimum supported Windows 10 Mobile build and a current desktop build.
This Linux editing environment cannot build or run UWP, so neither packaging
nor device rendering has been verified here.

Check long strings, font availability, language switching, hostname entry,
share-target activation, accept/deny PIN dialogs, updates, and modern/legacy
notifications. Arabic and Urdu need explicit right-to-left layout and mixed
Latin device-name/PIN/URL testing. Resource text alone is not proof that the
current page layout and notification templates mirror correctly. Native
wording review remains useful but is not required from community volunteers.
