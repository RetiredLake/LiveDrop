# 0.2.0.0 packaging validation

GitHub: signed native ARM, x86 and x64 applications in one AppX bundle, version 0.2.0.0, identity RetiredLake.LiveDrop and the existing publisher. Minimum OS remains 10.0.10586.0. Bundle signature and certificate SHA-256 were checked; the certificate remains c7b4e29835a263cbc71c39a9eb4ff27449fcef2c678e367c2c6bf9ee5cf9a29d.

Store: built with StoreBuild=true and UapAppxPackageBuildMode=StoreUpload. The appxupload contains ARM/x86/x64 managed application payloads and symbols. Managed PE headers were inspected to reject precompiled native upload payloads. The MainPage constructor was disassembled for all three architectures and checked to omit CheckForUpdate while retaining the folder/settings entries. The updater implementation remains in the application. Store manifest revision is zero. The upload is prepared locally, not submitted to Microsoft.

Current-source protocol fixtures pass: handshake, multiple exact-byte file transfers, native links versus URL-looking text files, sender/receiver cancellation and partial-file cleanup, production loopback guard, and notification activation XML. Resource/translation validation passes. UWP 0.2.0.0 was installed locally in place as a bundle during desktop testing.

LiveDrop8 0.1.0.6 is a separate testing build. Selection keeps a white border/text and black idle interior, with a theme-accent interior when selected. The phone's app-owned menu is #2B2B2B above a #3B3B3B action strip, with fixed menu rows, circular primary action and ellipsis. This replaces the native CommandBar on phone to avoid its reserved empty space and popup styling limitations. Hardware Back closes the menu; consent and transfer state continue to govern actions. Desktop retains the native command bar and left management commands.

Both LiveDrop8 phone architectures and desktop compile; its desktop installation starts discovery and finds LAN peers. The phone layout still needs visual verification on the user's device. Mobile UWP picker continuation/fallback also needs a device test. No claim is made that desktop tests validate those phone-specific UI paths.
