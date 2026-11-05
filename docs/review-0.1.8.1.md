# LiveDrop 0.1.8.1 review

- Restored the previous Microsoft Nearby Share startup error wording.
- Peer entries and their expiry keys now use protocol plus IP, regardless of advertised endpoint ID or port. Repeated discoveries replace the endpoint in place and remove existing duplicate rows and expiry records. Protocol choices remain independent.
- mDNS resolution chooses the most recently advertised service endpoint for an IP before publishing updates. Address replies for older cached hostnames cannot replace that endpoint. Cached service endpoints older than ten seconds cannot be revived by address traffic.
- Clean shutdown sends zero-TTL mDNS goodbye records for the service and host, allowing Android and other clients to discard the old TCP port immediately.
- Relevant mDNS queries receive an immediate announcement on the receiving interface, rate-limited to once per second per interface, instead of waiting for the periodic five-second announcement.
- Incoming TCP connections are logged before the Quick Share handshake, making a transport timeout distinguishable from a later protocol failure.

Focused regression checks cover service-query replies without response loops, newest endpoint selection on a shared IP, old hostname replies, expired endpoints, goodbye invalidation, and the existing split-response cases. The PC network was verified as Private; a Public-network permission explanation was not established here. These changes address concrete stale-discovery paths, but a Pixel-to-PC test is still required to determine whether they fully resolve the reported inbound timeout.

The signed ARM/x86/x64 Release bundle is for local review. No GitHub release is authorized for this development version. Use the same signing certificate as 0.1.8.0, whose exported CER SHA-256 is `c7b4e29835a263cbc71c39a9eb4ff27449fcef2c678e367c2c6bf9ee5cf9a29d`.
