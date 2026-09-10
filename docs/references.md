# Reference and attribution ledger

LiveDrop uses public specifications and implementation behavior as references. No third-party source is vendored by the initial scaffold.

- Microsoft: [MS-CDP](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cdp/929c2238-6d49-4ba4-a36a-37e732c4f736), [MS-NFPS](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-nfps/421eed84-90a5-426d-8217-0a7f7667e9ee), and [Windows universal samples](https://github.com/microsoft/Windows-universal-samples).
- `nearby-sharing/android`: GPL-3.0. Its CDP/NearShare behavior is a porting reference; future copied or ported files must carry its notices.
- NearDrop: repository license and its [`PROTOCOL.md`](https://github.com/grishka/NearDrop/blob/master/PROTOCOL.md) are the primary Quick Share LAN reference.
- Packet, rquickshare, and pyquickshare: cross-platform interoperability references for discovery, payload framing, and current device behavior.
- Google UKEY2: Apache-2.0 reference for the handshake and cryptographic transcript.
- OWL, OpenDrop, opendrop-rs, and PrivateDrop: AirDrop feasibility and security references only. They are not LiveDrop dependencies and do not create an AirDrop feature requirement.

