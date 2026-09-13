LiveDrop 0.1.5.0 is the sending and receiving interoperability hotfix.

- Received PDFs, images, videos, and other file types use one user-facing destination: `Public/Downloads/LiveDrop`.
- Outgoing Quick Share and Microsoft Nearby file reads use partial sequential/ranged streams, avoiding the Windows `A method was called at an unexpected time` failure from exact-load reads.
- Quick Share tolerates valid encrypted bandwidth-upgrade control frames, answers keep-alive requests, and keeps interleaved control payloads independent while reassembling them.
- Quick Share reports the derived security PIN so it can be compared with the other device when the peer requests verification.

The Public Downloads path is preferred. If Windows denies access to it, LiveDrop falls back to the current user's Downloads folder, then to the app's private Downloads/LiveDrop folder so a received transfer is not lost.

Physical Android and Windows peer testing remains recommended for protocol coverage; the signed ARM and x64 packages have passed the local transfer and packaging checks.
