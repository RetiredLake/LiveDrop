LiveDrop 0.1.4.9 is a Quick Share transfer hotfix.

It fixes a receiver crash/timeout path caused by encrypted keep-alive and file frames racing while assigning their per-direction sequence numbers. Encryption and the socket write are now serialized as one operation, preserving wire order for both LiveDrop and official Quick Share clients.

It also retains the 0.1.4.8 handshake correction: the receiver sends the connection response and paired-key frame in the order expected by the interoperable Quick Share flow before receiving the introduction and file payloads.

Validation:

- ARM and x64 Release builds use the WpBlueBubbles VS 2019/Windows 10 SDK toolchain.
- The x64 package is installed locally and reports version 0.1.4.9.
- Protocol smoke vectors pass for P-256/Quick Share crypto, CDP wire encoding, mDNS discovery, and adapter selection/shutdown.
- The ARM/x64 bundle is signed with the RetiredLake store certificate.
