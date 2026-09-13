LiveDrop 0.1.4.17 is a Quick Share handshake and transfer safety hotfix for local testing.

- Reuses the production Quick Share session in a loopback smoke fixture and verifies a complete handshake, file receive, and byte-for-byte comparison.
- Rejects loopback and local-interface routes in Quick Share and Microsoft Nearby adapters, and rejects literal loopback addresses in the shared socket connector.
- Retains the 0.1.4.16 handling for Android BYTES progress/control payloads and expected setup-frame ordering.

The package is signed with the current RetiredLake store certificate. This build is for local validation only; physical Android and Windows peer testing remains required before publishing a GitHub release.
