LiveDrop 0.1.4.16 is a Quick Share receive interoperability hotfix.

- Reassembles post-acceptance BYTES payloads and consumes progress/control frames without treating them as files.
- Waits for the expected sharing frame during setup so an initial Android progress frame cannot shift the paired-key and introduction sequence.
- Detects sharing-layer cancellation and cleans up partial files.
- Ignores payload acknowledgements while waiting for DATA payloads.

The package is signed with the current RetiredLake store certificate. Physical Android and Windows peer testing remains required for final interoperability confirmation.
