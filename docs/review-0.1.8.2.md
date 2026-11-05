# LiveDrop 0.1.8.2 review

## Browser link sharing

Both package manifests register WebLink and legacy Uri share formats. The share target reads browser WebLink data before text, including older Uri shares, and uses a native Quick Share TextMetadata URL introduction followed by a BYTES payload. The wire field layout follows NearDrop's wire_format.proto and OutboundNearbyConnection.swift. URLs provided as plain text are recognized too. Only HTTP/HTTPS links are accepted as web links.

LiveDrop receivers accept bounded text/URL payloads. They preserve received URLs as text in Pictures/LiveDrop without automatically launching a browser. WinRT storage rejects Internet Shortcut (.url) files, so receiving links must not attempt that extension. Android/official clients receive a URL-typed attachment rather than an outgoing .txt file. Native link sending currently requires Google Quick Share; the Microsoft adapter still sends files.

## Correct LAN advertisement

On the development PC, 172.20.112.1 belongs to vEthernet (Default Switch), whereas the physical Ethernet address is 192.168.86.2. The former is a valid private address, not loopback; it is nevertheless the wrong network for the phone.

Raw mDNS previously announced every IPv4 interface. Both the raw publisher and DNS-SD fallback now select the primary connected LAN adapter. Offline connected LAN profiles remain usable, while cellular WWAN profiles and the Hyper-V fallback profile are excluded. Discovery caches are separate per receiving interface so records from different networks do not combine. No 192.168 address or 172-range blacklist is hardcoded.

## Duplicate rows

One device row now represents an IP across both protocol adapters, preferring Google Quick Share when both find it. A Microsoft CDP discovery on the same IP therefore cannot add a second Windows Phone row. Traffic from that secondary protocol does not refresh a stale Quick Share endpoint. Existing same-IP ID/port replacement and ten-second expiry remain in place.

## Validation and remaining limits

- The live LAN-selector fixture returns only 192.168.86.2 on this PC.
- The production session fixture transfers two different equal-sized files and a browser WebLink through native Quick Share URL metadata/payloads, verifying exact stored content and production loopback rejection.
- Peer identity checks cover changed ID/port and same-IP discoveries across protocols. mDNS regression checks cover split replies and stale endpoint handling.
- Added receiver handshake-stage messages before key generation to distinguish TCP acceptance, initial request parsing, and UKEY2 preparation.
- A desktop session fixture does not establish successful transfers on two ARM phones. The older reported 0.1.7.x WP-to-WP failure needs a device retest with both peers updated, using the new stage messages if it remains reproducible. The Hyper-V correction explains the development PC address failure, not that separate historical phone-to-phone report.

## Discovery and transfer sequence

The receiver listens on TCP and advertises a PTR service record, SRV host/port, TXT endpoint name, and A address. These may arrive in different packets; the discovery implementation must join them by service/hostname on the correct interface. Name visibility alone does not prove reachability. The sender connects to the advertised endpoint, completes UKEY2 and encrypted connection setup, sends content metadata, obtains acceptance, then sends file chunks or URL bytes and closes through the negotiated teardown flow.

References: [NearDrop protocol](https://github.com/grishka/NearDrop/blob/master/PROTOCOL.md), [Windows share formats](https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/dataformat-reference), [DNS-SD adapter registration](https://learn.microsoft.com/en-us/uwp/api/windows.networking.servicediscovery.dnssd.dnssdserviceinstance.registerstreamsocketlistenerasync).
