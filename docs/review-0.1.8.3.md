# LiveDrop 0.1.8.3 ghost-device fix

The duplicate was reproduced on the live network. The two identically named rows actually advertised different endpoints:

- Stale: Windows phone, livedrop-fbO4.local., 192.168.86.21:50566.
- Current: Windows phone, livedrop-3j2l.local., 192.168.86.36:50606.

Both complete announcements were observed in DNS response packets from 192.168.86.36. TCP connection checks succeeded for .36:50606, failed for .21:50566, and failed for the old port 50566 on .36 too. The user confirmed one LiveDrop phone and one Android device, rather than two LiveDrop phones. IP-based row merging could not remove this ghost because the advertised addresses differed.

For LiveDrop's own raw announcements (whose hostnames start with livedrop-), discovery now requires the advertised address to match the response source address. This rejects a wrong-interface/stale LiveDrop address instead of merging unrelated devices by display name. The rule does not apply to generic DNS-SD clients, which can use responder proxies. Separately, DNS queries with cached known-answer sections no longer create or renew peers.

Focused regressions cover the captured stale/current address pair, two genuinely distinct same-name phones, generic proxy responses, and queries containing complete cached advertisements. Existing discovery regressions pass. The ARM/x86/x64 Release bundle was signed and verified using the same certificate; its CER SHA-256 remains c7b4e29835a263cbc71c39a9eb4ff27449fcef2c678e367c2c6bf9ee5cf9a29d.

Installed locally as 0.1.8.3. Computer-use inspection of the running app confirmed one Windows phone row in both the screenshot and accessibility tree. No GitHub release was published. The earlier phone-to-phone transfer report still requires a two-phone device retest.
