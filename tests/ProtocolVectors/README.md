# Protocol smoke tests

`P256Smoke.cs` validates the shared P-256, Quick Share encryption, CDP identity, and NearShare ValueSet paths. `CdpWireSmoke.cs` validates CDP header serialization, AES-CBC packet encryption, and HMAC rejection.

The UWP source check also compiles every `src/LiveDrop/*.cs` file against the cached WpBlueBubbles UWP reference assemblies and the Windows 10 SDK `Windows.winmd`. Generated executables and DLLs are ignored by the repository.
