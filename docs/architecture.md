# LiveDrop architecture

LiveDrop follows the classic WpBlueBubbles UWP layout so the same package can be built for ARM Windows 10 Mobile and x86/x64 Windows PCs.

```text
Share Target / MainPage
          |
    ShareCoordinator
      /           \
Microsoft CDP    Google Quick Share
 adapter         adapter
      |               |
 UDP 5050        mDNS 5353 + TCP
 TCP 5040        UKEY2 + Nearby frames
```

`PeerDescriptor`, `ShareOffer`, and `IShareProtocolAdapter` are the stable boundary. A transport adapter owns discovery and socket setup; protocol sessions own authentication, consent, framing, payload flow, cancellation, and progress. This prevents the Windows CDP binary header from being mixed with Quick Share's protobuf frames.

## Windows RTM constraints

- `TargetPlatformMinVersion` and the package minimum are `10.0.10586.0`.
- `DatagramSocket` is used for multicast DNS and CDP presence rather than `DnssdServiceWatcher`, which is optional and unsupported on the target floor.
- The app exposes a desktop build because a desktop Windows peer is the principal reference device.
- Newer APIs must be guarded with API contract checks or reflection; the baseline code uses RTM-available networking and storage APIs.

## Quick Share LAN path

The receiver publishes `_FC9F5ED42C8A._tcp.local.`. The service instance contains the PCP byte, a four-character endpoint ID, the Quick Share service hash, and two reserved bytes. TXT `n=` carries endpoint information. TCP frames use a four-byte big-endian length prefix. The adapter implements the UKEY2 P-256 exchange, encrypted `OfflineFrame`/`sharing.nearby.Frame` state machine, consent, file send, and atomic receive paths. Android/NearDrop interop remains an acceptance test.

## Microsoft Nearby path

The discovery adapter follows the current CDP v3 common header, UDP 5050 presence request/response, and TCP 5040 endpoint convention used by `nearby-sharing/android`. The adapter implements the CDP connection header, P-256 certificate authentication, encrypted channel, NearShare handshake, range-based file send, consent, and atomic receive paths. Windows PC interop remains an acceptance test.

## File safety

Incoming names pass through `ProtocolUtilities.NormalizeFileName`. Completed files are written to a generated temporary file and renamed only after the stream flushes. SHA-256 verification is available in `TransferFileStore` and is part of the transfer-session acceptance tests.
