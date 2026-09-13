LiveDrop 0.1.4.12 is a Quick Share UKEY2 interoperability hotfix.

- Sends the plaintext connection response before reading the peer response, preventing the two LiveDrop endpoints from waiting on each other.
- Retains the corrected control-payload framing, endpoint discovery metadata, and P-256 public-key encoding from 0.1.4.11.
- Keeps automatic incoming acceptance, visible received-file storage, progress reporting, and clean final-file shutdown.
