LiveDrop 0.1.4.10 is a Quick Share interoperability and receive-flow hotfix.

- Corrects the final marker for Quick Share control payloads. The marker now carries the completed payload offset and total size, matching NearDrop, NearShare, and current Quick Share clients.
- Corrects the Quick Share endpoint-info device-type encoding and advertises a laptop on desktop Windows or a phone on Windows 10 Mobile.
- Uses the known Quick Share Bluetooth trigger payload so Android can wake its LAN discovery when Bluetooth advertising is available.
- Accepts incoming transfers by default so a LiveDrop receiver does not wait indefinitely for a consent dialog.
- Shows receiver notifications and transfer progress while files arrive.
- Saves images, videos, and other files in the visible `LiveDrop` folder under the corresponding Windows library, with an app-local fallback for Windows builds that do not expose library access.
- Closes a receive session after the final file instead of requiring an extra disconnection frame from the sender.
- Keeps the existing RetiredLake package certificate.

The release is intended for testing between LiveDrop clients and Quick Share. Official client discovery may still depend on its share/receive surface being open when Bluetooth triggering is unavailable.
