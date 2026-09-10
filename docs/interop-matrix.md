# Interoperability matrix

This is the test plan for the first transfer milestone. A row is complete only with a real peer and a captured log that contains protocol state, byte counts, cancellation state, and final SHA-256.

| Local endpoint | Remote endpoint | Path | Required result |
| --- | --- | --- | --- |
| Windows 10 Mobile ARM | Windows 10/11 PC | Microsoft Nearby | discover, accept, send, receive, cancel |
| Windows 10/11 PC | Windows 10 Mobile ARM | Microsoft Nearby | discover, accept, send, receive, cancel |
| Windows 10 Mobile ARM | Android device | Quick Share LAN | discover, accept, send, receive, verify |
| Android device | Windows 10 Mobile ARM | Quick Share LAN | discover, accept, send, receive, verify |

Before declaring an RTM build viable, exercise Bluetooth off, Wi-Fi only, Wi-Fi Direct unavailable, app suspension, network loss, duplicate filenames, multiple files, rejected transfers, malformed frames, and files larger than the small-payload test.

QR/manual bootstrap is allowed for Android if RTM BLE background triggering cannot wake the receiver. It must be documented as a bootstrap path, not called seamless discovery.

