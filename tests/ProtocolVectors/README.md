# Protocol smoke tests

`P256Smoke.cs` validates the shared P-256, Quick Share encryption, CDP identity, and NearShare ValueSet paths. `CdpWireSmoke.cs` validates CDP header serialization, AES-CBC packet encryption, and HMAC rejection.

`MdnsSmoke.cs` compiles with the production `Transports/MdnsCodec.cs` and checks DNS announcement counts, endpoint fields, records arriving in separate responses, and unrelated-service filtering. Its Base64 helper replaces only the UWP-dependent utility class for this standalone test.

Run from the repository root in a developer PowerShell:

```powershell
csc /nologo /out:tests\ProtocolVectors\MdnsSmoke.exe tests\ProtocolVectors\MdnsSmoke.cs src\LiveDrop\Transports\MdnsCodec.cs
& .\tests\ProtocolVectors\MdnsSmoke.exe
```

The UWP source check also compiles every `src/LiveDrop/*.cs` file against the cached WpBlueBubbles UWP reference assemblies and the Windows 10 SDK `Windows.winmd`. Generated executables and DLLs are ignored by the repository.

`ShareCoordinatorSmoke.cs` compiles with the production coordinator and protocol interface. Fake network adapters verify all four selections and asynchronous shutdown with a synchronization context that no longer pumps messages:

```powershell
csc /nologo /out:tests\ProtocolVectors\ShareCoordinatorSmoke.exe tests\ProtocolVectors\ShareCoordinatorSmoke.cs src\LiveDrop\Services\ShareCoordinator.cs src\LiveDrop\Protocols\ShareProtocol.cs
& .\tests\ProtocolVectors\ShareCoordinatorSmoke.exe
```

Use the VS 2019 Roslyn `csc.exe` for these lifecycle tests. `ShareSourceSmoke.cs` is a desktop integration fixture that opens the actual Windows share sheet with either valid Unicode text or a deferred text provider that supplies no data. Compile it as a WinForms executable against `System.Windows.Forms`, `System.Drawing`, `System.Runtime.WindowsRuntime`, SDK `Windows.winmd`, and .NET Framework 4.7.2 facade assemblies `System.Runtime` and `System.Runtime.InteropServices.WindowsRuntime`. Choose **LiveDrop** in the share sheet; no content is transmitted unless Send is explicitly clicked. Close the target while discovery continues, then close/reopen the main app. The unavailable-data case should display a read error without terminating the main app.
