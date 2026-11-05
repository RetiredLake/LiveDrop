# LiveDrop 0.1.8 development review

Development branch: `development/0.1.8`. Public release milestone: `0.2.0.0`.
Review packages stay under `builds/releases/Review-0.1.8.0`; no GitHub release.

## Changes

- Transparent app/tile/store/splash artwork based on the single-ring README logo. Vector source: `src/LiveDrop/Assets/LiveDropLogo.svg`.
- LAN startup status asks the recipient to use Everyone mode.
- Incoming connection names prefer endpoint-info metadata, as NearDrop does, instead of falling back when the legacy endpoint-name field is empty.
- Discovery refreshes only records referenced by received answers; unrelated DNS traffic no longer renews every cached peer. Zero-TTL records invalidate their cached address/service data.
- Discovery rejects loopback endpoints and merges entries by protocol identity or identical address/port. Different devices are not merged merely because their names match.
- Outbound connects time out after 15 seconds and accept cancellation. Reads, writes, and outgoing file reads propagate cancellation to WinRT operations.
- Uploads retain a single control reader, handling keep-alives, progress/control payloads, recipient cancellation, and safe-disconnect acknowledgement while file writes proceed.
- Files cannot be matched solely by size. Chunks cannot exceed announced file sizes before writing. Incomplete control payloads are bounded to eight payloads of at most 1 MiB each.
- Type 12 remains a recognized bandwidth-upgrade retry, matching NearDrop's checked-in protobuf schema. This was already handled; it is not a newly fixed protocol change.
- Nearby binding errors distinguish possible system restrictions and other listeners rather than implying the Nearby Sharing toggle controls the port.

## Focused validation

- Production mDNS codec tests: split replies, unrelated traffic, queries, empty packets, and address refreshes.
- Production Quick Share loopback fixture: handshake, endpoint metadata name, two different 700,000-byte files, exact saved bytes, and production loopback rejection. Loopback is enabled only by bypassing production adapters inside the fixture.
- UWP x64 Debug compilation and ARM Release/.NET Native packaging.
- Real Android/Windows interoperability and long transfers still need device testing. Loopback alone cannot establish that all client compatibility bugs are fixed.

## Portability and security review

Packet codecs, protobuf readers/writers, P-256, and most encryption logic remain ordinary C#. Platform boundaries still need work: sessions refer directly to SocketConnection and StorageFile, and models include ShareOperation. A future WP8.1 project should share the wire code and isolate framed transport, file access, discovery, and application lifecycle behind small interfaces. Avoid distributing OS-version conditions through the protocol state machine.

Existing safeguards include frame-size limits, HMAC verification before decryption, sequence validation, UKEY2 commitment checking, P-256 peer-point validation, sanitized destination names, and partial-file cleanup. This is a focused review, not a formal security audit. Managed BigInteger P-256 arithmetic is not established as constant-time. Incoming auto-accept remains existing behavior; logging a PIN does not itself enforce a human comparison. Concurrent incoming sessions are not globally bounded.

## Other transports

- On this PC, TCP 5040 is owned by `CDPSvc` (Connected Devices Platform Service); it is not an excluded TCP port. Turning off Nearby Sharing does not necessarily stop that broader service. W10M's failure cause has not been measured on the phone. Current LiveDrop explicitly disables its MS Nearby adapter on mobile.
- The current MS LAN adapter assumes TCP 5040 and UDP 5050. Picking an arbitrary port alone cannot establish interoperability: endpoint discovery/negotiation must carry it and the other client must honor it. CDP itself is transport-agnostic, so a blanket claim that every CDP transport requires TCP 5040 would be wrong.
- MS-CDP documents Bluetooth/BLE, LAN and Wi-Fi Direct. A Bluetooth implementation is feasible research, not an implemented alternative transport in this build. UWP RFCOMM sockets are available; interoperability still needs the correct CDP service, discovery and handshake.
- NearDrop currently documents LAN-only transfers and says Bluetooth needs more reverse engineering. LiveDrop's existing BLE publisher is a best-effort trigger, not a Bluetooth file transport or a BLE discovery scanner.
- NFC proximity sharing (MS-NFPB/MS-NFPS) is a separate protocol family. It should not be presented as a drop-in Google Quick Share or modern MS Nearby Sharing transport.
- AirDrop: Google's newer supported phones have AirDrop interoperability, and Apple's newer Wi-Fi Aware APIs broaden third-party possibilities. Neither adds AWDL or Wi-Fi Aware capabilities to W10M's UWP networking API. Native AirDrop on stock W10M remains unproven and impractical with the current app-only API surface; this is not proof of impossibility across all future UWP/bridge implementations.

References: [NearDrop](https://github.com/grishka/NearDrop), [protocol notes](https://github.com/grishka/NearDrop/blob/master/PROTOCOL.md), [MS-CDP transport](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cdp/3e6be5e8-e315-4f4b-97c7-ec0045d9bbb9), [UWP RFCOMM](https://learn.microsoft.com/en-us/windows/uwp/devices-sensors/send-or-receive-files-with-rfcomm), [Microsoft protocol families](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-wpo/a0825224-efdc-47dd-b6f6-300e428c39e7), [Google AirDrop interoperability](https://blog.google/security/android-quick-share-support-for-airdrop-security/), [Apple Wi-Fi Aware](https://developer.apple.com/documentation/WiFiAware).
