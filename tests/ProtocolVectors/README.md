# Protocol smoke tests

`P256Smoke.cs` validates the shared P-256, Quick Share encryption, CDP identity, and NearShare ValueSet paths. `CdpWireSmoke.cs` validates CDP header serialization, AES-CBC packet encryption, and HMAC rejection.

`MdnsSmoke.cs` compiles with the production `Transports/MdnsCodec.cs` and checks DNS announcement counts, endpoint fields, records arriving in separate responses, and unrelated-service filtering. Its Base64 helper replaces only the UWP-dependent utility class for this standalone test.

Run from the repository root in a developer PowerShell:

```powershell
csc /nologo /out:tests\ProtocolVectors\MdnsSmoke.exe tests\ProtocolVectors\MdnsSmoke.cs src\LiveDrop\Transports\MdnsCodec.cs
& .\tests\ProtocolVectors\MdnsSmoke.exe
```

The UWP source check also compiles every `src/LiveDrop/*.cs` file against the cached WpBlueBubbles UWP reference assemblies and the Windows 10 SDK `Windows.winmd`. Generated executables and DLLs are ignored by the repository.
