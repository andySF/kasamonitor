# KasaMonitor Implementation Plan

> **For agentic workers:** REQUIRED: Use superpowers:subagent-driven-development (if subagents available) or superpowers:executing-plans to implement this plan. Steps use checkbox (`- [ ]`) syntax for tracking.

**Spec:** `docs/superpowers/specs/2026-05-08-kasa-blazor-design.md`

**Goal:** Build a self-hosted Blazor Server (.NET 10) monitoring app for 5 TP-Link KP115 Kasa smart plugs, deployed as a single Docker image on QNAP via Portainer/docker-compose, with auto-discovery, 10-second real-time polling, device-side history sync, SQLite persistence with rollup/retention, and a live LAN-only dashboard.

**Architecture:** Four C# library projects (`Core`, `Data`, `Workers`, `Web`) plus two test projects, all in one solution. `Core` holds pure logic (Kasa wire protocol, models, costing) + abstractions; `Data` implements the abstractions on EF Core + SQLite; `Workers` are `IHostedService` background loops; `Web` hosts Blazor Server + DI + UI. A multi-stage `Dockerfile` produces a `linux/amd64` image pushed to `andysf/kasamonitor` on Docker Hub.

**Tech Stack:** .NET 10 LTS, ASP.NET Core, Blazor Server, EF Core 10 with SQLite (WAL mode), xUnit, Serilog, ApexCharts.Blazor (default chart lib; spike at chunk 5 may switch), Docker, GitHub Actions for releases, Portainer + Container Station on QNAP.

**Working directory:** `C:\Users\andys\My Projects\KasaMonitor` (already initialized as a git repo with the spec committed). All paths in this plan are relative to that directory unless prefixed with `C:\`.

**Reference data:** `C:\Users\andys\kasa_full.json` is the authoritative payload capture from all 5 production plugs and is the source for test fixtures.

---

## File Structure

```
KasaMonitor.sln                               # solution file
Directory.Build.props                         # shared MSBuild props (TFM, nullable, warnings)
.gitignore                                    # .NET-flavored
.dockerignore
README.md                                     # top-level project README
src/
  KasaMonitor.Core/
    KasaMonitor.Core.csproj                   # net10.0, no deps beyond System.Text.Json
    Protocol/
      KasaCodec.cs                            # XOR encode/decode (pure, ~30 LoC)
      KasaWireProtocol.cs                     # TCP framing + UDP send/recv helpers
      IKasaClient.cs                          # interface
      KasaClient.cs                           # implementation
      Dtos/
        SysInfoDto.cs                         # mirrors device JSON shape
        EmeterRealtimeDto.cs
        DayStatDto.cs
        MonthStatDto.cs
    Models/
      PlugInfo.cs                             # mapped from SysInfoDto
      EmeterReading.cs                        # mapped from EmeterRealtimeDto
      DailyKwh.cs
      MonthlyKwh.cs
      DiscoveredPlug.cs
      PlugLiveState.cs
    Costing/
      TariffCalculator.cs
    Abstractions/
      IPlugRepository.cs
      ISampleRepository.cs
      IHistoryRepository.cs
      ISettingsRepository.cs
      IPlugStateBroadcaster.cs
  KasaMonitor.Data/
    KasaMonitor.Data.csproj                   # references Core, EF Core SQLite
    KasaDbContext.cs
    Entities/                                 # one file per entity
      PlugEntity.cs
      SampleEntity.cs
      OneMinuteAggregateEntity.cs
      OneHourAggregateEntity.cs
      DailyEnergyEntity.cs
      MonthlyEnergyEntity.cs
      SettingEntity.cs
    Repositories/
      PlugRepository.cs
      SampleRepository.cs
      HistoryRepository.cs
      SettingsRepository.cs
    Migrations/                               # EF-generated; one initial migration
  KasaMonitor.Workers/
    KasaMonitor.Workers.csproj                # references Core + Data + Hosting
    DiscoveryWorker.cs
    PollingWorker.cs
    HistorySyncWorker.cs
    RollupWorker.cs
    PlugStateBroadcaster.cs                   # in-process pub/sub for UI
    DependencyInjectionExtensions.cs          # AddKasaWorkers()
  KasaMonitor.Web/
    KasaMonitor.Web.csproj                    # Sdk=Microsoft.NET.Sdk.Web
    Program.cs
    appsettings.json
    appsettings.Production.json
    Components/
      App.razor
      Routes.razor
      _Imports.razor
      Layout/
        MainLayout.razor
        NavMenu.razor
      Pages/
        Dashboard.razor
        History.razor
        Plugs.razor
        Settings.razor
        Error.razor
    Endpoints/
      HealthEndpoint.cs
    Cli/
      HealthCheckCommand.cs                   # invoked by `dotnet KasaMonitor.Web.dll --healthcheck`
    wwwroot/
      site.css
tests/
  KasaMonitor.Core.Tests/
    KasaMonitor.Core.Tests.csproj             # xunit, references Core
    Protocol/
      KasaCodecTests.cs
      KasaWireProtocolTests.cs
      KasaClientTests.cs
      DtoMappingTests.cs
    Costing/
      TariffCalculatorTests.cs
    fixtures/                                 # .json files, copied to output
      sysinfo_kp115_birou.json
      sysinfo_kp115_sufeagerie.json
      emeter_kp115.json
      daystat_kp115.json
      monstat_kp115.json
  KasaMonitor.Data.Tests/
    KasaMonitor.Data.Tests.csproj             # xunit, references Data
    PlugRepositoryTests.cs
    SampleRepositoryTests.cs
    HistoryRepositoryTests.cs
    SettingsRepositoryTests.cs
    RollupSqlTests.cs
    TestDbFactory.cs                          # SQLite in-memory shared connection
deploy/
  Dockerfile
  docker-compose.yml
  README.md
.github/
  workflows/
    ci.yml                                    # build + test on push/PR
    release.yml                               # build + push image on tag v*
```

**File-size discipline:** every `.cs` file in this plan targets ≤ 200 lines. If a file approaches 200, split before adding more.

---

## Chunk 1: Solution scaffold + Core protocol

This chunk creates the .NET solution skeleton, the `KasaMonitor.Core` library, and the Kasa wire-protocol implementation with a complete unit-test suite using JSON fixtures captured from real plugs. By the end of the chunk, you can encode/decode a Kasa frame, parse a device payload, and run `dotnet test` with green output — but no DB or UI yet.

### Task 1.1: Verify .NET 10 SDK, gitignore, shared MSBuild props

**Files:**
- Create: `.gitignore`
- Create: `.dockerignore`
- Create: `Directory.Build.props`

- [ ] **Step 1: Verify .NET 10 SDK is installed**

Run: `dotnet --list-sdks`

Expected: at least one entry starting with `10.` (e.g., `10.0.100`). If absent: install from https://dotnet.microsoft.com/download/dotnet/10.0 (LTS) before continuing. Do not proceed with .NET 8 or 9.

- [ ] **Step 2: Verify git repo state**

Run: `git -C "C:/Users/andys/My Projects/KasaMonitor" log --oneline`

Expected: two existing commits — the spec and the spec refinement. Working tree clean.

- [ ] **Step 3: Create `.gitignore`**

Write file `.gitignore` with the following content (Microsoft default + a few extras):

```gitignore
# build output
bin/
obj/
out/
publish/
artifacts/

# IDE
.vs/
.vscode/
.idea/
*.user
*.suo

# test output
TestResults/
*.coverage
coverage/
*.lcov

# database & runtime data
*.db
*.db-shm
*.db-wal
data/

# logs
*.log
logs/

# secrets
appsettings.*.local.json
.env
.env.*

# OS
Thumbs.db
.DS_Store
```

- [ ] **Step 4: Create `.dockerignore`**

Write `.dockerignore`:

```dockerignore
**/bin
**/obj
**/.vs
**/.vscode
**/.idea
**/TestResults
**/coverage
**/*.user
**/*.suo
**/appsettings.*.local.json
.git
.gitignore
docs/
*.md
!README.md
```

- [ ] **Step 5: Create `Directory.Build.props`**

This file lives at the repo root and is auto-loaded by every csproj under it, eliminating duplicate `<PropertyGroup>` blocks across the 6 future projects.

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>
</Project>
```

- [ ] **Step 6: Commit**

```bash
git add .gitignore .dockerignore Directory.Build.props
git commit -m "chore: add gitignore, dockerignore, and shared MSBuild props"
```

### Task 1.2: Create empty solution and project skeleton

**Files:**
- Create: `KasaMonitor.sln`
- Create: `src/KasaMonitor.Core/KasaMonitor.Core.csproj`
- Create: `tests/KasaMonitor.Core.Tests/KasaMonitor.Core.Tests.csproj`

- [ ] **Step 1: Create the solution file**

From the project root:

```bash
dotnet new sln -n KasaMonitor
```

Expected: `KasaMonitor.sln` is created. `dotnet sln list` returns "No projects".

- [ ] **Step 2: Create the `Core` class library targeting .NET 10**

```bash
dotnet new classlib -n KasaMonitor.Core -o src/KasaMonitor.Core -f net10.0
```

Expected: `src/KasaMonitor.Core/KasaMonitor.Core.csproj` exists; `Class1.cs` was generated.

- [ ] **Step 3: Delete the generated stub class**

Delete `src/KasaMonitor.Core/Class1.cs`.

- [ ] **Step 4: Slim down the csproj — shared props come from `Directory.Build.props`**

Replace contents of `src/KasaMonitor.Core/KasaMonitor.Core.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>KasaMonitor.Core</RootNamespace>
  </PropertyGroup>
</Project>
```

- [ ] **Step 5: Create the `Core.Tests` xUnit project**

```bash
dotnet new xunit -n KasaMonitor.Core.Tests -o tests/KasaMonitor.Core.Tests -f net10.0
```

Expected: `tests/KasaMonitor.Core.Tests/KasaMonitor.Core.Tests.csproj` and a `UnitTest1.cs` are created.

- [ ] **Step 6: Delete the generated stub test class**

Delete `tests/KasaMonitor.Core.Tests/UnitTest1.cs`.

- [ ] **Step 7: Slim down the test csproj + reference Core + copy fixtures**

Replace contents of `tests/KasaMonitor.Core.Tests/KasaMonitor.Core.Tests.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <RootNamespace>KasaMonitor.Core.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\KasaMonitor.Core\KasaMonitor.Core.csproj" />
  </ItemGroup>

  <ItemGroup>
    <None Update="fixtures\**\*">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
</Project>
```

- [ ] **Step 8: Add both projects to the solution**

```bash
dotnet sln add src/KasaMonitor.Core/KasaMonitor.Core.csproj
dotnet sln add tests/KasaMonitor.Core.Tests/KasaMonitor.Core.Tests.csproj
```

Expected: `dotnet sln list` shows both.

- [ ] **Step 9: Verify solution builds**

```bash
dotnet build
```

Expected: succeeds with 0 errors and 0 warnings.

- [ ] **Step 10: Verify tests run (zero tests, zero failures)**

```bash
dotnet test
```

Expected: "Passed!  - Failed: 0, Passed: 0, Skipped: 0, Total: 0".

- [ ] **Step 11: Commit**

```bash
git add KasaMonitor.sln src tests
git commit -m "chore: scaffold solution with Core + Core.Tests projects"
```

### Task 1.3: Capture and stage real device fixtures

**Files:**
- Create: `tests/KasaMonitor.Core.Tests/fixtures/sysinfo_kp115_birou.json`
- Create: `tests/KasaMonitor.Core.Tests/fixtures/sysinfo_kp115_sufeagerie.json`
- Create: `tests/KasaMonitor.Core.Tests/fixtures/emeter_kp115.json`
- Create: `tests/KasaMonitor.Core.Tests/fixtures/daystat_kp115.json`
- Create: `tests/KasaMonitor.Core.Tests/fixtures/monstat_kp115.json`

Source: `C:\Users\andys\kasa_full.json` — a top-level dict keyed by IP. Each entry has the shape:

```jsonc
{
  "192.168.254.105": {
    "sysinfo":  { "system": { "get_sysinfo":   { ...the actual payload... } } },
    "realtime": { "emeter": { "get_realtime":  { ...payload... } } },
    "daystat":  { "emeter": { "get_daystat":   { ...payload... } } },
    "monstat":  { "emeter": { "get_monthstat": { ...payload... } } }
  },
  "192.168.254.123": { ... }
}
```

We want to extract the **innermost** payload (under `get_sysinfo` / `get_realtime` / `get_daystat` / `get_monthstat`), not the wrapped envelope. That's what the device returns on the wire after framing/XOR are stripped.

- [ ] **Step 1: Verify the source-file shape and record actual values**

Run in PowerShell from project root:

```powershell
$src = Get-Content "C:\Users\andys\kasa_full.json" -Raw | ConvertFrom-Json
$src.PSObject.Properties.Name | Format-Table        # list of IP keys
$src."192.168.254.105".sysinfo.system.get_sysinfo |
  Select-Object alias, model, mac, sw_ver, hw_ver, rssi, relay_state |
  Format-List
$src."192.168.254.123".sysinfo.system.get_sysinfo |
  Select-Object alias, model, mac, sw_ver |
  Format-List
```

Expected: 5 IP keys total. The first lookup prints the Birou plug; the second prints Sufeagerie with `sw_ver` containing `210205` (or similar Build-2021 string). **Record the exact `alias`, `mac`, and `sw_ver` strings you observe**, since later test assertions in Task 1.6 must match these values verbatim.

- [ ] **Step 2: Extract `sysinfo_kp115_birou.json`**

```powershell
$src."192.168.254.105".sysinfo.system.get_sysinfo |
  ConvertTo-Json -Depth 6 |
  Set-Content "tests/KasaMonitor.Core.Tests/fixtures/sysinfo_kp115_birou.json" -Encoding UTF8
```

- [ ] **Step 3: Extract `sysinfo_kp115_sufeagerie.json`** (older firmware + populated GPS coords)

```powershell
$src."192.168.254.123".sysinfo.system.get_sysinfo |
  ConvertTo-Json -Depth 6 |
  Set-Content "tests/KasaMonitor.Core.Tests/fixtures/sysinfo_kp115_sufeagerie.json" -Encoding UTF8
```

- [ ] **Step 4: Extract `emeter_kp115.json`**

```powershell
$src."192.168.254.105".realtime.emeter.get_realtime |
  ConvertTo-Json -Depth 6 |
  Set-Content "tests/KasaMonitor.Core.Tests/fixtures/emeter_kp115.json" -Encoding UTF8
```

- [ ] **Step 5: Extract `daystat_kp115.json`**

```powershell
$src."192.168.254.105".daystat.emeter.get_daystat |
  ConvertTo-Json -Depth 6 |
  Set-Content "tests/KasaMonitor.Core.Tests/fixtures/daystat_kp115.json" -Encoding UTF8
```

- [ ] **Step 6: Extract `monstat_kp115.json`**

```powershell
$src."192.168.254.105".monstat.emeter.get_monthstat |
  ConvertTo-Json -Depth 6 |
  Set-Content "tests/KasaMonitor.Core.Tests/fixtures/monstat_kp115.json" -Encoding UTF8
```

- [ ] **Step 7: Verify all fixtures are valid JSON**

```powershell
Get-ChildItem tests/KasaMonitor.Core.Tests/fixtures/*.json | ForEach-Object {
  try { Get-Content $_ -Raw | ConvertFrom-Json | Out-Null; "$($_.Name) OK" }
  catch { "$($_.Name) BAD: $_" }
}
```

Expected: every file reports `OK`.

- [ ] **Step 8: Commit fixtures**

```bash
git add tests/KasaMonitor.Core.Tests/fixtures
git commit -m "test: add KP115 JSON payload fixtures captured from real plugs"
```

### Task 1.4: Implement KasaCodec (XOR encrypt/decrypt) — TDD

**Files:**
- Test: `tests/KasaMonitor.Core.Tests/Protocol/KasaCodecTests.cs`
- Create: `src/KasaMonitor.Core/Protocol/KasaCodec.cs`

The codec is pure and stateless. Algorithm (canonical Kasa):
- **Encrypt:** start `key=171`. For each plaintext byte `b`, set `key = key XOR b`, output `key`.
- **Decrypt:** start `key=171`. For each ciphertext byte `c`, output `key XOR c`, set `key = c`.

- [ ] **Step 1: Write the failing tests**

Create `tests/KasaMonitor.Core.Tests/Protocol/KasaCodecTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Text;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaCodecTests
{
    [Fact]
    public void Encrypt_EmptyInput_ReturnsEmpty()
    {
        KasaCodec.Encrypt(ReadOnlySpan<byte>.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Decrypt_EmptyInput_ReturnsEmpty()
    {
        KasaCodec.Decrypt(ReadOnlySpan<byte>.Empty).Should().BeEmpty();
    }

    [Fact]
    public void Encrypt_KnownVector_ProducesExpectedBytes()
    {
        // Plaintext "{}" -> bytes [123, 125].
        // key=171:
        //   123 XOR 171 = 208 (0xD0). new key = 208
        //   125 XOR 208 = 173 (0xAD). new key = 173
        var plaintext = Encoding.UTF8.GetBytes("{}");
        var encrypted = KasaCodec.Encrypt(plaintext);
        encrypted.Should().Equal(new byte[] { 0xD0, 0xAD });
    }

    [Fact]
    public void Decrypt_InverseOfEncrypt_RoundTrips()
    {
        var plaintext = Encoding.UTF8.GetBytes("{\"system\":{\"get_sysinfo\":{}}}");
        var encrypted = KasaCodec.Encrypt(plaintext);
        var decrypted = KasaCodec.Decrypt(encrypted);
        decrypted.Should().Equal(plaintext);
    }

    [Theory]
    [InlineData("a")]
    [InlineData("hello")]
    [InlineData("{\"emeter\":{\"get_realtime\":{}}}")]
    public void RoundTrip_PreservesAnyAsciiInput(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var roundTripped = Encoding.UTF8.GetString(KasaCodec.Decrypt(KasaCodec.Encrypt(bytes)));
        roundTripped.Should().Be(input);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~KasaCodec"
```

Expected: build fails with "type or namespace 'KasaCodec' could not be found".

- [ ] **Step 3: Implement `KasaCodec`**

Create `src/KasaMonitor.Core/Protocol/KasaCodec.cs`:

```csharp
namespace KasaMonitor.Core.Protocol;

/// <summary>Stateless XOR cipher used by TP-Link Kasa devices on the wire (port 9999).</summary>
public static class KasaCodec
{
    private const byte InitialKey = 171;

    public static byte[] Encrypt(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.IsEmpty) return Array.Empty<byte>();
        var output = new byte[plaintext.Length];
        byte key = InitialKey;
        for (int i = 0; i < plaintext.Length; i++)
        {
            key = (byte)(key ^ plaintext[i]);
            output[i] = key;
        }
        return output;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> ciphertext)
    {
        if (ciphertext.IsEmpty) return Array.Empty<byte>();
        var output = new byte[ciphertext.Length];
        byte key = InitialKey;
        for (int i = 0; i < ciphertext.Length; i++)
        {
            output[i] = (byte)(key ^ ciphertext[i]);
            key = ciphertext[i];
        }
        return output;
    }
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~KasaCodec"
```

Expected: 7 passed, 0 failed (4 facts + 3 InlineData rows on `RoundTrip_PreservesAnyAsciiInput`).

- [ ] **Step 5: Commit**

```bash
git add src/KasaMonitor.Core/Protocol/KasaCodec.cs tests/KasaMonitor.Core.Tests/Protocol/KasaCodecTests.cs
git commit -m "feat(core): implement KasaCodec XOR cipher with round-trip tests"
```

### Task 1.5: Implement KasaWireProtocol (TCP framing + UDP)

**Files:**
- Test: `tests/KasaMonitor.Core.Tests/Protocol/KasaWireProtocolTests.cs`
- Create: `src/KasaMonitor.Core/Protocol/KasaWireProtocol.cs`

`KasaWireProtocol` is the byte-level adapter between an in-memory Kasa JSON request and the wire bytes. **No socket I/O lives here** — that's `KasaClient`'s job. Keeping framing pure makes it testable without sockets.

Wire rules:
- **TCP**: 4-byte big-endian length prefix, then XOR-encrypted JSON. Same on both directions.
- **UDP**: raw XOR-encrypted JSON (no length prefix).

- [ ] **Step 1: Write the failing tests**

Create `tests/KasaMonitor.Core.Tests/Protocol/KasaWireProtocolTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Text;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaWireProtocolTests
{
    [Fact]
    public void EncodeTcp_PrependsBigEndianLength()
    {
        var bytes = KasaWireProtocol.EncodeTcp("{}");
        bytes.Length.Should().Be(4 + 2);
        bytes[0..4].Should().Equal(new byte[] { 0, 0, 0, 2 }); // length = 2
    }

    [Fact]
    public void EncodeTcp_BodyIsXorEncrypted()
    {
        var bytes = KasaWireProtocol.EncodeTcp("{}");
        bytes[4..].Should().Equal(KasaCodec.Encrypt(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void DecodeTcp_StripsLengthAndDecrypts()
    {
        var encoded = KasaWireProtocol.EncodeTcp("hello");
        var decoded = KasaWireProtocol.DecodeTcp(encoded);
        decoded.Should().Be("hello");
    }

    [Fact]
    public void DecodeTcp_ThrowsWhenLengthMismatch()
    {
        var truncated = new byte[] { 0, 0, 0, 100, 1, 2, 3 };
        var act = () => KasaWireProtocol.DecodeTcp(truncated);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void EncodeUdp_HasNoLengthPrefix()
    {
        var bytes = KasaWireProtocol.EncodeUdp("{}");
        bytes.Should().Equal(KasaCodec.Encrypt(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void DecodeUdp_DecryptsRaw()
    {
        var encoded = KasaWireProtocol.EncodeUdp("hello");
        KasaWireProtocol.DecodeUdp(encoded).Should().Be("hello");
    }

    [Fact]
    public void DecodeTcp_RejectsHeaderTooShort()
    {
        var act = () => KasaWireProtocol.DecodeTcp(new byte[] { 0, 0, 1 });
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void DecodeTcp_RejectsAbsurdLength()
    {
        // Length declared 16 MiB; we cap at 1 MiB to avoid OOM amplification.
        var bytes = new byte[] { 0x01, 0x00, 0x00, 0x00 };
        var act = () => KasaWireProtocol.DecodeTcp(bytes);
        act.Should().Throw<InvalidDataException>().WithMessage("*too large*");
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~KasaWireProtocol"
```

Expected: build error (`KasaWireProtocol` not found).

- [ ] **Step 3: Implement `KasaWireProtocol`**

Create `src/KasaMonitor.Core/Protocol/KasaWireProtocol.cs`:

```csharp
using System.Buffers.Binary;
using System.Text;

namespace KasaMonitor.Core.Protocol;

/// <summary>
/// Byte-level framing for Kasa protocol. TCP uses 4-byte big-endian length prefix + XOR JSON.
/// UDP uses raw XOR JSON. No socket I/O here — see <see cref="KasaClient"/>.
/// </summary>
public static class KasaWireProtocol
{
    private const int MaxBodyBytes = 1 << 20; // 1 MiB safety cap; real payloads are < 8 KiB.

    public static byte[] EncodeTcp(string json)
    {
        var body = KasaCodec.Encrypt(Encoding.UTF8.GetBytes(json));
        var output = new byte[4 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(output.AsSpan(0, 4), body.Length);
        body.CopyTo(output, 4);
        return output;
    }

    public static string DecodeTcp(ReadOnlySpan<byte> framed)
    {
        if (framed.Length < 4)
            throw new InvalidDataException("Frame too short to contain length header.");
        var declared = BinaryPrimitives.ReadInt32BigEndian(framed[..4]);
        if (declared < 0 || declared > MaxBodyBytes)
            throw new InvalidDataException($"Frame length {declared} too large.");
        if (framed.Length - 4 < declared)
            throw new InvalidDataException(
                $"Frame body truncated: declared {declared} bytes, got {framed.Length - 4}.");
        return Encoding.UTF8.GetString(KasaCodec.Decrypt(framed.Slice(4, declared)));
    }

    public static byte[] EncodeUdp(string json) =>
        KasaCodec.Encrypt(Encoding.UTF8.GetBytes(json));

    public static string DecodeUdp(ReadOnlySpan<byte> ciphertext) =>
        Encoding.UTF8.GetString(KasaCodec.Decrypt(ciphertext));
}
```

- [ ] **Step 4: Run tests to verify pass**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~KasaWireProtocol"
```

Expected: 8 passed, 0 failed.

- [ ] **Step 5: Commit**

```bash
git add src/KasaMonitor.Core/Protocol/KasaWireProtocol.cs tests/KasaMonitor.Core.Tests/Protocol/KasaWireProtocolTests.cs
git commit -m "feat(core): implement Kasa TCP/UDP framing with safety cap"
```

### Task 1.6: Define DTO records mapped to device JSON

**Files:**
- Create: `src/KasaMonitor.Core/Protocol/Dtos/SysInfoDto.cs`
- Create: `src/KasaMonitor.Core/Protocol/Dtos/EmeterRealtimeDto.cs`
- Create: `src/KasaMonitor.Core/Protocol/Dtos/DayStatDto.cs`
- Create: `src/KasaMonitor.Core/Protocol/Dtos/MonthStatDto.cs`
- Test: `tests/KasaMonitor.Core.Tests/Protocol/DtoMappingTests.cs`

The DTOs mirror the **inner** payload (after stripping `system.get_sysinfo` / `emeter.get_realtime` wrappers). They use `JsonPropertyName` because device names use snake_case.

- [ ] **Step 1: Write `SysInfoDto.cs`**

All string fields are nullable: real-world payloads vary slightly by firmware version, and `JsonSerializer` would silently leave a non-nullable string as `null` if the key is missing, defeating the nullable-reference type system. The mapping layer in Chunk 2 enforces presence and produces a clean domain model.

```csharp
using System.Text.Json.Serialization;

namespace KasaMonitor.Core.Protocol.Dtos;

public sealed record SysInfoDto(
    [property: JsonPropertyName("alias")] string? Alias,
    [property: JsonPropertyName("dev_name")] string? DevName,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("hw_ver")] string? HwVer,
    [property: JsonPropertyName("sw_ver")] string? SwVer,
    [property: JsonPropertyName("mac")] string? Mac,
    [property: JsonPropertyName("deviceId")] string? DeviceId,
    [property: JsonPropertyName("hwId")] string? HwId,
    [property: JsonPropertyName("oemId")] string? OemId,
    [property: JsonPropertyName("relay_state")] int RelayState,
    [property: JsonPropertyName("on_time")] long OnTime,
    [property: JsonPropertyName("rssi")] int Rssi,
    [property: JsonPropertyName("feature")] string? Feature
);
```

- [ ] **Step 2: Write `EmeterRealtimeDto.cs`**

```csharp
using System.Text.Json.Serialization;

namespace KasaMonitor.Core.Protocol.Dtos;

public sealed record EmeterRealtimeDto(
    [property: JsonPropertyName("voltage_mv")] long VoltageMv,
    [property: JsonPropertyName("current_ma")] long CurrentMa,
    [property: JsonPropertyName("power_mw")] long PowerMw,
    [property: JsonPropertyName("total_wh")] long TotalWh,
    [property: JsonPropertyName("err_code")] int ErrCode
);
```

- [ ] **Step 3: Write `DayStatDto.cs`**

```csharp
using System.Text.Json.Serialization;

namespace KasaMonitor.Core.Protocol.Dtos;

public sealed record DayStatDto(
    [property: JsonPropertyName("day_list")] IReadOnlyList<DayEntryDto> DayList,
    [property: JsonPropertyName("err_code")] int ErrCode
);

public sealed record DayEntryDto(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("day")] int Day,
    [property: JsonPropertyName("energy_wh")] long EnergyWh
);
```

- [ ] **Step 4: Write `MonthStatDto.cs`**

```csharp
using System.Text.Json.Serialization;

namespace KasaMonitor.Core.Protocol.Dtos;

public sealed record MonthStatDto(
    [property: JsonPropertyName("month_list")] IReadOnlyList<MonthEntryDto> MonthList,
    [property: JsonPropertyName("err_code")] int ErrCode
);

public sealed record MonthEntryDto(
    [property: JsonPropertyName("year")] int Year,
    [property: JsonPropertyName("month")] int Month,
    [property: JsonPropertyName("energy_wh")] long EnergyWh
);
```

- [ ] **Step 5: Write fixture-driven mapping tests**

Create `tests/KasaMonitor.Core.Tests/Protocol/DtoMappingTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Protocol.Dtos;
using System.Text.Json;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class DtoMappingTests
{
    private static T Load<T>(string fixture)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fixture);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json)!;
    }

    [Fact]
    public void SysInfo_BirouFixture_DeserializesCorrectly()
    {
        var dto = Load<SysInfoDto>("sysinfo_kp115_birou.json");
        dto.Alias.Should().Be("Birou");                       // adjust if you observed a different alias in Task 1.3 Step 1
        dto.Model.Should().StartWith("KP115");
        dto.Mac.Should().StartWith("C0:06:C3");               // adjust if your MAC OUI differs
        dto.RelayState.Should().Be(1);
        dto.Rssi.Should().BeNegative();
    }

    [Fact]
    public void SysInfo_SufeagerieFixture_HasOldFirmware()
    {
        var dto = Load<SysInfoDto>("sysinfo_kp115_sufeagerie.json");
        dto.SwVer.Should().NotBeNull();
        dto.SwVer!.Should().Contain("210205");                // adjust if you observed a different build string in Task 1.3 Step 1
    }

    [Fact]
    public void EmeterFixture_HasNonZeroPower()
    {
        var dto = Load<EmeterRealtimeDto>("emeter_kp115.json");
        dto.PowerMw.Should().BeGreaterThan(0);
        dto.VoltageMv.Should().BeGreaterThan(200_000); // > 200 V
        dto.ErrCode.Should().Be(0);
    }

    [Fact]
    public void DayStatFixture_ContainsDayEntries()
    {
        var dto = Load<DayStatDto>("daystat_kp115.json");
        dto.DayList.Should().NotBeEmpty();
        dto.DayList.Should().AllSatisfy(d =>
        {
            d.Year.Should().BeGreaterThan(2020);
            d.Day.Should().BeInRange(1, 31);
        });
    }

    [Fact]
    public void MonthStatFixture_ContainsMonthEntries()
    {
        var dto = Load<MonthStatDto>("monstat_kp115.json");
        dto.MonthList.Should().NotBeEmpty();
        dto.MonthList.Should().AllSatisfy(m => m.Month.Should().BeInRange(1, 12));
    }
}
```

- [ ] **Step 6: Run tests**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~DtoMapping"
```

Expected: 5 passed.

- [ ] **Step 7: Commit**

```bash
git add src/KasaMonitor.Core/Protocol/Dtos tests/KasaMonitor.Core.Tests/Protocol/DtoMappingTests.cs
git commit -m "feat(core): add Kasa DTOs with fixture-based mapping tests"
```

### Task 1.7: Implement IKasaClient + KasaClient with TCP I/O

**Files:**
- Create: `src/KasaMonitor.Core/Models/PlugInfo.cs` (stub — replaced in Chunk 2)
- Create: `src/KasaMonitor.Core/Models/EmeterReading.cs` (stub)
- Create: `src/KasaMonitor.Core/Models/DailyKwh.cs` (stub)
- Create: `src/KasaMonitor.Core/Models/MonthlyKwh.cs` (stub)
- Create: `src/KasaMonitor.Core/Models/DiscoveredPlug.cs` (stub)
- Create: `src/KasaMonitor.Core/Protocol/IKasaClient.cs`
- Create: `src/KasaMonitor.Core/Protocol/KasaClient.cs`
- Test: `tests/KasaMonitor.Core.Tests/Protocol/KasaClientTests.cs`

`KasaClient` is the only place in `Core` that touches sockets. In Chunk 1 it implements TCP/9999 for the four query methods. UDP discovery is **scaffolded as not-implemented** here and finished in Chunk 2 where `DiscoveredPlug` becomes a proper model. Splitting the UDP path into its own file (`KasaClient.Udp.cs`) is a Chunk-2 task to keep file size in check.

The placeholder model records are created as **separate files** so Chunk 2 can simply overwrite them — no surgical edits inside `IKasaClient.cs` required.

- [ ] **Step 1a: Create stub model files**

Create `src/KasaMonitor.Core/Models/PlugInfo.cs`:

```csharp
namespace KasaMonitor.Core.Models;

// Stub: full model lands in Chunk 2 (Task 2.1).
public sealed record PlugInfo;
```

Create the other four stubs the same way:

```csharp
// src/KasaMonitor.Core/Models/EmeterReading.cs
namespace KasaMonitor.Core.Models;
public sealed record EmeterReading;
```
```csharp
// src/KasaMonitor.Core/Models/DailyKwh.cs
namespace KasaMonitor.Core.Models;
public sealed record DailyKwh;
```
```csharp
// src/KasaMonitor.Core/Models/MonthlyKwh.cs
namespace KasaMonitor.Core.Models;
public sealed record MonthlyKwh;
```
```csharp
// src/KasaMonitor.Core/Models/DiscoveredPlug.cs
namespace KasaMonitor.Core.Models;
public sealed record DiscoveredPlug;
```

Note the lack of `()` — `public sealed record PlugInfo;` (no parentheses) declares an empty record without a primary constructor and avoids the `CS9301` warning under `TreatWarningsAsErrors`.

- [ ] **Step 1b: Write `IKasaClient.cs`**

```csharp
using KasaMonitor.Core.Models;
using System.Net;

namespace KasaMonitor.Core.Protocol;

public interface IKasaClient
{
    Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct);
    Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct);
    Task<IReadOnlyList<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct);
    Task<IReadOnlyList<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct);
    IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(IReadOnlyList<IPAddress> broadcastAddresses, TimeSpan timeout, CancellationToken ct);
}
```

- [ ] **Step 2: Write the failing tests using a loopback TCP listener**

Create `tests/KasaMonitor.Core.Tests/Protocol/KasaClientTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaClientTests
{
    private static (TcpListener listener, IPEndPoint endpoint) StartLoopback()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return (listener, (IPEndPoint)listener.LocalEndpoint);
    }

    [Fact]
    public async Task GetSysInfoAsync_SendsCorrectRequestAndDecodesResponse()
    {
        var (listener, endpoint) = StartLoopback();
        try
        {
            var sysinfoJson = File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "fixtures", "sysinfo_kp115_birou.json"));
            var responseJson = $"{{\"system\":{{\"get_sysinfo\":{sysinfoJson}}}}}";

            var serverTask = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                await using var stream = tcp.GetStream();

                var hdr = new byte[4];
                await stream.ReadExactlyAsync(hdr);
                int len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
                var body = new byte[len];
                await stream.ReadExactlyAsync(body);

                var requestJson = KasaWireProtocol.DecodeTcp(hdr.Concat(body).ToArray());
                requestJson.Should().Contain("get_sysinfo");

                var resp = KasaWireProtocol.EncodeTcp(responseJson);
                await stream.WriteAsync(resp);
            });

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var clientTask = client.GetSysInfoAsync(IPAddress.Loopback, CancellationToken.None);

            await Task.WhenAll(clientTask, serverTask);
            clientTask.Result.Should().NotBeNull();   // proper model assertions land in Chunk 2
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetSysInfoAsync_TimeoutRaisesTimeoutException()
    {
        var (listener, endpoint) = StartLoopback();
        try
        {
            // Listener doesn't accept; let the client time out.
            var client = new KasaClient(TimeSpan.FromMilliseconds(150), portOverride: endpoint.Port);
            var act = async () => await client.GetSysInfoAsync(IPAddress.Loopback, CancellationToken.None);
            await act.Should().ThrowAsync<TimeoutException>();
        }
        finally { listener.Stop(); }
    }
}
```

- [ ] **Step 3: Run tests to verify failure**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~KasaClient"
```

Expected: build fails (no `KasaClient` type).

- [ ] **Step 4: Implement `KasaClient`**

Create `src/KasaMonitor.Core/Protocol/KasaClient.cs`:

```csharp
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Dtos;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace KasaMonitor.Core.Protocol;

public sealed class KasaClient : IKasaClient
{
    private const int DefaultPort = 9999;
    private readonly TimeSpan _timeout;
    private readonly int _port;

    public KasaClient() : this(TimeSpan.FromSeconds(2.5), DefaultPort) { }

    /// <param name="portOverride">Override the standard 9999 port. Test-only — production code uses <see cref="DefaultPort"/>.</param>
    public KasaClient(TimeSpan timeout, int portOverride = DefaultPort)
    {
        _timeout = timeout;
        _port = portOverride;
    }

    public async Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct)
    {
        // Chunk 2 will map SysInfoDto -> PlugInfo here.
        _ = await TcpQueryAsync<SysInfoDto>(ip, "{\"system\":{\"get_sysinfo\":{}}}", "system", "get_sysinfo", ct);
        return new PlugInfo();
    }

    public async Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct)
    {
        _ = await TcpQueryAsync<EmeterRealtimeDto>(ip, "{\"emeter\":{\"get_realtime\":{}}}", "emeter", "get_realtime", ct);
        return new EmeterReading();
    }

    public async Task<IReadOnlyList<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct)
    {
        _ = await TcpQueryAsync<DayStatDto>(
            ip,
            $"{{\"emeter\":{{\"get_daystat\":{{\"month\":{month},\"year\":{year}}}}}}}",
            "emeter", "get_daystat", ct);
        return Array.Empty<DailyKwh>();
    }

    public async Task<IReadOnlyList<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct)
    {
        _ = await TcpQueryAsync<MonthStatDto>(
            ip,
            $"{{\"emeter\":{{\"get_monthstat\":{{\"year\":{year}}}}}}}",
            "emeter", "get_monthstat", ct);
        return Array.Empty<MonthlyKwh>();
    }

    public async IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(
        IReadOnlyList<IPAddress> broadcastAddresses,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Full implementation lands in Chunk 2 (Task 2.x: KasaClient.Udp.cs partial).
        await Task.CompletedTask;
        yield break;
    }

    private async Task<T> TcpQueryAsync<T>(
        IPAddress ip,
        string requestJson,
        string outerKey,
        string innerKey,
        CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeout);
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(ip, _port, cts.Token);
            await using var stream = tcp.GetStream();

            var encoded = KasaWireProtocol.EncodeTcp(requestJson);
            await stream.WriteAsync(encoded, cts.Token);

            var hdr = new byte[4];
            await stream.ReadExactlyAsync(hdr, cts.Token);
            int len = BinaryPrimitives.ReadInt32BigEndian(hdr);
            if (len < 0 || len > 1 << 20)
                throw new InvalidDataException($"Bad frame length {len}");

            var body = new byte[len];
            await stream.ReadExactlyAsync(body, cts.Token);

            var responseJson = Encoding.UTF8.GetString(KasaCodec.Decrypt(body));
            using var doc = JsonDocument.Parse(responseJson);
            var inner = doc.RootElement.GetProperty(outerKey).GetProperty(innerKey);
            return inner.Deserialize<T>()
                ?? throw new InvalidDataException($"Null inner payload for {outerKey}.{innerKey}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Kasa query to {ip}:{_port} timed out after {_timeout.TotalMilliseconds:0} ms.");
        }
    }
}
```

- [ ] **Step 5: Run tests**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~KasaClient"
```

Expected: 2 passed.

- [ ] **Step 6: Run the full Core test suite**

```bash
dotnet test tests/KasaMonitor.Core.Tests
```

Expected: 22 passed, 0 failed.

Tally: KasaCodec = 7 (4 facts + 3 theory rows on `RoundTrip_PreservesAnyAsciiInput`), KasaWireProtocol = 8, DtoMapping = 5, KasaClient = 2.

- [ ] **Step 7: Commit**

```bash
git add src/KasaMonitor.Core/Protocol/IKasaClient.cs src/KasaMonitor.Core/Protocol/KasaClient.cs tests/KasaMonitor.Core.Tests/Protocol/KasaClientTests.cs
git commit -m "feat(core): implement KasaClient TCP query with timeout (sysinfo/emeter/daystat/monstat scaffold)"
```

### Task 1.8: Chunk-1 verification gate

- [ ] **Step 1: Build entire solution**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings (because of `TreatWarningsAsErrors`).

- [ ] **Step 2: Run all tests**

```bash
dotnet test
```

Expected: 22 passed, 0 failed.

- [ ] **Step 3: Confirm there is a clean working tree**

```bash
git status
```

Expected: "nothing to commit, working tree clean".

- [ ] **Step 4: Review commit log**

```bash
git log --oneline
```

Expected (newest first):
- `feat(core): implement KasaClient TCP query …`
- `feat(core): add Kasa DTOs with fixture-based mapping tests`
- `feat(core): implement Kasa TCP/UDP framing with safety cap`
- `feat(core): implement KasaCodec XOR cipher with round-trip tests`
- `test: add KP115 JSON payload fixtures captured from real plugs`
- `chore: scaffold solution with Core + Core.Tests projects`
- `chore: add gitignore, dockerignore, and shared MSBuild props`
- `Refine spec: …` (pre-existing)
- `Add KasaMonitor design spec …` (pre-existing)

---

## Chunk 2: Complete Core — real models, mapping, costing, abstractions, UDP discovery

This chunk replaces every Chunk-1 stub and finishes the Core project. After this chunk, `KasaMonitor.Core` exposes the full domain surface (device models + persisted projections + abstractions) and the `KasaClient` can do TCP queries that return real domain objects plus UDP broadcast discovery. No `Data`, `Workers`, or `Web` project yet — those land in later chunks.

**Expected commit count:** 6. **Expected test count after Chunk 2:** ~38 (22 from Chunk 1 + 6 new mapping tests + 4 TariffCalculator + 6 UDP discovery).

### Task 2.1: Real Core models + KasaProtocolException

**Files:**
- Modify: `src/KasaMonitor.Core/Models/PlugInfo.cs` (replace stub with full record)
- Modify: `src/KasaMonitor.Core/Models/EmeterReading.cs`
- Modify: `src/KasaMonitor.Core/Models/DailyKwh.cs`
- Modify: `src/KasaMonitor.Core/Models/MonthlyKwh.cs`
- Modify: `src/KasaMonitor.Core/Models/DiscoveredPlug.cs`
- Create: `src/KasaMonitor.Core/Models/PlugLiveState.cs`
- Create: `src/KasaMonitor.Core/Protocol/KasaProtocolException.cs`

These are pure data records; no behavior to test directly. Round-trip and mapping behavior is covered by Task 2.2's tests.

- [ ] **Step 1: `PlugInfo.cs`** — overwrite with the device-shape projection used by `KasaClient.GetSysInfoAsync`. `Mac` is normalized (lowercase, no separators); `RelayOn` is a bool (the wire field is 0/1 int); `OnTime` is a `TimeSpan` (the wire field is seconds).

```csharp
namespace KasaMonitor.Core.Models;

public sealed record PlugInfo(
    string Mac,
    string Alias,
    string Model,
    string HwVer,
    string SwVer,
    string OemId,
    bool RelayOn,
    int RssiDbm,
    TimeSpan OnTime
);
```

- [ ] **Step 2: `EmeterReading.cs`** — overwrite. Units stay in the device's native milli-units; conversion to V/A/W happens in the UI layer.

```csharp
namespace KasaMonitor.Core.Models;

public sealed record EmeterReading(
    long PowerMw,
    long VoltageMv,
    long CurrentMa,
    long TotalWh
);
```

- [ ] **Step 3: `DailyKwh.cs`** — overwrite.

```csharp
namespace KasaMonitor.Core.Models;

public sealed record DailyKwh(int Year, int Month, int Day, long EnergyWh);
```

- [ ] **Step 4: `MonthlyKwh.cs`** — overwrite.

```csharp
namespace KasaMonitor.Core.Models;

public sealed record MonthlyKwh(int Year, int Month, long EnergyWh);
```

- [ ] **Step 5: `DiscoveredPlug.cs`** — overwrite. Bundles the source IP (where the UDP response came from) with the device-side `PlugInfo`.

```csharp
using System.Net;

namespace KasaMonitor.Core.Models;

public sealed record DiscoveredPlug(IPAddress IpAddress, PlugInfo Info);
```

- [ ] **Step 6: `PlugLiveState.cs`** — new file. The dashboard-facing record that combines the persisted `PlugId` with the latest reading. `LastReading` is nullable so a plug whose discovery just happened (no sample yet) can still appear with `IsOnline=false`.

```csharp
namespace KasaMonitor.Core.Models;

public sealed record PlugLiveState(
    Guid PlugId,
    string Mac,
    string Alias,
    bool IsOnline,
    EmeterReading? LastReading,
    DateTimeOffset LastSampleUtc
);
```

- [ ] **Step 7: `KasaProtocolException.cs`** — new file. Thrown by `KasaClient` mapping when the device returns `err_code != 0` or a payload field is structurally missing.

```csharp
namespace KasaMonitor.Core.Protocol;

public sealed class KasaProtocolException : Exception
{
    public int ErrCode { get; }
    public string? Operation { get; }

    public KasaProtocolException(string message, int errCode = 0, string? operation = null)
        : base(message)
    {
        ErrCode = errCode;
        Operation = operation;
    }
}
```

- [ ] **Step 8: Build and ensure existing tests still pass**

```bash
dotnet build
dotnet test
```

Expected: 0 errors / 0 warnings. 22 tests still pass (mapping in Task 2.2 hasn't changed yet, so the Chunk-1 tests that returned `new PlugInfo()` etc. still pass — but wait: the `new PlugInfo()` call in `KasaClient.GetSysInfoAsync` no longer compiles because `PlugInfo` no longer has a parameterless constructor. **You must temporarily make `KasaClient` compile by replacing those calls with `new PlugInfo("","","","","","",false,0,TimeSpan.Zero)` etc., or by suppressing them as `null!` returns.** This is intentional — Task 2.2 immediately follows and replaces every one of those calls with real mappings, so the placeholder lives for at most one commit.

The cleanest path is to keep this task tightly scoped to "data shapes only" and absorb the placeholder churn in one commit, then 2.2 wipes it.

- [ ] **Step 9: Patch `KasaClient` placeholders so the solution compiles**

Edit `src/KasaMonitor.Core/Protocol/KasaClient.cs` to replace each `return new XxxModel();` with a sentinel that compiles:

```csharp
public async Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct)
{
    _ = await TcpQueryAsync<SysInfoDto>(ip, "{\"system\":{\"get_sysinfo\":{}}}", "system", "get_sysinfo", ct);
    // TODO(Task 2.2): map dto to PlugInfo. Sentinel keeps Chunk-2 Task 2.1 compilable.
    return new PlugInfo(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, string.Empty, false, 0, TimeSpan.Zero);
}

public async Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct)
{
    _ = await TcpQueryAsync<EmeterRealtimeDto>(ip, "{\"emeter\":{\"get_realtime\":{}}}", "emeter", "get_realtime", ct);
    return new EmeterReading(0, 0, 0, 0);
}
```

The `GetDayStatAsync` and `GetMonthStatAsync` methods already return `Array.Empty<...>()`, which still compiles — no change needed.

The two existing `KasaClient` tests assert only `result.Should().NotBeNull()` for sysinfo and a `TimeoutException` for the timeout case. Both still hold against the sentinel.

- [ ] **Step 10: Run all tests**

```bash
dotnet test
```

Expected: 22 passed.

- [ ] **Step 11: Commit**

```bash
git add src/KasaMonitor.Core/Models src/KasaMonitor.Core/Protocol/KasaProtocolException.cs src/KasaMonitor.Core/Protocol/KasaClient.cs
git commit -m "feat(core): real domain models + KasaProtocolException (KasaClient mapping pending)"
```

### Task 2.2: DTO→Model mapping in KasaClient (TDD)

**Files:**
- Test: `tests/KasaMonitor.Core.Tests/Protocol/KasaClientMappingTests.cs` (new)
- Modify: `tests/KasaMonitor.Core.Tests/Protocol/KasaClientTests.cs` (strengthen existing test's assertion)
- Modify: `src/KasaMonitor.Core/Protocol/KasaClient.cs`

This task ends the Task-2.1 placeholder by mapping every `*Dto` to its domain model inside `KasaClient`. MAC normalization happens here. `err_code != 0` from any DTO causes `KasaProtocolException`.

- [ ] **Step 1: Write the new mapping tests** — file `tests/KasaMonitor.Core.Tests/Protocol/KasaClientMappingTests.cs`. Each test reuses the loopback `TcpListener` helper pattern from `KasaClientTests`. Extract the helper into a shared `LoopbackTestServer` class to keep the new file readable.

First, refactor: create a small helper file `tests/KasaMonitor.Core.Tests/Protocol/LoopbackTestServer.cs`:

```csharp
using System.Net;
using System.Net.Sockets;

namespace KasaMonitor.Core.Tests.Protocol;

internal static class LoopbackTestServer
{
    public static (TcpListener listener, IPEndPoint endpoint) Start()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return (l, (IPEndPoint)l.LocalEndpoint);
    }

    public static async Task<string> ReceiveRequestAsync(TcpListener listener, string responseJson)
    {
        using var tcp = await listener.AcceptTcpClientAsync();
        await using var stream = tcp.GetStream();

        var hdr = new byte[4];
        await stream.ReadExactlyAsync(hdr);
        int len = (hdr[0] << 24) | (hdr[1] << 16) | (hdr[2] << 8) | hdr[3];
        var body = new byte[len];
        await stream.ReadExactlyAsync(body);

        var requestJson = KasaWireProtocol.DecodeTcp(hdr.Concat(body).ToArray());

        var resp = KasaWireProtocol.EncodeTcp(responseJson);
        await stream.WriteAsync(resp);
        return requestJson;
    }
}
```

Then write the mapping tests in `KasaClientMappingTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Net;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaClientMappingTests
{
    private static string WrapSysInfo(string innerJson) => $"{{\"system\":{{\"get_sysinfo\":{innerJson}}}}}";
    private static string WrapEmeter(string method, string innerJson) => $"{{\"emeter\":{{\"{method}\":{innerJson}}}}}";

    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public async Task GetSysInfoAsync_MapsDtoToDomainModel_NormalizingMac()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapSysInfo(LoadFixture("sysinfo_kp115_birou.json")));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var info = await client.GetSysInfoAsync(IPAddress.Loopback, CancellationToken.None);
            await serverTask;

            info.Mac.Should().Be("c006c3702bfc"); // lowercase, separators stripped
            info.Alias.Should().Be("Birou");
            info.Model.Should().StartWith("KP115");
            info.RelayOn.Should().BeTrue();        // wire was 1
            info.RssiDbm.Should().BeNegative();
            info.OnTime.Should().BeGreaterThan(TimeSpan.Zero);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetRealtimeAsync_MapsDtoToDomainModel()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapEmeter("get_realtime", LoadFixture("emeter_kp115.json")));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var reading = await client.GetRealtimeAsync(IPAddress.Loopback, CancellationToken.None);
            await serverTask;

            reading.PowerMw.Should().BeGreaterThan(0);
            reading.VoltageMv.Should().BeGreaterThan(200_000);
            reading.CurrentMa.Should().BeGreaterThan(0);
            reading.TotalWh.Should().BeGreaterOrEqualTo(0);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetRealtimeAsync_ThrowsKasaProtocolException_OnNonZeroErrCode()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapEmeter("get_realtime", "{\"voltage_mv\":0,\"current_ma\":0,\"power_mw\":0,\"total_wh\":0,\"err_code\":-1}"));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var act = async () => await client.GetRealtimeAsync(IPAddress.Loopback, CancellationToken.None);

            await act.Should().ThrowAsync<KasaProtocolException>().Where(e => e.ErrCode == -1);
            await serverTask;
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetDayStatAsync_MapsAllEntries()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapEmeter("get_daystat", LoadFixture("daystat_kp115.json")));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var days = await client.GetDayStatAsync(IPAddress.Loopback, 2026, 5, CancellationToken.None);
            await serverTask;

            days.Should().NotBeEmpty();
            days.Should().AllSatisfy(d =>
            {
                d.Year.Should().BeGreaterThan(2020);
                d.Day.Should().BeInRange(1, 31);
                d.EnergyWh.Should().BeGreaterOrEqualTo(0);
            });
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetMonthStatAsync_MapsAllEntries()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            var serverTask = LoopbackTestServer.ReceiveRequestAsync(
                listener, WrapEmeter("get_monthstat", LoadFixture("monstat_kp115.json")));

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            var months = await client.GetMonthStatAsync(IPAddress.Loopback, 2026, CancellationToken.None);
            await serverTask;

            months.Should().NotBeEmpty();
            months.Should().AllSatisfy(m => m.Month.Should().BeInRange(1, 12));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task GetSysInfoAsync_RequestPayloadAndMacNormalization_AreCorrect()
    {
        var (listener, endpoint) = LoopbackTestServer.Start();
        try
        {
            string? capturedRequest = null;
            var serverTask = Task.Run(async () =>
            {
                capturedRequest = await LoopbackTestServer.ReceiveRequestAsync(
                    listener, WrapSysInfo(LoadFixture("sysinfo_kp115_birou.json")));
            });

            var client = new KasaClient(TimeSpan.FromSeconds(2), portOverride: endpoint.Port);
            await client.GetSysInfoAsync(IPAddress.Loopback, CancellationToken.None);
            await serverTask;

            capturedRequest.Should().Contain("\"system\"").And.Contain("\"get_sysinfo\"");
        }
        finally { listener.Stop(); }
    }
}
```

That's 6 new tests.

- [ ] **Step 2: Update the existing `KasaClientTests.GetSysInfoAsync_SendsCorrectRequestAndDecodesResponse` to use real assertions**

Open `tests/KasaMonitor.Core.Tests/Protocol/KasaClientTests.cs` and replace the inner block of the first test so that `result.Should().NotBeNull()` becomes meaningful:

Replace:
```csharp
            await Task.WhenAll(clientTask, serverTask);
            var result = await clientTask;
            result.Should().NotBeNull();   // proper model assertions land in Chunk 2
```
with:
```csharp
            await Task.WhenAll(clientTask, serverTask);
            var result = await clientTask;
            result.Mac.Should().Be("c006c3702bfc");
            result.Alias.Should().Be("Birou");
```

- [ ] **Step 3: Run tests — expect failures**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~KasaClient"
```

Expected: 8 cases run (2 existing + 6 new), at least 6 fail because mapping is still placeholder. Existing timeout test still passes.

- [ ] **Step 4: Implement the mapping in `KasaClient.cs`**

Add a private helper region to `src/KasaMonitor.Core/Protocol/KasaClient.cs`. After the existing `using` block, ensure these are present:

```csharp
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Dtos;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
```

Replace the four query method bodies with real mapping. Add the `MapSysInfo`, `MapEmeter`, and `NormalizeMac` private static helpers below `TcpQueryAsync`:

```csharp
public async Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct)
{
    var dto = await TcpQueryAsync<SysInfoDto>(ip, "{\"system\":{\"get_sysinfo\":{}}}", "system", "get_sysinfo", ct);
    return MapSysInfo(dto);
}

public async Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct)
{
    var dto = await TcpQueryAsync<EmeterRealtimeDto>(ip, "{\"emeter\":{\"get_realtime\":{}}}", "emeter", "get_realtime", ct);
    if (dto.ErrCode != 0)
        throw new KasaProtocolException($"Device returned err_code {dto.ErrCode} for get_realtime.", dto.ErrCode, "get_realtime");
    return new EmeterReading(dto.PowerMw, dto.VoltageMv, dto.CurrentMa, dto.TotalWh);
}

public async Task<IReadOnlyList<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct)
{
    var dto = await TcpQueryAsync<DayStatDto>(
        ip,
        $"{{\"emeter\":{{\"get_daystat\":{{\"month\":{month},\"year\":{year}}}}}}}",
        "emeter", "get_daystat", ct);
    if (dto.ErrCode != 0)
        throw new KasaProtocolException($"Device returned err_code {dto.ErrCode} for get_daystat.", dto.ErrCode, "get_daystat");
    return dto.DayList.Select(d => new DailyKwh(d.Year, d.Month, d.Day, d.EnergyWh)).ToList();
}

public async Task<IReadOnlyList<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct)
{
    var dto = await TcpQueryAsync<MonthStatDto>(
        ip,
        $"{{\"emeter\":{{\"get_monthstat\":{{\"year\":{year}}}}}}}",
        "emeter", "get_monthstat", ct);
    if (dto.ErrCode != 0)
        throw new KasaProtocolException($"Device returned err_code {dto.ErrCode} for get_monthstat.", dto.ErrCode, "get_monthstat");
    return dto.MonthList.Select(m => new MonthlyKwh(m.Year, m.Month, m.EnergyWh)).ToList();
}
```

Add private helpers (placement: after `TcpQueryAsync`, before the closing brace of the class):

```csharp
private static PlugInfo MapSysInfo(SysInfoDto dto)
{
    var alias = dto.Alias ?? throw new KasaProtocolException("sysinfo missing alias", operation: "get_sysinfo");
    var mac   = NormalizeMac(dto.Mac ?? throw new KasaProtocolException("sysinfo missing mac", operation: "get_sysinfo"));
    return new PlugInfo(
        Mac:     mac,
        Alias:   alias,
        Model:   dto.Model ?? string.Empty,
        HwVer:   dto.HwVer ?? string.Empty,
        SwVer:   dto.SwVer ?? string.Empty,
        OemId:   dto.OemId ?? string.Empty,
        RelayOn: dto.RelayState != 0,
        RssiDbm: dto.Rssi,
        OnTime:  TimeSpan.FromSeconds(dto.OnTime));
}

private static string NormalizeMac(string raw) =>
    new string(raw.Where(c => c != ':' && c != '-').ToArray()).ToLowerInvariant();
```

- [ ] **Step 5: Run tests**

```bash
dotnet test
```

Expected: 28 passed (22 + 6 new mapping tests).

- [ ] **Step 6: Commit**

```bash
git add src/KasaMonitor.Core/Protocol/KasaClient.cs tests/KasaMonitor.Core.Tests/Protocol/KasaClientMappingTests.cs tests/KasaMonitor.Core.Tests/Protocol/KasaClientTests.cs tests/KasaMonitor.Core.Tests/Protocol/LoopbackTestServer.cs
git commit -m "feat(core): map Kasa DTOs to domain models in KasaClient with err_code handling"
```

### Task 2.3: TariffCalculator (Costing)

**Files:**
- Test: `tests/KasaMonitor.Core.Tests/Costing/TariffCalculatorTests.cs` (new)
- Create: `src/KasaMonitor.Core/Costing/TariffCalculator.cs`

Pure math. Cost = energy_kWh × rate_RON_per_kWh. Inputs validated.

- [ ] **Step 1: Failing tests** — `tests/KasaMonitor.Core.Tests/Costing/TariffCalculatorTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Costing;
using Xunit;

namespace KasaMonitor.Core.Tests.Costing;

public class TariffCalculatorTests
{
    [Fact]
    public void CostFromWh_SimpleCase_ProducesRonAtTwoDecimals()
    {
        var calc = new TariffCalculator(ronPerKwh: 1.30m);
        calc.CostFromWh(1000).Should().Be(1.30m);
    }

    [Fact]
    public void CostFromWh_FractionalKwh_ProducesProportionalRon()
    {
        var calc = new TariffCalculator(ronPerKwh: 1.30m);
        calc.CostFromWh(500).Should().Be(0.65m); // 0.5 kWh
    }

    [Fact]
    public void CostFromWh_ZeroEnergy_IsZero()
    {
        new TariffCalculator(1.30m).CostFromWh(0).Should().Be(0m);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-0.01)]
    public void Constructor_RejectsNonPositiveRate(decimal rate)
    {
        var act = () => new TariffCalculator(rate);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
```

- [ ] **Step 2: Run tests, expect failure**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~Tariff"
```

Expected: build fails (no `TariffCalculator`).

- [ ] **Step 3: Implement** — `src/KasaMonitor.Core/Costing/TariffCalculator.cs`:

```csharp
namespace KasaMonitor.Core.Costing;

public sealed class TariffCalculator
{
    private readonly decimal _ronPerKwh;

    public TariffCalculator(decimal ronPerKwh)
    {
        if (ronPerKwh <= 0)
            throw new ArgumentOutOfRangeException(nameof(ronPerKwh), ronPerKwh, "Tariff rate must be > 0 RON/kWh.");
        _ronPerKwh = ronPerKwh;
    }

    public decimal CostFromWh(long wh) => wh * _ronPerKwh / 1000m;
}
```

- [ ] **Step 4: Run, expect 5 passed (1 fact + 1 fact + 1 fact + theory with 2 inline rows = 5)**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~Tariff"
```

Expected: 5 passed.

- [ ] **Step 5: Commit**

```bash
git add src/KasaMonitor.Core/Costing tests/KasaMonitor.Core.Tests/Costing
git commit -m "feat(core): add TariffCalculator (RON per kWh)"
```

### Task 2.4: Persisted projections + Core abstractions

**Files:**
- Create: `src/KasaMonitor.Core/Models/Plug.cs`
- Create: `src/KasaMonitor.Core/Models/Sample.cs`
- Create: `src/KasaMonitor.Core/Abstractions/IPlugRepository.cs`
- Create: `src/KasaMonitor.Core/Abstractions/ISampleRepository.cs`
- Create: `src/KasaMonitor.Core/Abstractions/IHistoryRepository.cs`
- Create: `src/KasaMonitor.Core/Abstractions/ISettingsRepository.cs`
- Create: `src/KasaMonitor.Core/Abstractions/IPlugStateBroadcaster.cs`

`Plug` and `Sample` are persistence projections that the `Data` project (Chunk 3) will materialize from EF entities. Keeping them in `Core` lets repositories implement Core-declared interfaces (DIP).

No tests on these — they're shape-only. Compilation success + downstream usage in Chunk 3 covers them.

- [ ] **Step 1: `Plug.cs`**

```csharp
using System.Net;

namespace KasaMonitor.Core.Models;

public sealed record Plug(
    Guid Id,
    string Mac,
    IPAddress IpAddress,
    string Alias,
    bool AliasIsCustom,
    string Model,
    string HwVer,
    string SwVer,
    string OemId,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    bool IsArchived,
    string? Notes
);
```

- [ ] **Step 2: `Sample.cs`** — note `RelayOn` is nullable because old samples may have null when the device timed out.

```csharp
namespace KasaMonitor.Core.Models;

public sealed record Sample(
    long Id,
    Guid PlugId,
    DateTimeOffset TimestampUtc,
    bool Online,
    long? PowerMw,
    long? VoltageMv,
    long? CurrentMa,
    long? TotalWh,
    int? Rssi,
    bool? RelayOn,
    string? ErrorMessage
);
```

- [ ] **Step 3: `IPlugRepository.cs`**

```csharp
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface IPlugRepository
{
    Task<Guid> UpsertFromDiscoveryAsync(DiscoveredPlug discovered, DateTimeOffset utcNow, CancellationToken ct);
    Task UpdateLastSeenAsync(Guid plugId, DateTimeOffset utcNow, CancellationToken ct);
    Task<IReadOnlyList<Plug>> ListAllAsync(CancellationToken ct);
    Task<Plug?> GetByMacAsync(string mac, CancellationToken ct);
    Task SetAliasAsync(Guid plugId, string alias, bool isCustom, CancellationToken ct);
    Task SetArchivedAsync(Guid plugId, bool archived, CancellationToken ct);
}
```

- [ ] **Step 4: `ISampleRepository.cs`**

```csharp
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface ISampleRepository
{
    Task InsertAsync(Sample sample, CancellationToken ct);
    Task InsertManyAsync(IReadOnlyList<Sample> samples, CancellationToken ct);
    Task<IReadOnlyList<Sample>> ListByPlugAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);
    Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct);
}
```

- [ ] **Step 5: `IHistoryRepository.cs`**

```csharp
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface IHistoryRepository
{
    Task UpsertDailyAsync(Guid plugId, IReadOnlyList<DailyKwh> entries, CancellationToken ct);
    Task UpsertMonthlyAsync(Guid plugId, IReadOnlyList<MonthlyKwh> entries, CancellationToken ct);
    Task<IReadOnlyList<DailyKwh>> GetDailyRangeAsync(Guid plugId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct);
    Task<IReadOnlyList<MonthlyKwh>> GetMonthlyRangeAsync(Guid plugId, int yearFromInclusive, int yearToInclusive, CancellationToken ct);
}
```

- [ ] **Step 6: `ISettingsRepository.cs`**

```csharp
namespace KasaMonitor.Core.Abstractions;

public interface ISettingsRepository
{
    Task<string?> GetAsync(string key, CancellationToken ct);
    Task SetAsync(string key, string value, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct);
}
```

- [ ] **Step 7: `IPlugStateBroadcaster.cs`**

```csharp
using KasaMonitor.Core.Models;

namespace KasaMonitor.Core.Abstractions;

public interface IPlugStateBroadcaster
{
    void Publish(PlugLiveState state);
    IReadOnlyDictionary<Guid, PlugLiveState> Snapshot();
    event Action<PlugLiveState> StateChanged;
}
```

- [ ] **Step 8: Build + run tests (regression check)**

```bash
dotnet build
dotnet test
```

Expected: 0 errors / 0 warnings, 28 passed.

- [ ] **Step 9: Commit**

```bash
git add src/KasaMonitor.Core/Models/Plug.cs src/KasaMonitor.Core/Models/Sample.cs src/KasaMonitor.Core/Abstractions
git commit -m "feat(core): add persisted projections (Plug/Sample) and 5 repository abstractions"
```

### Task 2.5: KasaClient UDP discovery (`KasaClient.Udp.cs` partial)

**Files:**
- Modify: `src/KasaMonitor.Core/Protocol/KasaClient.cs` (add `partial` modifier)
- Create: `src/KasaMonitor.Core/Protocol/KasaClient.Udp.cs` (the discovery half)
- Test: `tests/KasaMonitor.Core.Tests/Protocol/KasaClientDiscoveryTests.cs`

The plan keeps each file ≤ 200 lines; splitting `KasaClient` into a TCP partial and a UDP partial avoids growth as discovery code lands.

`DiscoverAsync` sends a `get_sysinfo` UDP broadcast to each requested address, then listens for responses for the requested timeout. Each unique `(SourceIP, Mac)` reply yields one `DiscoveredPlug`. Duplicate replies (devices on multi-segment LANs sometimes echo) are deduped by Mac.

- [ ] **Step 1: Mark `KasaClient` as `partial`**

In `src/KasaMonitor.Core/Protocol/KasaClient.cs`, change the class declaration:

```csharp
public sealed class KasaClient : IKasaClient
```

to:

```csharp
public sealed partial class KasaClient : IKasaClient
```

Delete the existing stub `DiscoverAsync` method body entirely (it lives in the new partial file now). Remove the `using System.Runtime.CompilerServices;` line from this file if it's no longer needed (`[EnumeratorCancellation]` moves to the UDP partial).

- [ ] **Step 2: Write the failing tests** — `tests/KasaMonitor.Core.Tests/Protocol/KasaClientDiscoveryTests.cs`:

The test binds a UDP socket on a chosen loopback port, runs `DiscoverAsync` against `IPAddress.Loopback` on that port, and replies with a sysinfo payload.

```csharp
using FluentAssertions;
using KasaMonitor.Core.Protocol;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace KasaMonitor.Core.Tests.Protocol;

public class KasaClientDiscoveryTests
{
    private static string LoadFixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    private static (UdpClient server, int port) StartUdpServer()
    {
        var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return (udp, ((IPEndPoint)udp.Client.LocalEndPoint!).Port);
    }

    private static async Task RespondOnce(UdpClient server, string responsePayloadJson, CancellationToken ct)
    {
        var received = await server.ReceiveAsync(ct);
        var responseBytes = KasaWireProtocol.EncodeUdp(responsePayloadJson);
        await server.SendAsync(responseBytes, received.RemoteEndPoint, ct);
    }

    [Fact]
    public async Task DiscoverAsync_ReceivesAndMapsResponse()
    {
        var (server, port) = StartUdpServer();
        try
        {
            using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var serverTask = RespondOnce(
                server,
                $"{{\"system\":{{\"get_sysinfo\":{LoadFixture("sysinfo_kp115_birou.json")}}}}}",
                serverCts.Token);

            var client = new KasaClient(TimeSpan.FromMilliseconds(500), portOverride: port);
            var found = new List<KasaMonitor.Core.Models.DiscoveredPlug>();
            await foreach (var p in client.DiscoverAsync(
                new[] { IPAddress.Loopback }, TimeSpan.FromMilliseconds(800), CancellationToken.None))
            {
                found.Add(p);
            }
            await serverTask;

            found.Should().HaveCount(1);
            found[0].Info.Mac.Should().Be("c006c3702bfc");
            found[0].Info.Alias.Should().Be("Birou");
            found[0].IpAddress.Should().Be(IPAddress.Loopback);
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task DiscoverAsync_NoResponse_YieldsNothing_AndCompletes()
    {
        var (server, port) = StartUdpServer(); // server bound but never replies
        try
        {
            var client = new KasaClient(TimeSpan.FromMilliseconds(200), portOverride: port);
            var found = new List<KasaMonitor.Core.Models.DiscoveredPlug>();
            await foreach (var p in client.DiscoverAsync(
                new[] { IPAddress.Loopback }, TimeSpan.FromMilliseconds(300), CancellationToken.None))
            {
                found.Add(p);
            }
            found.Should().BeEmpty();
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task DiscoverAsync_DedupesByMac_WhenSamePlugRespondsTwice()
    {
        var (server, port) = StartUdpServer();
        try
        {
            using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var payload = $"{{\"system\":{{\"get_sysinfo\":{LoadFixture("sysinfo_kp115_birou.json")}}}}}";
            // Respond twice to the same caller endpoint.
            var serverTask = Task.Run(async () =>
            {
                var r1 = await server.ReceiveAsync(serverCts.Token);
                await server.SendAsync(KasaWireProtocol.EncodeUdp(payload), r1.RemoteEndPoint, serverCts.Token);
                await server.SendAsync(KasaWireProtocol.EncodeUdp(payload), r1.RemoteEndPoint, serverCts.Token);
            }, serverCts.Token);

            var client = new KasaClient(TimeSpan.FromMilliseconds(500), portOverride: port);
            var found = new List<KasaMonitor.Core.Models.DiscoveredPlug>();
            await foreach (var p in client.DiscoverAsync(
                new[] { IPAddress.Loopback }, TimeSpan.FromMilliseconds(800), CancellationToken.None))
            {
                found.Add(p);
            }
            await serverTask;

            found.Should().HaveCount(1); // duplicate Mac was deduped
        }
        finally { server.Dispose(); }
    }

    [Fact]
    public async Task DiscoverAsync_MultipleBroadcastAddresses_AllSent()
    {
        // Bind two servers on different loopback ports? Easier: bind one and verify
        // it receives two requests when client is given two broadcast addresses
        // both pointing at IPAddress.Loopback.
        var (server, port) = StartUdpServer();
        try
        {
            using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            int requestCount = 0;
            var serverTask = Task.Run(async () =>
            {
                while (!serverCts.IsCancellationRequested)
                {
                    try
                    {
                        var r = await server.ReceiveAsync(serverCts.Token);
                        Interlocked.Increment(ref requestCount);
                    }
                    catch (OperationCanceledException) { return; }
                }
            }, serverCts.Token);

            var client = new KasaClient(TimeSpan.FromMilliseconds(300), portOverride: port);
            var addrs = new[] { IPAddress.Loopback, IPAddress.Loopback };
            await foreach (var _ in client.DiscoverAsync(addrs, TimeSpan.FromMilliseconds(400), CancellationToken.None))
            { /* drain */ }

            // Give the receiver a beat to count any in-flight datagrams, then cancel.
            await Task.Delay(50);
            serverCts.Cancel();
            try { await serverTask; } catch { /* swallow */ }

            requestCount.Should().BeGreaterOrEqualTo(2);
        }
        finally { server.Dispose(); }
    }
}
```

- [ ] **Step 3: Run tests, expect failure**

```bash
dotnet test tests/KasaMonitor.Core.Tests --filter "FullyQualifiedName~KasaClientDiscovery"
```

Expected: build fails (no implementation of `DiscoverAsync` body, since Step 1 deleted the stub).

- [ ] **Step 4: Implement `KasaClient.Udp.cs`** — `src/KasaMonitor.Core/Protocol/KasaClient.Udp.cs`:

```csharp
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol.Dtos;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace KasaMonitor.Core.Protocol;

public sealed partial class KasaClient
{
    public async IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(
        IReadOnlyList<IPAddress> broadcastAddresses,
        TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (broadcastAddresses.Count == 0) yield break;

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var request = KasaWireProtocol.EncodeUdp("{\"system\":{\"get_sysinfo\":{}}}");
        foreach (var addr in broadcastAddresses)
            await udp.SendAsync(request, new IPEndPoint(addr, _port), ct);

        var seenMacs = new HashSet<string>(StringComparer.Ordinal);
        using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadlineCts.CancelAfter(timeout);

        while (!deadlineCts.IsCancellationRequested)
        {
            UdpReceiveResult result;
            try
            {
                result = await udp.ReceiveAsync(deadlineCts.Token);
            }
            catch (OperationCanceledException) { yield break; }
            catch (SocketException) { continue; }

            DiscoveredPlug? mapped;
            try
            {
                mapped = TryParseDiscoveryResponse(result);
            }
            catch (JsonException) { continue; }
            catch (KasaProtocolException) { continue; }

            if (mapped is null) continue;
            if (!seenMacs.Add(mapped.Info.Mac)) continue;
            yield return mapped;
        }
    }

    private static DiscoveredPlug? TryParseDiscoveryResponse(UdpReceiveResult result)
    {
        var responseJson = KasaWireProtocol.DecodeUdp(result.Buffer);
        using var doc = JsonDocument.Parse(responseJson);
        if (!doc.RootElement.TryGetProperty("system", out var system) ||
            !system.TryGetProperty("get_sysinfo", out var inner))
            return null;
        var dto = inner.Deserialize<SysInfoDto>();
        if (dto is null) return null;
        return new DiscoveredPlug(result.RemoteEndPoint.Address, MapSysInfo(dto));
    }
}
```

Note: `MapSysInfo` was defined as `private static` in the TCP partial in Task 2.2 — both partials are the same class, so `KasaClient.Udp.cs` can call it.

- [ ] **Step 5: Run tests**

```bash
dotnet test tests/KasaMonitor.Core.Tests
```

Expected: 32 passed (28 from before + 4 discovery tests).

- [ ] **Step 6: Commit**

```bash
git add src/KasaMonitor.Core/Protocol/KasaClient.cs src/KasaMonitor.Core/Protocol/KasaClient.Udp.cs tests/KasaMonitor.Core.Tests/Protocol/KasaClientDiscoveryTests.cs
git commit -m "feat(core): implement Kasa UDP discovery with mac-based dedupe"
```

### Task 2.6: Chunk-2 verification gate

- [ ] **Step 1: Build whole solution clean**

```bash
dotnet build
```

Expected: 0 errors, 0 warnings.

- [ ] **Step 2: Run all tests**

```bash
dotnet test
```

Expected: **32 passed, 0 failed.** Tally: KasaCodec 7 + KasaWireProtocol 8 + DtoMapping 5 + KasaClient (existing) 2 + KasaClientMapping 6 + Tariff 5 + KasaClientDiscovery 4 = 37? Recount:

  - KasaCodec: 7
  - KasaWireProtocol: 8
  - DtoMapping: 5
  - KasaClient (legacy): 2
  - KasaClientMapping (new): 6
  - TariffCalculator: 5
  - KasaClientDiscovery: 4
  - **Total: 37**

If `dotnet test` reports 37 passed, proceed. If a different number, stop and audit.

- [ ] **Step 3: Confirm clean working tree**

```bash
git status
```

Expected: "nothing to commit, working tree clean".

- [ ] **Step 4: Review commit log**

```bash
git log --oneline
```

Expected (newest first, after Chunk 2):
- `feat(core): implement Kasa UDP discovery with mac-based dedupe`
- `feat(core): add persisted projections (Plug/Sample) and 5 repository abstractions`
- `feat(core): add TariffCalculator (RON per kWh)`
- `feat(core): map Kasa DTOs to domain models in KasaClient with err_code handling`
- `feat(core): real domain models + KasaProtocolException (KasaClient mapping pending)`
- … (Chunk 1 commits below)

---

## Chunk 3: Data layer — `KasaMonitor.Data` project, EF Core SQLite, repositories

This chunk adds persistence. By the end:
- `KasaMonitor.Data` and `KasaMonitor.Data.Tests` projects exist in the solution.
- EF Core 10 + SQLite, with WAL pragmas applied at startup.
- 7 EF entities mirror the spec's data model.
- An initial EF migration produces the schema.
- 4 repositories implement the Core abstractions: `PlugRepository`, `SampleRepository`, `HistoryRepository`, `SettingsRepository`.
- Test infrastructure uses `Microsoft.Data.Sqlite` with a shared in-memory connection per test (cache=Shared) so EF migrations apply against a real SQLite engine.

**Expected commit count:** 9. **Expected test count after Chunk 3:** ~65 (37 + ~28 from 4 repo test suites + 1 schema test).

### Task 3.1: Scaffold `KasaMonitor.Data` + `KasaMonitor.Data.Tests`

**Files:**
- Create: `src/KasaMonitor.Data/KasaMonitor.Data.csproj`
- Create: `tests/KasaMonitor.Data.Tests/KasaMonitor.Data.Tests.csproj`

- [ ] **Step 1:** From repo root, create the Data class library:

```bash
dotnet new classlib -n KasaMonitor.Data -o src/KasaMonitor.Data -f net10.0
rm src/KasaMonitor.Data/Class1.cs
```

- [ ] **Step 2:** Replace `src/KasaMonitor.Data/KasaMonitor.Data.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>KasaMonitor.Data</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="10.*">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\KasaMonitor.Core\KasaMonitor.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3:** Create the test project:

```bash
dotnet new xunit -n KasaMonitor.Data.Tests -o tests/KasaMonitor.Data.Tests -f net10.0
rm tests/KasaMonitor.Data.Tests/UnitTest1.cs
```

- [ ] **Step 4:** Replace `tests/KasaMonitor.Data.Tests/KasaMonitor.Data.Tests.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <RootNamespace>KasaMonitor.Data.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\KasaMonitor.Data\KasaMonitor.Data.csproj" />
    <ProjectReference Include="..\..\src\KasaMonitor.Core\KasaMonitor.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 5:** Add both projects to the solution:

```bash
dotnet sln add src/KasaMonitor.Data/KasaMonitor.Data.csproj
dotnet sln add tests/KasaMonitor.Data.Tests/KasaMonitor.Data.Tests.csproj
```

- [ ] **Step 6:** Build (`dotnet build`) — expect 0 errors / 0 warnings; NuGet restore pulls EF Core 10.

- [ ] **Step 7:** Tests (`dotnet test`) — expect 37 passed (Core) + 0 from Data (no tests yet), so 37 total.

- [ ] **Step 8:** Commit:

```bash
git add KasaMonitor.sln src/KasaMonitor.Data tests/KasaMonitor.Data.Tests
git commit -m "chore: scaffold KasaMonitor.Data + KasaMonitor.Data.Tests projects"
```

### Task 3.2: EF entities

**Files:** all under `src/KasaMonitor.Data/Entities/`:
- `PlugEntity.cs`, `SampleEntity.cs`, `OneMinuteAggregateEntity.cs`, `OneHourAggregateEntity.cs`, `DailyEnergyEntity.cs`, `MonthlyEnergyEntity.cs`, `SettingEntity.cs`

These are POCO classes (NOT records — EF Core handles records but the convention for entities is plain classes with init properties). Naming uses `*Entity` suffix to disambiguate from `Plug`/`Sample` domain projections in `Core.Models`.

- [ ] **Step 1: `PlugEntity.cs`**

```csharp
namespace KasaMonitor.Data.Entities;

public sealed class PlugEntity
{
    public Guid Id { get; set; }
    public string Mac { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string Alias { get; set; } = "";
    public bool AliasIsCustom { get; set; }
    public string Model { get; set; } = "";
    public string HwVer { get; set; } = "";
    public string SwVer { get; set; } = "";
    public string OemId { get; set; } = "";
    public DateTimeOffset FirstSeenUtc { get; set; }
    public DateTimeOffset LastSeenUtc { get; set; }
    public bool IsArchived { get; set; }
    public string? Notes { get; set; }
}
```

- [ ] **Step 2: `SampleEntity.cs`**

```csharp
namespace KasaMonitor.Data.Entities;

public sealed class SampleEntity
{
    public long Id { get; set; }
    public Guid PlugId { get; set; }
    public DateTimeOffset TimestampUtc { get; set; }
    public bool Online { get; set; }
    public long? PowerMw { get; set; }
    public long? VoltageMv { get; set; }
    public long? CurrentMa { get; set; }
    public long? TotalWh { get; set; }
    public int? Rssi { get; set; }
    public bool? RelayOn { get; set; }
    public string? ErrorMessage { get; set; }
}
```

- [ ] **Step 3: `OneMinuteAggregateEntity.cs`**

```csharp
namespace KasaMonitor.Data.Entities;

public sealed class OneMinuteAggregateEntity
{
    public Guid PlugId { get; set; }
    public DateTimeOffset BucketStartUtc { get; set; }
    public long PAvgMw { get; set; }
    public long PMaxMw { get; set; }
    public long PMinMw { get; set; }
    public int SampleCount { get; set; }
    public int OnlineCount { get; set; }
}
```

- [ ] **Step 4: `OneHourAggregateEntity.cs`**

```csharp
namespace KasaMonitor.Data.Entities;

public sealed class OneHourAggregateEntity
{
    public Guid PlugId { get; set; }
    public DateTimeOffset BucketStartUtc { get; set; }
    public long PAvgMw { get; set; }
    public long PMaxMw { get; set; }
    public long EnergyWh { get; set; }
    public int SampleCount { get; set; }
}
```

- [ ] **Step 5: `DailyEnergyEntity.cs`**

```csharp
namespace KasaMonitor.Data.Entities;

public sealed class DailyEnergyEntity
{
    public Guid PlugId { get; set; }
    public string Date { get; set; } = ""; // ISO YYYY-MM-DD, local
    public long EnergyWh { get; set; }
}
```

- [ ] **Step 6: `MonthlyEnergyEntity.cs`**

```csharp
namespace KasaMonitor.Data.Entities;

public sealed class MonthlyEnergyEntity
{
    public Guid PlugId { get; set; }
    public int Year { get; set; }
    public int Month { get; set; }
    public long EnergyWh { get; set; }
}
```

- [ ] **Step 7: `SettingEntity.cs`**

```csharp
namespace KasaMonitor.Data.Entities;

public sealed class SettingEntity
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
```

- [ ] **Step 8: Build, no new tests yet**

```bash
dotnet build
```

Expected: 0 errors / 0 warnings. (No tests for entities — they're POCOs; behavior is in DbContext + repos.)

- [ ] **Step 9: Commit**

```bash
git add src/KasaMonitor.Data/Entities
git commit -m "feat(data): add 7 EF entities mirroring spec data model"
```

### Task 3.3: `KasaDbContext` + WAL pragmas

**Files:**
- Create: `src/KasaMonitor.Data/KasaDbContext.cs`
- Create: `tests/KasaMonitor.Data.Tests/TestDbFactory.cs`
- Create: `tests/KasaMonitor.Data.Tests/KasaDbContextTests.cs`

- [ ] **Step 1: `KasaDbContext.cs`** — `src/KasaMonitor.Data/KasaDbContext.cs`:

```csharp
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data;

public sealed class KasaDbContext : DbContext
{
    public DbSet<PlugEntity> Plugs => Set<PlugEntity>();
    public DbSet<SampleEntity> Samples => Set<SampleEntity>();
    public DbSet<OneMinuteAggregateEntity> OneMinuteAggregates => Set<OneMinuteAggregateEntity>();
    public DbSet<OneHourAggregateEntity> OneHourAggregates => Set<OneHourAggregateEntity>();
    public DbSet<DailyEnergyEntity> DailyEnergies => Set<DailyEnergyEntity>();
    public DbSet<MonthlyEnergyEntity> MonthlyEnergies => Set<MonthlyEnergyEntity>();
    public DbSet<SettingEntity> Settings => Set<SettingEntity>();

    public KasaDbContext(DbContextOptions<KasaDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<PlugEntity>(e =>
        {
            e.ToTable("Plugs");
            e.HasKey(p => p.Id);
            e.HasIndex(p => p.Mac).IsUnique();
        });

        mb.Entity<SampleEntity>(e =>
        {
            e.ToTable("Samples");
            e.HasKey(s => s.Id);
            e.Property(s => s.Id).ValueGeneratedOnAdd();
            e.HasIndex(s => new { s.PlugId, s.TimestampUtc })
                .IsDescending(false, true)
                .HasDatabaseName("idx_samples_plug_time");
        });

        mb.Entity<OneMinuteAggregateEntity>(e =>
        {
            e.ToTable("OneMinuteAggregates");
            e.HasKey(a => new { a.PlugId, a.BucketStartUtc });
        });

        mb.Entity<OneHourAggregateEntity>(e =>
        {
            e.ToTable("OneHourAggregates");
            e.HasKey(a => new { a.PlugId, a.BucketStartUtc });
        });

        mb.Entity<DailyEnergyEntity>(e =>
        {
            e.ToTable("DailyEnergy");
            e.HasKey(d => new { d.PlugId, d.Date });
        });

        mb.Entity<MonthlyEnergyEntity>(e =>
        {
            e.ToTable("MonthlyEnergy");
            e.HasKey(m => new { m.PlugId, m.Year, m.Month });
        });

        mb.Entity<SettingEntity>(e =>
        {
            e.ToTable("Settings");
            e.HasKey(s => s.Key);
        });
    }
}
```

- [ ] **Step 2: `TestDbFactory.cs`** — shared SQLite in-memory connection helper:

```csharp
using KasaMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Tests;

internal sealed class TestDbFactory : IDisposable
{
    private readonly SqliteConnection _conn;

    public TestDbFactory()
    {
        _conn = new SqliteConnection("Filename=:memory:");
        _conn.Open();
        using var ctx = CreateContext();
        ctx.Database.EnsureCreated();
    }

    public KasaDbContext CreateContext()
    {
        var opts = new DbContextOptionsBuilder<KasaDbContext>()
            .UseSqlite(_conn)
            .Options;
        return new KasaDbContext(opts);
    }

    public void Dispose() => _conn.Dispose();
}
```

- [ ] **Step 3: `KasaDbContextTests.cs`** — minimal smoke test:

```csharp
using FluentAssertions;
using KasaMonitor.Data.Entities;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class KasaDbContextTests
{
    [Fact]
    public void EnsureCreated_BuildsAllTables()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();

        var tables = ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name").ToList();

        tables.Should().Contain(new[]
        {
            "DailyEnergy", "MonthlyEnergy", "OneHourAggregates", "OneMinuteAggregates",
            "Plugs", "Samples", "Settings"
        });
    }

    [Fact]
    public void InsertAndReadPlug_RoundTrips()
    {
        using var f = new TestDbFactory();
        using (var ctx = f.CreateContext())
        {
            ctx.Plugs.Add(new PlugEntity
            {
                Id = Guid.NewGuid(),
                Mac = "c006c3702bfc",
                IpAddress = "192.168.254.105",
                Alias = "Birou",
                Model = "KP115(EU)",
                HwVer = "1.0",
                SwVer = "1.0.20 Build 221125 Rel.092759",
                OemId = "X",
                FirstSeenUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow
            });
            ctx.SaveChanges();
        }

        using (var ctx = f.CreateContext())
        {
            var plug = ctx.Plugs.Single();
            plug.Mac.Should().Be("c006c3702bfc");
            plug.Alias.Should().Be("Birou");
        }
    }
}
```

- [ ] **Step 4: Build + run tests**

```bash
dotnet build
dotnet test
```

Expected: 0 errors. Total tests: 39 (37 + 2 new).

- [ ] **Step 5: Commit**

```bash
git add src/KasaMonitor.Data/KasaDbContext.cs tests/KasaMonitor.Data.Tests/TestDbFactory.cs tests/KasaMonitor.Data.Tests/KasaDbContextTests.cs
git commit -m "feat(data): add KasaDbContext with composite keys + indexes; in-memory test factory"
```

### Task 3.4: Initial migration + schema test

EF migrations are generated via `dotnet ef`. Output goes to `src/KasaMonitor.Data/Migrations/`. The migration is checked into git.

- [ ] **Step 1: Generate the migration**

From repo root:

```bash
dotnet ef migrations add InitialCreate \
    --project src/KasaMonitor.Data/KasaMonitor.Data.csproj \
    --startup-project src/KasaMonitor.Data/KasaMonitor.Data.csproj \
    -o Migrations
```

This produces 3 files under `src/KasaMonitor.Data/Migrations/`:
- `<timestamp>_InitialCreate.cs` (the Up/Down migration)
- `<timestamp>_InitialCreate.Designer.cs`
- `KasaDbContextModelSnapshot.cs`

The migration creates 7 tables matching `OnModelCreating`. The `dotnet ef` tool requires `Microsoft.EntityFrameworkCore.Design` package, which the csproj already includes.

**Note:** because `KasaDbContext` only has a single ctor `(DbContextOptions<KasaDbContext>)` and not a parameterless one, `dotnet ef` would normally need a design-time factory. However for `dotnet ef migrations add` (non-runtime), EF can build the model from `OnModelCreating` without instantiating the context against a real DB. If `dotnet ef` complains about no design-time factory, add `tests/KasaMonitor.Data.Tests/` is already a fall back consumer — but the simpler fix is the design-time factory:

If needed, create `src/KasaMonitor.Data/DesignTimeKasaDbContextFactory.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace KasaMonitor.Data;

internal sealed class DesignTimeKasaDbContextFactory : IDesignTimeDbContextFactory<KasaDbContext>
{
    public KasaDbContext CreateDbContext(string[] args)
    {
        var opts = new DbContextOptionsBuilder<KasaDbContext>()
            .UseSqlite("Filename=:memory:")
            .Options;
        return new KasaDbContext(opts);
    }
}
```

Then re-run the `dotnet ef migrations add` command. The internal class only exists for tooling — at runtime nothing references it.

- [ ] **Step 2: Verify migration applies in tests**

Add `tests/KasaMonitor.Data.Tests/MigrationTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class MigrationTests
{
    [Fact]
    public void Migrate_ProducesExpectedSchema()
    {
        using var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();

        var opts = new DbContextOptionsBuilder<KasaDbContext>().UseSqlite(conn).Options;
        using var ctx = new KasaDbContext(opts);
        ctx.Database.Migrate();

        var tables = ctx.Database.SqlQueryRaw<string>(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT LIKE '__EFMigrationsHistory' ORDER BY name").ToList();

        tables.Should().Contain(new[]
        {
            "DailyEnergy", "MonthlyEnergy", "OneHourAggregates", "OneMinuteAggregates",
            "Plugs", "Samples", "Settings"
        });
    }
}
```

Note: this differs from `KasaDbContextTests.EnsureCreated_BuildsAllTables` because `Migrate()` runs the actual checked-in migration SQL, while `EnsureCreated()` builds from the model. Both should produce the same schema; testing both catches drift.

- [ ] **Step 3: Build + test**

```bash
dotnet build
dotnet test
```

Expected: 0 errors. Total tests: 40.

- [ ] **Step 4: Commit**

```bash
git add src/KasaMonitor.Data/Migrations src/KasaMonitor.Data/DesignTimeKasaDbContextFactory.cs tests/KasaMonitor.Data.Tests/MigrationTests.cs
git commit -m "feat(data): add InitialCreate EF migration with schema verification test"
```

If `DesignTimeKasaDbContextFactory.cs` was not needed (depends on EF tool version), omit it from `git add`.

### Task 3.5: `PlugRepository` (TDD)

**Files:**
- Test: `tests/KasaMonitor.Data.Tests/PlugRepositoryTests.cs`
- Create: `src/KasaMonitor.Data/Repositories/PlugRepository.cs`

`PlugRepository` implements `IPlugRepository` by translating `Plug` and `DiscoveredPlug` domain projections to/from `PlugEntity`. UPSERT on Mac (the unique business key).

- [ ] **Step 1: Write tests** — `tests/KasaMonitor.Data.Tests/PlugRepositoryTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using System.Net;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class PlugRepositoryTests
{
    private static DiscoveredPlug MakeDiscovered(string mac = "c006c3702bfc", string alias = "Birou", string ip = "192.168.254.105") =>
        new(IPAddress.Parse(ip), new PlugInfo(
            Mac: mac, Alias: alias, Model: "KP115(EU)", HwVer: "1.0", SwVer: "1.0.20", OemId: "X",
            RelayOn: true, RssiDbm: -53, OnTime: TimeSpan.FromMinutes(10)));

    [Fact]
    public async Task UpsertFromDiscovery_NewPlug_InsertsRowAndReturnsId()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var now = DateTimeOffset.UtcNow;

        var id = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(), now, CancellationToken.None);

        id.Should().NotBe(Guid.Empty);
        var plug = (await repo.ListAllAsync(CancellationToken.None)).Single();
        plug.Mac.Should().Be("c006c3702bfc");
        plug.FirstSeenUtc.Should().Be(now);
        plug.LastSeenUtc.Should().Be(now);
    }

    [Fact]
    public async Task UpsertFromDiscovery_ExistingPlug_UpdatesIpAndLastSeen_PreservesId()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);

        var t0 = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var firstId = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(ip: "192.168.254.105"), t0, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-08T10:30:00Z");
        var secondId = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(ip: "192.168.254.106"), t1, CancellationToken.None);

        secondId.Should().Be(firstId);
        var plug = (await repo.ListAllAsync(CancellationToken.None)).Single();
        plug.IpAddress.ToString().Should().Be("192.168.254.106");
        plug.FirstSeenUtc.Should().Be(t0); // unchanged
        plug.LastSeenUtc.Should().Be(t1);
    }

    [Fact]
    public async Task UpsertFromDiscovery_DoesNotOverwriteCustomAlias()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var now = DateTimeOffset.UtcNow;

        var id = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(alias: "Birou"), now, CancellationToken.None);
        await repo.SetAliasAsync(id, "MyCustom", isCustom: true, CancellationToken.None);

        await repo.UpsertFromDiscoveryAsync(MakeDiscovered(alias: "DeviceAlias"), now, CancellationToken.None);

        var plug = (await repo.ListAllAsync(CancellationToken.None)).Single();
        plug.Alias.Should().Be("MyCustom");
        plug.AliasIsCustom.Should().BeTrue();
    }

    [Fact]
    public async Task GetByMac_ReturnsRow_WhenPresent()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        await repo.UpsertFromDiscoveryAsync(MakeDiscovered(), DateTimeOffset.UtcNow, CancellationToken.None);

        var plug = await repo.GetByMacAsync("c006c3702bfc", CancellationToken.None);
        plug.Should().NotBeNull();
        plug!.Alias.Should().Be("Birou");
    }

    [Fact]
    public async Task GetByMac_ReturnsNull_WhenAbsent()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);

        var plug = await repo.GetByMacAsync("ffffffffffff", CancellationToken.None);
        plug.Should().BeNull();
    }

    [Fact]
    public async Task SetArchived_FlipsFlag()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var id = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(), DateTimeOffset.UtcNow, CancellationToken.None);

        await repo.SetArchivedAsync(id, true, CancellationToken.None);
        (await repo.GetByMacAsync("c006c3702bfc", CancellationToken.None))!.IsArchived.Should().BeTrue();

        await repo.SetArchivedAsync(id, false, CancellationToken.None);
        (await repo.GetByMacAsync("c006c3702bfc", CancellationToken.None))!.IsArchived.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateLastSeen_TouchesTimestampOnly()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var t0 = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
        var id = await repo.UpsertFromDiscoveryAsync(MakeDiscovered(), t0, CancellationToken.None);

        var t1 = DateTimeOffset.Parse("2026-05-08T11:00:00Z");
        await repo.UpdateLastSeenAsync(id, t1, CancellationToken.None);

        var plug = (await repo.ListAllAsync(CancellationToken.None)).Single();
        plug.LastSeenUtc.Should().Be(t1);
        plug.FirstSeenUtc.Should().Be(t0);
    }
}
```

- [ ] **Step 2: Run tests** — expect compile failure (`PlugRepository` undefined).

- [ ] **Step 3: Implement** — `src/KasaMonitor.Data/Repositories/PlugRepository.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace KasaMonitor.Data.Repositories;

public sealed class PlugRepository : IPlugRepository
{
    private readonly KasaDbContext _db;
    public PlugRepository(KasaDbContext db) => _db = db;

    public async Task<Guid> UpsertFromDiscoveryAsync(DiscoveredPlug discovered, DateTimeOffset utcNow, CancellationToken ct)
    {
        var existing = await _db.Plugs.FirstOrDefaultAsync(p => p.Mac == discovered.Info.Mac, ct);
        if (existing is null)
        {
            var entity = new PlugEntity
            {
                Id = Guid.NewGuid(),
                Mac = discovered.Info.Mac,
                IpAddress = discovered.IpAddress.ToString(),
                Alias = discovered.Info.Alias,
                AliasIsCustom = false,
                Model = discovered.Info.Model,
                HwVer = discovered.Info.HwVer,
                SwVer = discovered.Info.SwVer,
                OemId = discovered.Info.OemId,
                FirstSeenUtc = utcNow,
                LastSeenUtc = utcNow,
                IsArchived = false
            };
            _db.Plugs.Add(entity);
            await _db.SaveChangesAsync(ct);
            return entity.Id;
        }

        existing.IpAddress = discovered.IpAddress.ToString();
        existing.Model = discovered.Info.Model;
        existing.HwVer = discovered.Info.HwVer;
        existing.SwVer = discovered.Info.SwVer;
        existing.OemId = discovered.Info.OemId;
        existing.LastSeenUtc = utcNow;
        if (!existing.AliasIsCustom)
            existing.Alias = discovered.Info.Alias;
        await _db.SaveChangesAsync(ct);
        return existing.Id;
    }

    public async Task UpdateLastSeenAsync(Guid plugId, DateTimeOffset utcNow, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return;
        existing.LastSeenUtc = utcNow;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Plug>> ListAllAsync(CancellationToken ct) =>
        await _db.Plugs.AsNoTracking().Select(p => Map(p)).ToListAsync(ct);

    public async Task<Plug?> GetByMacAsync(string mac, CancellationToken ct)
    {
        var e = await _db.Plugs.AsNoTracking().FirstOrDefaultAsync(p => p.Mac == mac, ct);
        return e is null ? null : Map(e);
    }

    public async Task SetAliasAsync(Guid plugId, string alias, bool isCustom, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return;
        existing.Alias = alias;
        existing.AliasIsCustom = isCustom;
        await _db.SaveChangesAsync(ct);
    }

    public async Task SetArchivedAsync(Guid plugId, bool archived, CancellationToken ct)
    {
        var existing = await _db.Plugs.FindAsync(new object?[] { plugId }, ct);
        if (existing is null) return;
        existing.IsArchived = archived;
        await _db.SaveChangesAsync(ct);
    }

    private static Plug Map(PlugEntity e) => new(
        Id: e.Id,
        Mac: e.Mac,
        IpAddress: IPAddress.TryParse(e.IpAddress, out var ip) ? ip : IPAddress.None,
        Alias: e.Alias,
        AliasIsCustom: e.AliasIsCustom,
        Model: e.Model,
        HwVer: e.HwVer,
        SwVer: e.SwVer,
        OemId: e.OemId,
        FirstSeenUtc: e.FirstSeenUtc,
        LastSeenUtc: e.LastSeenUtc,
        IsArchived: e.IsArchived,
        Notes: e.Notes);
}
```

- [ ] **Step 4: Run tests** — expect 47 passed (40 + 7 new). Tally for this task: 7 tests above.

- [ ] **Step 5: Commit**

```bash
git add src/KasaMonitor.Data/Repositories/PlugRepository.cs tests/KasaMonitor.Data.Tests/PlugRepositoryTests.cs
git commit -m "feat(data): implement PlugRepository with mac-keyed upsert and alias-custom preservation"
```

### Task 3.6: `SampleRepository` (TDD)

**Files:**
- Test: `tests/KasaMonitor.Data.Tests/SampleRepositoryTests.cs`
- Create: `src/KasaMonitor.Data/Repositories/SampleRepository.cs`

- [ ] **Step 1: Tests** — `tests/KasaMonitor.Data.Tests/SampleRepositoryTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class SampleRepositoryTests
{
    private static Sample MakeSample(Guid plugId, DateTimeOffset ts, long? power = 50_000) =>
        new(Id: 0, PlugId: plugId, TimestampUtc: ts, Online: power.HasValue,
            PowerMw: power, VoltageMv: 230_000, CurrentMa: 200, TotalWh: 100, Rssi: -50, RelayOn: true, ErrorMessage: null);

    [Fact]
    public async Task InsertAsync_RoundTripsAllFields()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var ts = DateTimeOffset.UtcNow;

        await repo.InsertAsync(MakeSample(plugId, ts), CancellationToken.None);

        var read = (await repo.ListByPlugAsync(plugId, ts.AddMinutes(-1), ts.AddMinutes(1), CancellationToken.None)).Single();
        read.PlugId.Should().Be(plugId);
        read.PowerMw.Should().Be(50_000);
    }

    [Fact]
    public async Task InsertManyAsync_BulkInsertsRows()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 5)
            .Select(i => MakeSample(plugId, t0.AddSeconds(i * 10)))
            .ToList();

        await repo.InsertManyAsync(samples, CancellationToken.None);

        var read = await repo.ListByPlugAsync(plugId, t0.AddSeconds(-1), t0.AddMinutes(1), CancellationToken.None);
        read.Should().HaveCount(5);
    }

    [Fact]
    public async Task ListByPlug_ScopesToTimeRange()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var t0 = DateTimeOffset.Parse("2026-05-08T10:00:00Z");

        await repo.InsertManyAsync(new[]
        {
            MakeSample(plugId, t0.AddMinutes(-10)),
            MakeSample(plugId, t0.AddMinutes(0)),
            MakeSample(plugId, t0.AddMinutes(10)),
            MakeSample(plugId, t0.AddMinutes(20)),
        }, CancellationToken.None);

        var read = await repo.ListByPlugAsync(plugId, t0, t0.AddMinutes(15), CancellationToken.None);
        read.Should().HaveCount(2); // t0 and t0+10
    }

    [Fact]
    public async Task DeleteOlderThan_RemovesOldRows_OnlyOldOnes()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var cutoff = DateTimeOffset.Parse("2026-05-08T00:00:00Z");

        await repo.InsertManyAsync(new[]
        {
            MakeSample(plugId, cutoff.AddDays(-10)),
            MakeSample(plugId, cutoff.AddDays(-1)),
            MakeSample(plugId, cutoff.AddDays(1)),
        }, CancellationToken.None);

        var deleted = await repo.DeleteOlderThanAsync(cutoff, CancellationToken.None);
        deleted.Should().Be(2);

        var remaining = await repo.ListByPlugAsync(plugId, cutoff.AddDays(-30), cutoff.AddDays(30), CancellationToken.None);
        remaining.Should().HaveCount(1);
    }
}
```

- [ ] **Step 2: Run, expect compile failure**

- [ ] **Step 3: Implement** — `src/KasaMonitor.Data/Repositories/SampleRepository.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class SampleRepository : ISampleRepository
{
    private readonly KasaDbContext _db;
    public SampleRepository(KasaDbContext db) => _db = db;

    public async Task InsertAsync(Sample sample, CancellationToken ct)
    {
        _db.Samples.Add(MapToEntity(sample));
        await _db.SaveChangesAsync(ct);
    }

    public async Task InsertManyAsync(IReadOnlyList<Sample> samples, CancellationToken ct)
    {
        foreach (var s in samples) _db.Samples.Add(MapToEntity(s));
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Sample>> ListByPlugAsync(Guid plugId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct) =>
        await _db.Samples
            .AsNoTracking()
            .Where(s => s.PlugId == plugId && s.TimestampUtc >= fromUtc && s.TimestampUtc <= toUtc)
            .OrderBy(s => s.TimestampUtc)
            .Select(s => Map(s))
            .ToListAsync(ct);

    public async Task<int> DeleteOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct)
    {
        return await _db.Samples
            .Where(s => s.TimestampUtc < cutoffUtc)
            .ExecuteDeleteAsync(ct);
    }

    private static SampleEntity MapToEntity(Sample s) => new()
    {
        PlugId = s.PlugId,
        TimestampUtc = s.TimestampUtc,
        Online = s.Online,
        PowerMw = s.PowerMw,
        VoltageMv = s.VoltageMv,
        CurrentMa = s.CurrentMa,
        TotalWh = s.TotalWh,
        Rssi = s.Rssi,
        RelayOn = s.RelayOn,
        ErrorMessage = s.ErrorMessage
    };

    private static Sample Map(SampleEntity e) => new(
        Id: e.Id, PlugId: e.PlugId, TimestampUtc: e.TimestampUtc, Online: e.Online,
        PowerMw: e.PowerMw, VoltageMv: e.VoltageMv, CurrentMa: e.CurrentMa, TotalWh: e.TotalWh,
        Rssi: e.Rssi, RelayOn: e.RelayOn, ErrorMessage: e.ErrorMessage);
}
```

- [ ] **Step 4: Tests pass** (51 total = 47 + 4).

- [ ] **Step 5: Commit**

```bash
git add src/KasaMonitor.Data/Repositories/SampleRepository.cs tests/KasaMonitor.Data.Tests/SampleRepositoryTests.cs
git commit -m "feat(data): implement SampleRepository (insert, range query, retention delete)"
```

### Task 3.7: `HistoryRepository` (TDD)

**Files:**
- Test: `tests/KasaMonitor.Data.Tests/HistoryRepositoryTests.cs`
- Create: `src/KasaMonitor.Data/Repositories/HistoryRepository.cs`

- [ ] **Step 1: Tests**:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class HistoryRepositoryTests
{
    [Fact]
    public async Task UpsertDaily_NewEntries_Inserts()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var plugId = Guid.NewGuid();

        await repo.UpsertDailyAsync(plugId, new[]
        {
            new DailyKwh(2026, 5, 1, 1500),
            new DailyKwh(2026, 5, 2, 1700),
        }, CancellationToken.None);

        var got = await repo.GetDailyRangeAsync(plugId,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 31), CancellationToken.None);
        got.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpsertDaily_OnDuplicateKey_Replaces()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var plugId = Guid.NewGuid();

        await repo.UpsertDailyAsync(plugId, new[] { new DailyKwh(2026, 5, 1, 1500) }, CancellationToken.None);
        await repo.UpsertDailyAsync(plugId, new[] { new DailyKwh(2026, 5, 1, 1900) }, CancellationToken.None);

        var got = await repo.GetDailyRangeAsync(plugId,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 1), CancellationToken.None);
        got.Single().EnergyWh.Should().Be(1900);
    }

    [Fact]
    public async Task UpsertMonthly_NewAndUpsert()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var plugId = Guid.NewGuid();

        await repo.UpsertMonthlyAsync(plugId, new[] { new MonthlyKwh(2026, 5, 50000) }, CancellationToken.None);
        await repo.UpsertMonthlyAsync(plugId, new[] { new MonthlyKwh(2026, 5, 60000), new MonthlyKwh(2026, 6, 1) }, CancellationToken.None);

        var got = await repo.GetMonthlyRangeAsync(plugId, 2026, 2026, CancellationToken.None);
        got.Should().HaveCount(2);
        got.Single(m => m.Month == 5).EnergyWh.Should().Be(60000);
    }

    [Fact]
    public async Task GetDailyRange_RespectsBounds()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new HistoryRepository(ctx);
        var plugId = Guid.NewGuid();
        await repo.UpsertDailyAsync(plugId, new[]
        {
            new DailyKwh(2026, 4, 30, 100),
            new DailyKwh(2026, 5, 1, 200),
            new DailyKwh(2026, 5, 2, 300),
            new DailyKwh(2026, 5, 3, 400),
        }, CancellationToken.None);

        var got = await repo.GetDailyRangeAsync(plugId,
            new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 2), CancellationToken.None);
        got.Should().HaveCount(2);
        got.Sum(d => d.EnergyWh).Should().Be(500);
    }
}
```

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement** — `src/KasaMonitor.Data/Repositories/HistoryRepository.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class HistoryRepository : IHistoryRepository
{
    private readonly KasaDbContext _db;
    public HistoryRepository(KasaDbContext db) => _db = db;

    public async Task UpsertDailyAsync(Guid plugId, IReadOnlyList<DailyKwh> entries, CancellationToken ct)
    {
        foreach (var d in entries)
        {
            var key = $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}";
            var existing = await _db.DailyEnergies.FindAsync(new object?[] { plugId, key }, ct);
            if (existing is null)
            {
                _db.DailyEnergies.Add(new DailyEnergyEntity { PlugId = plugId, Date = key, EnergyWh = d.EnergyWh });
            }
            else
            {
                existing.EnergyWh = d.EnergyWh;
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task UpsertMonthlyAsync(Guid plugId, IReadOnlyList<MonthlyKwh> entries, CancellationToken ct)
    {
        foreach (var m in entries)
        {
            var existing = await _db.MonthlyEnergies.FindAsync(new object?[] { plugId, m.Year, m.Month }, ct);
            if (existing is null)
            {
                _db.MonthlyEnergies.Add(new MonthlyEnergyEntity { PlugId = plugId, Year = m.Year, Month = m.Month, EnergyWh = m.EnergyWh });
            }
            else
            {
                existing.EnergyWh = m.EnergyWh;
            }
        }
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DailyKwh>> GetDailyRangeAsync(Guid plugId, DateOnly fromInclusive, DateOnly toInclusive, CancellationToken ct)
    {
        var fromKey = fromInclusive.ToString("yyyy-MM-dd");
        var toKey   = toInclusive.ToString("yyyy-MM-dd");
        var rows = await _db.DailyEnergies.AsNoTracking()
            .Where(d => d.PlugId == plugId && string.Compare(d.Date, fromKey) >= 0 && string.Compare(d.Date, toKey) <= 0)
            .OrderBy(d => d.Date)
            .ToListAsync(ct);
        return rows.Select(d =>
        {
            var dt = DateOnly.ParseExact(d.Date, "yyyy-MM-dd");
            return new DailyKwh(dt.Year, dt.Month, dt.Day, d.EnergyWh);
        }).ToList();
    }

    public async Task<IReadOnlyList<MonthlyKwh>> GetMonthlyRangeAsync(Guid plugId, int yearFromInclusive, int yearToInclusive, CancellationToken ct) =>
        await _db.MonthlyEnergies.AsNoTracking()
            .Where(m => m.PlugId == plugId && m.Year >= yearFromInclusive && m.Year <= yearToInclusive)
            .OrderBy(m => m.Year).ThenBy(m => m.Month)
            .Select(m => new MonthlyKwh(m.Year, m.Month, m.EnergyWh))
            .ToListAsync(ct);
}
```

- [ ] **Step 4: Tests pass** (55 total = 51 + 4).

- [ ] **Step 5: Commit**

```bash
git add src/KasaMonitor.Data/Repositories/HistoryRepository.cs tests/KasaMonitor.Data.Tests/HistoryRepositoryTests.cs
git commit -m "feat(data): implement HistoryRepository with daily/monthly UPSERT semantics"
```

### Task 3.8: `SettingsRepository` (TDD)

**Files:**
- Test: `tests/KasaMonitor.Data.Tests/SettingsRepositoryTests.cs`
- Create: `src/KasaMonitor.Data/Repositories/SettingsRepository.cs`

- [ ] **Step 1: Tests**:

```csharp
using FluentAssertions;
using KasaMonitor.Data.Repositories;
using Xunit;

namespace KasaMonitor.Data.Tests;

public class SettingsRepositoryTests
{
    [Fact]
    public async Task Get_AbsentKey_ReturnsNull()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SettingsRepository(ctx);
        (await repo.GetAsync("missing", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Set_NewKey_PersistsValue()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("Tariff", "1.30", CancellationToken.None);
        (await repo.GetAsync("Tariff", CancellationToken.None)).Should().Be("1.30");
    }

    [Fact]
    public async Task Set_ExistingKey_OverwritesValue()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("Tariff", "1.30", CancellationToken.None);
        await repo.SetAsync("Tariff", "1.45", CancellationToken.None);
        (await repo.GetAsync("Tariff", CancellationToken.None)).Should().Be("1.45");
    }

    [Fact]
    public async Task GetAll_ReturnsAllKeys()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var repo = new SettingsRepository(ctx);

        await repo.SetAsync("a", "1", CancellationToken.None);
        await repo.SetAsync("b", "2", CancellationToken.None);

        var all = await repo.GetAllAsync(CancellationToken.None);
        all.Should().ContainKey("a").WhoseValue.Should().Be("1");
        all.Should().ContainKey("b").WhoseValue.Should().Be("2");
    }
}
```

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement** — `src/KasaMonitor.Data/Repositories/SettingsRepository.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace KasaMonitor.Data.Repositories;

public sealed class SettingsRepository : ISettingsRepository
{
    private readonly KasaDbContext _db;
    public SettingsRepository(KasaDbContext db) => _db = db;

    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        var e = await _db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);
        return e?.Value;
    }

    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var e = await _db.Settings.FindAsync(new object?[] { key }, ct);
        if (e is null) _db.Settings.Add(new SettingEntity { Key = key, Value = value });
        else e.Value = value;
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, string>> GetAllAsync(CancellationToken ct)
    {
        var rows = await _db.Settings.AsNoTracking().ToListAsync(ct);
        return rows.ToDictionary(s => s.Key, s => s.Value);
    }
}
```

- [ ] **Step 4: Tests pass** (59 total = 55 + 4).

- [ ] **Step 5: Commit**

```bash
git add src/KasaMonitor.Data/Repositories/SettingsRepository.cs tests/KasaMonitor.Data.Tests/SettingsRepositoryTests.cs
git commit -m "feat(data): implement SettingsRepository (key-value store)"
```

### Task 3.9: Chunk-3 verification gate

- [ ] **Step 1: Build clean** — `dotnet build` → 0/0.
- [ ] **Step 2: All tests pass** — `dotnet test` → 59 passed (or close — recount: 37 Core + 2 DbContext + 1 Migration + 7 Plug + 4 Sample + 4 History + 4 Settings = 59).
- [ ] **Step 3: Working tree clean** — `git status`.
- [ ] **Step 4: Review log** — Chunk 3 added 9 commits.

---

## Chunk 4: Workers — `KasaMonitor.Workers` project, hosted services, in-memory broadcaster

This chunk adds the four background services that drive data collection and scheduled work, plus the in-memory `PlugStateBroadcaster` and a `PlugDiscoveredChannel` for cross-worker signaling. Each worker exposes a `RunOnceAsync` method so unit tests don't need a `Host`. `BackgroundService` integration is via a shared base class.

**Expected commit count:** 9. **Expected test count after Chunk 4:** ~80.

### Task 4.1: Scaffold `KasaMonitor.Workers` + `KasaMonitor.Workers.Tests`

**Files:**
- Create: `src/KasaMonitor.Workers/KasaMonitor.Workers.csproj`
- Create: `tests/KasaMonitor.Workers.Tests/KasaMonitor.Workers.Tests.csproj`

- [ ] Step 1: `dotnet new classlib -n KasaMonitor.Workers -o src/KasaMonitor.Workers -f net10.0` then delete `Class1.cs`.

- [ ] Step 2: Replace `src/KasaMonitor.Workers/KasaMonitor.Workers.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <RootNamespace>KasaMonitor.Workers</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.*" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\KasaMonitor.Core\KasaMonitor.Core.csproj" />
    <ProjectReference Include="..\KasaMonitor.Data\KasaMonitor.Data.csproj" />
  </ItemGroup>
</Project>
```

- [ ] Step 3: `dotnet new xunit -n KasaMonitor.Workers.Tests -o tests/KasaMonitor.Workers.Tests -f net10.0` then delete `UnitTest1.cs`.

- [ ] Step 4: Replace `tests/KasaMonitor.Workers.Tests/KasaMonitor.Workers.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
    <RootNamespace>KasaMonitor.Workers.Tests</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <PackageReference Include="FluentAssertions" Version="6.*" />
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.*" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\src\KasaMonitor.Workers\KasaMonitor.Workers.csproj" />
    <ProjectReference Include="..\..\src\KasaMonitor.Data\KasaMonitor.Data.csproj" />
    <ProjectReference Include="..\..\src\KasaMonitor.Core\KasaMonitor.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] Step 5: Add to solution + build + test (still 59 passed).

```bash
dotnet sln add src/KasaMonitor.Workers/KasaMonitor.Workers.csproj
dotnet sln add tests/KasaMonitor.Workers.Tests/KasaMonitor.Workers.Tests.csproj
dotnet build && dotnet test
```

- [ ] Step 6: Commit `chore: scaffold KasaMonitor.Workers + KasaMonitor.Workers.Tests projects`.

### Task 4.2: `PlugStateBroadcaster` (in-memory implementation)

**Files:**
- Create: `src/KasaMonitor.Workers/PlugStateBroadcaster.cs`
- Test: `tests/KasaMonitor.Workers.Tests/PlugStateBroadcasterTests.cs`

Simple thread-safe pub/sub. `Snapshot()` returns the latest known state per plug.

- [ ] Step 1: Tests:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Models;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class PlugStateBroadcasterTests
{
    private static PlugLiveState State(Guid id, string mac = "aa", bool online = true) =>
        new(PlugId: id, Mac: mac, Alias: "x", IsOnline: online,
            LastReading: null, LastSampleUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void Snapshot_AfterPublish_ReflectsLatest()
    {
        var b = new PlugStateBroadcaster();
        var id = Guid.NewGuid();
        b.Publish(State(id, online: false));
        b.Publish(State(id, online: true));

        b.Snapshot()[id].IsOnline.Should().BeTrue();
    }

    [Fact]
    public void StateChanged_FiresOnPublish()
    {
        var b = new PlugStateBroadcaster();
        var fired = new List<PlugLiveState>();
        b.StateChanged += s => fired.Add(s);

        var id = Guid.NewGuid();
        b.Publish(State(id));

        fired.Should().HaveCount(1).And.ContainSingle(s => s.PlugId == id);
    }

    [Fact]
    public void Publish_FromMultipleThreads_DoesNotCorrupt()
    {
        var b = new PlugStateBroadcaster();
        var ids = Enumerable.Range(0, 50).Select(_ => Guid.NewGuid()).ToList();

        Parallel.ForEach(ids, id => b.Publish(State(id)));

        b.Snapshot().Should().HaveCount(50);
    }
}
```

- [ ] Step 2: Implement `src/KasaMonitor.Workers/PlugStateBroadcaster.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using System.Collections.Concurrent;

namespace KasaMonitor.Workers;

public sealed class PlugStateBroadcaster : IPlugStateBroadcaster
{
    private readonly ConcurrentDictionary<Guid, PlugLiveState> _state = new();

    public event Action<PlugLiveState>? StateChanged;

    public void Publish(PlugLiveState state)
    {
        _state[state.PlugId] = state;
        StateChanged?.Invoke(state);
    }

    public IReadOnlyDictionary<Guid, PlugLiveState> Snapshot() =>
        new Dictionary<Guid, PlugLiveState>(_state);
}
```

- [ ] Step 3: Tests pass (62 total).

- [ ] Step 4: Commit `feat(workers): add in-memory PlugStateBroadcaster (concurrent snapshot + event)`.

### Task 4.3: `PlugDiscoveredChannel` for cross-worker signaling

**Files:**
- Create: `src/KasaMonitor.Core/Abstractions/IPlugDiscoveredChannel.cs`
- Create: `src/KasaMonitor.Workers/PlugDiscoveredChannel.cs`
- Test: `tests/KasaMonitor.Workers.Tests/PlugDiscoveredChannelTests.cs`

The interface lives in Core (DIP); the impl uses `System.Threading.Channels.Channel<Guid>` with unbounded capacity (writes never block, no backpressure for hobby load).

- [ ] Step 1: `IPlugDiscoveredChannel.cs`:

```csharp
namespace KasaMonitor.Core.Abstractions;

public interface IPlugDiscoveredChannel
{
    void Publish(Guid plugId);
    IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct);
}
```

- [ ] Step 2: Test:

```csharp
using FluentAssertions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class PlugDiscoveredChannelTests
{
    [Fact]
    public async Task Publish_ReadAll_ReceivesInOrder()
    {
        var ch = new PlugDiscoveredChannel();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        foreach (var id in ids) ch.Publish(id);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var seen = new List<Guid>();
        try
        {
            await foreach (var id in ch.ReadAllAsync(cts.Token))
            {
                seen.Add(id);
                if (seen.Count == ids.Length) break;
            }
        }
        catch (OperationCanceledException) { }

        seen.Should().Equal(ids);
    }
}
```

- [ ] Step 3: Implement `src/KasaMonitor.Workers/PlugDiscoveredChannel.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using System.Threading.Channels;

namespace KasaMonitor.Workers;

public sealed class PlugDiscoveredChannel : IPlugDiscoveredChannel
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public void Publish(Guid plugId) => _channel.Writer.TryWrite(plugId);

    public IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}
```

- [ ] Step 4: Tests pass (63 total).

- [ ] Step 5: Commit `feat(workers): add PlugDiscoveredChannel (unbounded in-memory queue)`.

### Task 4.4: `DiscoveryWorker`

**Files:**
- Create: `src/KasaMonitor.Workers/DiscoveryWorker.cs`
- Test: `tests/KasaMonitor.Workers.Tests/DiscoveryWorkerTests.cs`
- Test helper: `tests/KasaMonitor.Workers.Tests/FakeKasaClient.cs`

The worker has a public `RunOnceAsync(CancellationToken ct)` that does one discovery cycle (broadcast → upsert → publish events). The `BackgroundService.ExecuteAsync` override loops `RunOnceAsync` on a `_interval` cadence.

- [ ] Step 1: `FakeKasaClient.cs` — minimal `IKasaClient` test double:

```csharp
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using System.Net;
using System.Runtime.CompilerServices;

namespace KasaMonitor.Workers.Tests;

internal sealed class FakeKasaClient : IKasaClient
{
    public Func<IPAddress, Task<PlugInfo>>? OnGetSysInfo;
    public Func<IPAddress, Task<EmeterReading>>? OnGetRealtime;
    public Func<IPAddress, int, int, Task<IReadOnlyList<DailyKwh>>>? OnGetDayStat;
    public Func<IPAddress, int, Task<IReadOnlyList<MonthlyKwh>>>? OnGetMonthStat;
    public List<DiscoveredPlug> DiscoveredPlugs { get; } = new();

    public Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct) =>
        OnGetSysInfo?.Invoke(ip) ?? throw new InvalidOperationException("OnGetSysInfo not set");

    public Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct) =>
        OnGetRealtime?.Invoke(ip) ?? throw new InvalidOperationException("OnGetRealtime not set");

    public Task<IReadOnlyList<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct) =>
        OnGetDayStat?.Invoke(ip, year, month) ?? throw new InvalidOperationException("OnGetDayStat not set");

    public Task<IReadOnlyList<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct) =>
        OnGetMonthStat?.Invoke(ip, year) ?? throw new InvalidOperationException("OnGetMonthStat not set");

    public async IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(
        IReadOnlyList<IPAddress> broadcastAddresses, TimeSpan timeout,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var p in DiscoveredPlugs) yield return p;
        await Task.CompletedTask;
    }
}
```

- [ ] Step 2: Tests — `DiscoveryWorkerTests.cs`:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using KasaMonitor.Data.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class DiscoveryWorkerTests
{
    private static DiscoveredPlug Make(string mac, string alias = "X", string ip = "192.168.254.105") =>
        new(IPAddress.Parse(ip), new PlugInfo(mac, alias, "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));

    [Fact]
    public async Task RunOnceAsync_NewPlugs_AreUpserted_AndPublishedAsDiscovered()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var plugRepo = new PlugRepository(ctx);
        var fake = new FakeKasaClient();
        fake.DiscoveredPlugs.Add(Make("aa", ip: "192.168.254.105"));
        fake.DiscoveredPlugs.Add(Make("bb", ip: "192.168.254.106"));
        var channel = new PlugDiscoveredChannel();

        var worker = new DiscoveryWorker(
            fake, plugRepo, channel,
            new[] { IPAddress.Parse("192.168.254.255") },
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMinutes(5),
            NullLogger<DiscoveryWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        var plugs = await plugRepo.ListAllAsync(CancellationToken.None);
        plugs.Should().HaveCount(2);
    }

    [Fact]
    public async Task RunOnceAsync_AlreadyKnownPlug_NotPublishedAgain()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var plugRepo = new PlugRepository(ctx);
        await plugRepo.UpsertFromDiscoveryAsync(Make("aa"), DateTimeOffset.UtcNow, CancellationToken.None);

        var fake = new FakeKasaClient();
        fake.DiscoveredPlugs.Add(Make("aa"));
        var channel = new PlugDiscoveredChannel();

        var worker = new DiscoveryWorker(
            fake, plugRepo, channel,
            new[] { IPAddress.Parse("192.168.254.255") },
            TimeSpan.FromMilliseconds(500),
            TimeSpan.FromMinutes(5),
            NullLogger<DiscoveryWorker>.Instance);

        await worker.RunOnceAsync(CancellationToken.None);

        // Channel should be empty (no new discovery published)
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var got = new List<Guid>();
        try { await foreach (var id in channel.ReadAllAsync(cts.Token)) got.Add(id); }
        catch (OperationCanceledException) { }
        got.Should().BeEmpty();
    }
}
```

- [ ] Step 3: Implement `src/KasaMonitor.Workers/DiscoveryWorker.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Net;

namespace KasaMonitor.Workers;

public sealed class DiscoveryWorker : BackgroundService
{
    private readonly IKasaClient _kasa;
    private readonly IPlugRepository _plugRepo;
    private readonly IPlugDiscoveredChannel _channel;
    private readonly IReadOnlyList<IPAddress> _broadcastAddresses;
    private readonly TimeSpan _scanTimeout;
    private readonly TimeSpan _interval;
    private readonly ILogger<DiscoveryWorker> _log;

    public DiscoveryWorker(
        IKasaClient kasa,
        IPlugRepository plugRepo,
        IPlugDiscoveredChannel channel,
        IReadOnlyList<IPAddress> broadcastAddresses,
        TimeSpan scanTimeout,
        TimeSpan interval,
        ILogger<DiscoveryWorker> log)
    {
        _kasa = kasa;
        _plugRepo = plugRepo;
        _channel = channel;
        _broadcastAddresses = broadcastAddresses;
        _scanTimeout = scanTimeout;
        _interval = interval;
        _log = log;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existingMacs = (await _plugRepo.ListAllAsync(ct)).Select(p => p.Mac).ToHashSet(StringComparer.Ordinal);

        await foreach (var dp in _kasa.DiscoverAsync(_broadcastAddresses, _scanTimeout, ct))
        {
            var wasNew = !existingMacs.Contains(dp.Info.Mac);
            var id = await _plugRepo.UpsertFromDiscoveryAsync(dp, now, ct);
            if (wasNew)
            {
                _channel.Publish(id);
                _log.LogInformation("Discovered new plug {Mac} ({Alias}) -> Id {Id}", dp.Info.Mac, dp.Info.Alias, id);
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _log.LogError(ex, "Discovery cycle failed");
            }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
```

- [ ] Step 4: Tests pass (65 total).

- [ ] Step 5: Commit `feat(workers): implement DiscoveryWorker with new-plug event publication`.

### Task 4.5: `PollingWorker`

**Files:**
- Create: `src/KasaMonitor.Workers/PollingWorker.cs`
- Test: `tests/KasaMonitor.Workers.Tests/PollingWorkerTests.cs`

Per cycle: list active (non-archived) plugs → parallel `GetRealtimeAsync` → insert sample → publish state.

- [ ] Step 1: Tests — at least 2 cases:
  - Successful poll: 2 plugs both reachable, 2 samples inserted with correct fields, both states broadcast.
  - Failed poll: client throws → sample inserted with `Online=false, ErrorMessage=...`, state still broadcast.

```csharp
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using KasaMonitor.Data.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class PollingWorkerTests
{
    private static async Task<(Guid id, PlugRepository repo, KasaMonitor.Data.KasaDbContext ctx, TestDbFactory f)> SeedPlug(string mac, string ip = "192.168.254.105")
    {
        var f = new TestDbFactory();
        var ctx = f.CreateContext();
        var repo = new PlugRepository(ctx);
        var dp = new DiscoveredPlug(IPAddress.Parse(ip),
            new PlugInfo(mac, "X", "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));
        var id = await repo.UpsertFromDiscoveryAsync(dp, DateTimeOffset.UtcNow, CancellationToken.None);
        return (id, repo, ctx, f);
    }

    [Fact]
    public async Task RunOnceAsync_SuccessfulPoll_InsertsOnlineSample_AndBroadcasts()
    {
        var (id, plugRepo, ctx, f) = await SeedPlug("aa");
        using (f)
        {
            using var ctx2 = f.CreateContext();
            var sampleRepo = new SampleRepository(ctx2);
            var fake = new FakeKasaClient
            {
                OnGetRealtime = ip => Task.FromResult(new EmeterReading(50_000, 230_000, 200, 100))
            };
            var bc = new PlugStateBroadcaster();

            var worker = new PollingWorker(
                fake, () => f.CreateContext(),
                async ct =>
                {
                    using var c = f.CreateContext();
                    return await new PlugRepository(c).ListAllAsync(ct);
                },
                sampleRepo, bc,
                TimeSpan.FromSeconds(10),
                NullLogger<PollingWorker>.Instance);

            await worker.RunOnceAsync(CancellationToken.None);

            var samples = await sampleRepo.ListByPlugAsync(id,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None);
            samples.Should().HaveCount(1);
            samples[0].Online.Should().BeTrue();
            samples[0].PowerMw.Should().Be(50_000);

            bc.Snapshot().Should().ContainKey(id);
        }
    }

    [Fact]
    public async Task RunOnceAsync_PollFails_InsertsOfflineSampleWithErrorMessage()
    {
        var (id, plugRepo, ctx, f) = await SeedPlug("bb");
        using (f)
        {
            using var ctx2 = f.CreateContext();
            var sampleRepo = new SampleRepository(ctx2);
            var fake = new FakeKasaClient
            {
                OnGetRealtime = _ => Task.FromException<EmeterReading>(new TimeoutException("simulated"))
            };
            var bc = new PlugStateBroadcaster();

            var worker = new PollingWorker(
                fake, () => f.CreateContext(),
                async ct =>
                {
                    using var c = f.CreateContext();
                    return await new PlugRepository(c).ListAllAsync(ct);
                },
                sampleRepo, bc,
                TimeSpan.FromSeconds(10),
                NullLogger<PollingWorker>.Instance);

            await worker.RunOnceAsync(CancellationToken.None);

            var samples = await sampleRepo.ListByPlugAsync(id,
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(1),
                CancellationToken.None);
            samples.Single().Online.Should().BeFalse();
            samples.Single().ErrorMessage.Should().Contain("simulated");
        }
    }
}
```

- [ ] Step 2: Implement `src/KasaMonitor.Workers/PollingWorker.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Models;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public sealed class PollingWorker : BackgroundService
{
    private readonly IKasaClient _kasa;
    private readonly Func<KasaDbContext> _ctxFactory; // for IPlugRepository instantiation per cycle
    private readonly Func<CancellationToken, Task<IReadOnlyList<Plug>>> _listPlugs;
    private readonly ISampleRepository _samples;
    private readonly IPlugStateBroadcaster _broadcaster;
    private readonly TimeSpan _interval;
    private readonly ILogger<PollingWorker> _log;

    public PollingWorker(
        IKasaClient kasa,
        Func<KasaDbContext> ctxFactory,
        Func<CancellationToken, Task<IReadOnlyList<Plug>>> listPlugs,
        ISampleRepository samples,
        IPlugStateBroadcaster broadcaster,
        TimeSpan interval,
        ILogger<PollingWorker> log)
    {
        _kasa = kasa;
        _ctxFactory = ctxFactory;
        _listPlugs = listPlugs;
        _samples = samples;
        _broadcaster = broadcaster;
        _interval = interval;
        _log = log;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var plugs = (await _listPlugs(ct)).Where(p => !p.IsArchived).ToList();
        var now = DateTimeOffset.UtcNow;
        var samplesToInsert = new List<Sample>();

        await Parallel.ForEachAsync(plugs, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct },
            async (plug, innerCt) =>
            {
                Sample s;
                try
                {
                    var reading = await _kasa.GetRealtimeAsync(plug.IpAddress, innerCt);
                    s = new Sample(0, plug.Id, now, true,
                        reading.PowerMw, reading.VoltageMv, reading.CurrentMa, reading.TotalWh,
                        Rssi: null, RelayOn: null, ErrorMessage: null);
                    _broadcaster.Publish(new PlugLiveState(plug.Id, plug.Mac, plug.Alias, true, reading, now));
                }
                catch (Exception ex)
                {
                    s = new Sample(0, plug.Id, now, false,
                        null, null, null, null, null, null, ex.Message);
                    _broadcaster.Publish(new PlugLiveState(plug.Id, plug.Mac, plug.Alias, false, null, now));
                }
                lock (samplesToInsert) samplesToInsert.Add(s);
            });

        if (samplesToInsert.Count > 0)
            await _samples.InsertManyAsync(samplesToInsert, ct);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "Polling cycle failed"); }
            try { await Task.Delay(_interval, stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
```

- [ ] Step 3: Tests pass (67 total).
- [ ] Step 4: Commit `feat(workers): implement PollingWorker with parallel fan-out and offline-sample fallback`.

### Task 4.6: `HistorySyncWorker`

**Files:**
- Create: `src/KasaMonitor.Workers/HistorySyncWorker.cs`
- Test: `tests/KasaMonitor.Workers.Tests/HistorySyncWorkerTests.cs`

Per cycle (per plug): fetch monstat for current year + previous, fetch daystat for current month + previous. Upsert. Listens to `PlugDiscoveredChannel` for first-time fetch trigger.

For Chunk 4, scope this worker as a single `RunOnceAsync(plugs)` that does the fetch+upsert. Skip the channel-listening loop (kept in `ExecuteAsync` only).

- [ ] Step 1: Tests:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data.Repositories;
using KasaMonitor.Data.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class HistorySyncWorkerTests
{
    [Fact]
    public async Task SyncOneAsync_FetchesAndUpsertsDailyAndMonthly()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var plugRepo = new PlugRepository(ctx);
        var historyRepo = new HistoryRepository(ctx);
        var dp = new DiscoveredPlug(IPAddress.Parse("192.168.254.105"),
            new PlugInfo("aa", "X", "KP115(EU)", "1.0", "1.0.20", "X", true, -50, TimeSpan.Zero));
        var plugId = await plugRepo.UpsertFromDiscoveryAsync(dp, DateTimeOffset.UtcNow, CancellationToken.None);

        var fake = new FakeKasaClient
        {
            OnGetMonthStat = (_, year) => Task.FromResult<IReadOnlyList<MonthlyKwh>>(new[]
            {
                new MonthlyKwh(year, 1, 100), new MonthlyKwh(year, 2, 200)
            }),
            OnGetDayStat = (_, year, month) => Task.FromResult<IReadOnlyList<DailyKwh>>(new[]
            {
                new DailyKwh(year, month, 1, 50), new DailyKwh(year, month, 2, 60)
            })
        };

        var worker = new HistorySyncWorker(fake, plugRepo, historyRepo,
            DateTime.UtcNow, // injectable "now"
            NullLogger<HistorySyncWorker>.Instance);

        await worker.SyncOneAsync(plugId, CancellationToken.None);

        var thisYear = DateTime.UtcNow.Year;
        var monthly = await historyRepo.GetMonthlyRangeAsync(plugId, thisYear - 1, thisYear, CancellationToken.None);
        monthly.Should().NotBeEmpty();

        var thisMonth = DateTime.UtcNow.Month;
        var daily = await historyRepo.GetDailyRangeAsync(plugId,
            new DateOnly(thisYear, 1, 1), new DateOnly(thisYear, 12, 31), CancellationToken.None);
        daily.Should().NotBeEmpty();
    }
}
```

- [ ] Step 2: Implement `src/KasaMonitor.Workers/HistorySyncWorker.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public sealed class HistorySyncWorker : BackgroundService
{
    private readonly IKasaClient _kasa;
    private readonly IPlugRepository _plugRepo;
    private readonly IHistoryRepository _historyRepo;
    private readonly DateTime _now;
    private readonly ILogger<HistorySyncWorker> _log;

    public HistorySyncWorker(
        IKasaClient kasa,
        IPlugRepository plugRepo,
        IHistoryRepository historyRepo,
        DateTime now,
        ILogger<HistorySyncWorker> log)
    {
        _kasa = kasa;
        _plugRepo = plugRepo;
        _historyRepo = historyRepo;
        _now = now;
        _log = log;
    }

    public async Task SyncOneAsync(Guid plugId, CancellationToken ct)
    {
        var plug = (await _plugRepo.ListAllAsync(ct)).FirstOrDefault(p => p.Id == plugId);
        if (plug is null) return;

        try
        {
            var monthsThis = await _kasa.GetMonthStatAsync(plug.IpAddress, _now.Year, ct);
            var monthsPrev = await _kasa.GetMonthStatAsync(plug.IpAddress, _now.Year - 1, ct);
            await _historyRepo.UpsertMonthlyAsync(plugId, monthsThis, ct);
            await _historyRepo.UpsertMonthlyAsync(plugId, monthsPrev, ct);

            var daysThis = await _kasa.GetDayStatAsync(plug.IpAddress, _now.Year, _now.Month, ct);
            var prevMonth = _now.AddMonths(-1);
            var daysPrev = await _kasa.GetDayStatAsync(plug.IpAddress, prevMonth.Year, prevMonth.Month, ct);
            await _historyRepo.UpsertDailyAsync(plugId, daysThis, ct);
            await _historyRepo.UpsertDailyAsync(plugId, daysPrev, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "History sync failed for {PlugId}", plugId);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var plugs = await _plugRepo.ListAllAsync(stoppingToken);
                foreach (var p in plugs.Where(p => !p.IsArchived))
                    await SyncOneAsync(p.Id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "History sync cycle failed"); }
            try { await Task.Delay(TimeSpan.FromHours(6), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
```

- [ ] Step 3: Tests pass (68 total).
- [ ] Step 4: Commit `feat(workers): implement HistorySyncWorker (per-plug daystat/monstat upsert)`.

### Task 4.7: `RollupWorker` — sample → 1-minute aggregates (raw SQL)

**Files:**
- Create: `src/KasaMonitor.Workers/RollupWorker.cs`
- Test: `tests/KasaMonitor.Workers.Tests/RollupWorkerTests.cs`

Per the spec, Rollup is heavyweight (multi-stage aggregation). For Chunk 4, implement a minimum-viable version:
- `RollupSamplesToMinutesAsync(DateOnly day, CancellationToken)` — aggregates Samples in the given day's UTC range into `OneMinuteAggregates`.
- `PruneOldSamplesAsync(int retentionDays, CancellationToken)` — calls `ISampleRepository.DeleteOlderThanAsync`.

Skip 1-minute → 1-hour aggregation and the VACUUM step for now; they can be added in a later chunk if time-pressured.

- [ ] Step 1: Test:

```csharp
using FluentAssertions;
using KasaMonitor.Core.Models;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using KasaMonitor.Data.Repositories;
using KasaMonitor.Data.Tests;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class RollupWorkerTests
{
    [Fact]
    public async Task RollupSamplesToMinutesAsync_Aggregates_BySample_PerMinute()
    {
        using var f = new TestDbFactory();
        using var ctx = f.CreateContext();
        var sampleRepo = new SampleRepository(ctx);
        var plugId = Guid.NewGuid();
        var dayBase = DateTimeOffset.Parse("2026-05-08T00:00:00Z");

        var samples = new[]
        {
            new Sample(0, plugId, dayBase, true, 1000, null, null, null, null, null, null),
            new Sample(0, plugId, dayBase.AddSeconds(10), true, 2000, null, null, null, null, null, null),
            new Sample(0, plugId, dayBase.AddSeconds(20), true, 3000, null, null, null, null, null, null),
            // Different minute
            new Sample(0, plugId, dayBase.AddMinutes(1), true, 4000, null, null, null, null, null, null),
        };
        await sampleRepo.InsertManyAsync(samples, CancellationToken.None);

        var worker = new RollupWorker(() => f.CreateContext(), sampleRepo, NullLogger<RollupWorker>.Instance);
        await worker.RollupSamplesToMinutesAsync(new DateOnly(2026, 5, 8), CancellationToken.None);

        using var ctxRead = f.CreateContext();
        var aggs = ctxRead.OneMinuteAggregates.Where(a => a.PlugId == plugId).OrderBy(a => a.BucketStartUtc).ToList();
        aggs.Should().HaveCount(2);
        aggs[0].PAvgMw.Should().Be(2000); // (1000+2000+3000)/3
        aggs[0].PMaxMw.Should().Be(3000);
        aggs[0].PMinMw.Should().Be(1000);
        aggs[0].SampleCount.Should().Be(3);
    }
}
```

- [ ] Step 2: Implement `src/KasaMonitor.Workers/RollupWorker.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Data;
using KasaMonitor.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace KasaMonitor.Workers;

public sealed class RollupWorker : BackgroundService
{
    private readonly Func<KasaDbContext> _ctxFactory;
    private readonly ISampleRepository _samples;
    private readonly ILogger<RollupWorker> _log;

    public RollupWorker(Func<KasaDbContext> ctxFactory, ISampleRepository samples, ILogger<RollupWorker> log)
    {
        _ctxFactory = ctxFactory;
        _samples = samples;
        _log = log;
    }

    public async Task RollupSamplesToMinutesAsync(DateOnly day, CancellationToken ct)
    {
        var dayStart = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var dayEnd = dayStart.AddDays(1);
        var dayStartTicks = dayStart.UtcTicks;
        var dayEndTicks = dayEnd.UtcTicks;
        var ticksPerMinute = TimeSpan.TicksPerMinute;

        using var ctx = _ctxFactory();
        // Group via raw query: bucket by floor(UtcTicks / ticksPerMinute)
        var rows = await ctx.Samples.AsNoTracking()
            .Where(s => s.PowerMw != null
                        && s.TimestampUtc.UtcTicks >= dayStartTicks
                        && s.TimestampUtc.UtcTicks <  dayEndTicks)
            .ToListAsync(ct);

        var grouped = rows
            .GroupBy(s => new { s.PlugId, BucketTicks = s.TimestampUtc.UtcTicks / ticksPerMinute * ticksPerMinute })
            .Select(g => new OneMinuteAggregateEntity
            {
                PlugId = g.Key.PlugId,
                BucketStartUtc = new DateTimeOffset(g.Key.BucketTicks, TimeSpan.Zero),
                PAvgMw = (long)g.Average(s => s.PowerMw!.Value),
                PMaxMw = g.Max(s => s.PowerMw!.Value),
                PMinMw = g.Min(s => s.PowerMw!.Value),
                SampleCount = g.Count(),
                OnlineCount = g.Count(s => s.Online)
            })
            .ToList();

        foreach (var agg in grouped)
        {
            var existing = await ctx.OneMinuteAggregates.FindAsync(new object?[] { agg.PlugId, agg.BucketStartUtc }, ct);
            if (existing is null)
            {
                ctx.OneMinuteAggregates.Add(agg);
            }
            else
            {
                existing.PAvgMw = agg.PAvgMw;
                existing.PMaxMw = agg.PMaxMw;
                existing.PMinMw = agg.PMinMw;
                existing.SampleCount = agg.SampleCount;
                existing.OnlineCount = agg.OnlineCount;
            }
        }
        await ctx.SaveChangesAsync(ct);
    }

    public Task<int> PruneOldSamplesAsync(int retentionDays, CancellationToken ct) =>
        _samples.DeleteOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-retentionDays), ct);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1));
                await RollupSamplesToMinutesAsync(yesterday, stoppingToken);
                await PruneOldSamplesAsync(30, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested) { _log.LogError(ex, "Rollup cycle failed"); }
            try { await Task.Delay(TimeSpan.FromHours(24), stoppingToken); }
            catch (TaskCanceledException) { return; }
        }
    }
}
```

- [ ] Step 3: Tests pass (69 total).
- [ ] Step 4: Commit `feat(workers): implement RollupWorker minute aggregates + sample retention`.

### Task 4.8: DI extension `AddKasaWorkers`

**Files:**
- Create: `src/KasaMonitor.Workers/DependencyInjectionExtensions.cs`
- Test: `tests/KasaMonitor.Workers.Tests/DependencyInjectionExtensionsTests.cs`

- [ ] Step 1: Test — verifies the DI container can resolve every interface and worker.

```csharp
using FluentAssertions;
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace KasaMonitor.Workers.Tests;

public class DependencyInjectionExtensionsTests
{
    [Fact]
    public void AddKasaWorkers_RegistersAllAbstractions()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var conn = new SqliteConnection("Filename=:memory:");
        conn.Open();
        services.AddDbContext<KasaDbContext>(o => o.UseSqlite(conn));
        services.AddSingleton<IKasaClient, KasaClient>();
        services.AddKasaWorkers();

        var sp = services.BuildServiceProvider();

        sp.GetService<IPlugStateBroadcaster>().Should().NotBeNull();
        sp.GetService<IPlugDiscoveredChannel>().Should().NotBeNull();
        sp.GetService<IPlugRepository>().Should().NotBeNull();
        sp.GetService<ISampleRepository>().Should().NotBeNull();
        sp.GetService<IHistoryRepository>().Should().NotBeNull();
        sp.GetService<ISettingsRepository>().Should().NotBeNull();
    }
}
```

- [ ] Step 2: Implement:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Data.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace KasaMonitor.Workers;

public static class DependencyInjectionExtensions
{
    public static IServiceCollection AddKasaWorkers(this IServiceCollection services)
    {
        services.AddScoped<IPlugRepository, PlugRepository>();
        services.AddScoped<ISampleRepository, SampleRepository>();
        services.AddScoped<IHistoryRepository, HistoryRepository>();
        services.AddScoped<ISettingsRepository, SettingsRepository>();

        services.AddSingleton<IPlugStateBroadcaster, PlugStateBroadcaster>();
        services.AddSingleton<IPlugDiscoveredChannel, PlugDiscoveredChannel>();
        return services;
    }
}
```

(Hosted services — DiscoveryWorker, PollingWorker, etc. — require non-trivial constructor args and will be registered at the Web project level in Chunk 5 with `appsettings`-driven config.)

- [ ] Step 3: Tests pass (70 total).
- [ ] Step 4: Commit `feat(workers): add AddKasaWorkers DI extension for repositories + broadcaster + channel`.

### Task 4.9: Chunk-4 verification gate

- [ ] Step 1: `dotnet build` → 0/0.
- [ ] Step 2: `dotnet test` → ~70 passed.
- [ ] Step 3: `git status` clean, `git log --oneline | head -12` shows the 9 chunk-4 commits.

---

## Chunk 5: Blazor Web — `KasaMonitor.Web` project, live dashboard, health endpoint

This chunk delivers the **first user-visible build of the app** — a Blazor Server site that boots, applies migrations, runs all 4 background workers under `IHostedService`, and renders a live dashboard at `/` with one card per discovered plug. Cards push updates via the `IPlugStateBroadcaster.StateChanged` event (Blazor Server's native SignalR transports them to the browser).

Out of scope for Chunk 5: history page, plugs management, settings page (deferred to Chunk 6 or later).

**Expected commit count:** 7. **Expected test count after Chunk 5:** ~71 (1 small Razor render smoke test added; rest is manual UI verification).

### Task 5.1: Scaffold `KasaMonitor.Web` (Blazor Server)

- [ ] Step 1: `dotnet new blazor -n KasaMonitor.Web -o src/KasaMonitor.Web -f net10.0 --interactivity Server --use-program-main false --empty false`. Some flags may differ between dotnet versions; if `--empty` isn't supported, use the default template — Step 3 trims it.

- [ ] Step 2: Replace `src/KasaMonitor.Web/KasaMonitor.Web.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <RootNamespace>KasaMonitor.Web</RootNamespace>
    <UserSecretsId>kasamonitor-web</UserSecretsId>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="10.*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\KasaMonitor.Core\KasaMonitor.Core.csproj" />
    <ProjectReference Include="..\KasaMonitor.Data\KasaMonitor.Data.csproj" />
    <ProjectReference Include="..\KasaMonitor.Workers\KasaMonitor.Workers.csproj" />
  </ItemGroup>
</Project>
```

- [ ] Step 3: Trim the generated content. Keep only:
  - `Components/App.razor`
  - `Components/Routes.razor`
  - `Components/_Imports.razor`
  - `Components/Layout/MainLayout.razor`
  - `Components/Pages/Home.razor` → rename to `Components/Pages/Dashboard.razor`
  - `Components/Pages/Error.razor`
  - `wwwroot/` directory (delete `bootstrap/`, `app.css` blank-out)

  Delete generated `Components/Pages/Counter.razor`, `Components/Pages/Weather.razor`, the `Components/Layout/NavMenu.razor`'s extras, `Components/Layout/MainLayout.razor.css`, etc. — anything beyond bare scaffolding.

- [ ] Step 4: Add to solution and build:

```bash
dotnet sln add src/KasaMonitor.Web/KasaMonitor.Web.csproj
dotnet build
```

Expected: 0 errors / 0 warnings (the empty-template scaffolding may emit a few warnings under our strict settings; if so, suppress with the `<NoWarn>` of just those codes scoped to the Web csproj — for instance, CS8767 from `[HttpGet]`-style scaffolds).

- [ ] Step 5: Tests still pass (70).

- [ ] Step 6: Commit `chore: scaffold KasaMonitor.Web (Blazor Server, .NET 10)`.

### Task 5.2: `Program.cs` with full DI wiring + hosted services + migrate on start

`src/KasaMonitor.Web/Program.cs`:

```csharp
using KasaMonitor.Core.Abstractions;
using KasaMonitor.Core.Protocol;
using KasaMonitor.Data;
using KasaMonitor.Web.Components;
using KasaMonitor.Workers;
using Microsoft.EntityFrameworkCore;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var dbPath = builder.Configuration["KasaMonitor:DbPath"] ?? "kasa.db";
builder.Services.AddDbContext<KasaDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath};Cache=Shared"));

builder.Services.AddSingleton<IKasaClient, KasaClient>();
builder.Services.AddKasaWorkers();

var broadcastAddrs = (builder.Configuration["KasaMonitor:BroadcastNetworks"] ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
    .Select(IPAddress.Parse)
    .ToList();
if (broadcastAddrs.Count == 0) broadcastAddrs.Add(IPAddress.Broadcast);

var pollIntervalSec = builder.Configuration.GetValue("KasaMonitor:PollIntervalSeconds", 10);

builder.Services.AddSingleton<DiscoveryWorker>(sp => new DiscoveryWorker(
    sp.GetRequiredService<IKasaClient>(),
    sp.CreateScope().ServiceProvider.GetRequiredService<IPlugRepository>(),
    sp.GetRequiredService<IPlugDiscoveredChannel>(),
    broadcastAddrs,
    TimeSpan.FromSeconds(2.5),
    TimeSpan.FromMinutes(5),
    sp.GetRequiredService<ILogger<DiscoveryWorker>>()));
builder.Services.AddHostedService(sp => sp.GetRequiredService<DiscoveryWorker>());

builder.Services.AddSingleton<PollingWorker>(sp =>
{
    var ctxFactory = () => sp.CreateScope().ServiceProvider.GetRequiredService<KasaDbContext>();
    return new PollingWorker(
        sp.GetRequiredService<IKasaClient>(),
        ctxFactory,
        async ct =>
        {
            using var scope = sp.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IPlugRepository>().ListAllAsync(ct);
        },
        sp.CreateScope().ServiceProvider.GetRequiredService<ISampleRepository>(),
        sp.GetRequiredService<IPlugStateBroadcaster>(),
        TimeSpan.FromSeconds(pollIntervalSec),
        sp.GetRequiredService<ILogger<PollingWorker>>());
});
builder.Services.AddHostedService(sp => sp.GetRequiredService<PollingWorker>());

// (HistorySync + Rollup workers omitted for chunk-5 minimum; add later.)

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var ctx = scope.ServiceProvider.GetRequiredService<KasaDbContext>();
    ctx.Database.Migrate();
    // SQLite WAL mode for concurrent reader/writer
    ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    ctx.Database.ExecuteSqlRaw("PRAGMA busy_timeout=5000;");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapGet("/health", async (KasaDbContext ctx) =>
{
    try { await ctx.Plugs.AnyAsync(); return Results.Ok("healthy"); }
    catch (Exception ex) { return Results.Problem(ex.Message, statusCode: 503); }
});

app.Run();
```

- [ ] Step 1: Write `Program.cs` as above.
- [ ] Step 2: Build — confirm 0/0. Some scoped/singleton service-lifetime warnings may appear; the `sp.CreateScope()` pattern resolves them.
- [ ] Step 3: Commit `feat(web): wire DI, hosted services, migrate-on-startup, health endpoint`.

### Task 5.3: appsettings.json + Development overrides

- [ ] Step 1: `src/KasaMonitor.Web/appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  },
  "AllowedHosts": "*",
  "KasaMonitor": {
    "DbPath": "kasa.db",
    "BroadcastNetworks": "",
    "PollIntervalSeconds": 10,
    "TariffRonPerKwh": 1.30
  }
}
```

- [ ] Step 2: `src/KasaMonitor.Web/appsettings.Development.json` — same shape, can override BroadcastNetworks for local testing if needed.

- [ ] Step 3: Commit `chore(web): default appsettings with KasaMonitor section`.

### Task 5.4: Live dashboard page

`src/KasaMonitor.Web/Components/Pages/Dashboard.razor`:

```razor
@page "/"
@rendermode InteractiveServer
@using KasaMonitor.Core.Abstractions
@using KasaMonitor.Core.Models
@inject IPlugStateBroadcaster Broadcaster
@implements IDisposable

<PageTitle>KasaMonitor — Dashboard</PageTitle>

<h1>Plugs</h1>

@if (_states.Count == 0)
{
    <p><em>No plugs discovered yet — check broadcast network configuration and wait ~30s after startup.</em></p>
}
else
{
    <div class="plug-grid">
        @foreach (var s in _states.Values.OrderBy(s => s.Alias))
        {
            <div class="plug-card @(s.IsOnline ? "online" : "offline")">
                <h3>@s.Alias</h3>
                <p class="status">@(s.IsOnline ? "ONLINE" : "OFFLINE")</p>
                @if (s.LastReading is not null)
                {
                    <p>@(s.LastReading.PowerMw / 1000.0) W</p>
                    <p>@(s.LastReading.VoltageMv / 1000.0) V · @(s.LastReading.CurrentMa) mA</p>
                    <p>Total: @(s.LastReading.TotalWh / 1000.0) kWh</p>
                }
                <p class="age">last sample @(_now - s.LastSampleUtc).TotalSeconds:F0 s ago</p>
            </div>
        }
    </div>
}

<style>
.plug-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(220px, 1fr)); gap: 1em; }
.plug-card { border: 1px solid #ddd; border-radius: 6px; padding: 1em; }
.plug-card.online { border-left: 4px solid #4c4; }
.plug-card.offline { border-left: 4px solid #a33; opacity: 0.6; }
.plug-card .status { font-weight: bold; }
.plug-card .age { color: #888; font-size: 0.85em; }
</style>

@code {
    private Dictionary<Guid, PlugLiveState> _states = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private System.Threading.Timer? _refreshTimer;

    protected override void OnInitialized()
    {
        _states = Broadcaster.Snapshot().ToDictionary(kv => kv.Key, kv => kv.Value);
        Broadcaster.StateChanged += OnStateChanged;
        _refreshTimer = new System.Threading.Timer(_ => InvokeAsync(() => { _now = DateTimeOffset.UtcNow; StateHasChanged(); }), null, 1000, 1000);
    }

    private void OnStateChanged(PlugLiveState state)
    {
        InvokeAsync(() =>
        {
            _states[state.PlugId] = state;
            _now = DateTimeOffset.UtcNow;
            StateHasChanged();
        });
    }

    public void Dispose()
    {
        Broadcaster.StateChanged -= OnStateChanged;
        _refreshTimer?.Dispose();
    }
}
```

- [ ] Step 1: Write the Dashboard.razor file (replacing the generated `Home.razor` if you didn't rename it earlier).
- [ ] Step 2: Update `Components/Routes.razor` if needed to route `/` to `Dashboard.razor`.
- [ ] Step 3: Build clean.
- [ ] Step 4: Commit `feat(web): live dashboard with one card per plug + auto-refresh on state change`.

### Task 5.5: Smoke test the running app

- [ ] Step 1: From repo root: `dotnet run --project src/KasaMonitor.Web`. Wait for startup logs.

- [ ] Step 2: In another terminal (or browser): `curl -s http://localhost:5000/health` should return `"healthy"` (port may differ — the launchSettings.json from the template will pick something like 5000 or 5001/5002).

- [ ] Step 3: Open `http://localhost:5000/` in a browser. The dashboard should render with the "No plugs discovered yet" message (no real plugs unless the dev machine is on `192.168.254.0/24`).

- [ ] Step 4: Stop the app (`Ctrl+C`).

- [ ] Step 5: If a SQLite file `kasa.db` was created in the project root, that's normal — it's gitignored (`.db` pattern in `.gitignore`).

- [ ] Step 6: No commit if everything works as-is. If any tweaks were needed (port conflicts, missing static files, missing `Routes.razor` entry), fix and commit `fix(web): ...`.

### Task 5.6: Chunk-5 verification gate

- [ ] Step 1: `dotnet build` → 0/0.
- [ ] Step 2: `dotnet test` → 70+ passed (Web has no automated tests yet).
- [ ] Step 3: `git status` clean.
- [ ] Step 4: Manual smoke from Task 5.5 succeeded — health endpoint OK, dashboard renders.

---

## Chunk 6: Deployment-ready — wire all workers, cost display, healthcheck CLI, Docker

This chunk closes the gap between "Blazor renders live data" (end of Chunk 5) and "container running on QNAP via Portainer" (the spec's deployment target). It does NOT add the `/history`, `/plugs`, or `/settings` pages — those are scoped to Chunk 7.

**Expected commit count:** 5. **Expected test count after Chunk 6:** 70 (no new tests; manual smoke verifies Docker build + run).

### Task 6.1: Wire HistorySync + Rollup workers + total-power cost display

**Files:**
- Modify: `src/KasaMonitor.Web/Program.cs` — register `HistorySyncWorker` + `RollupWorker` as hosted services; expose `TariffCalculator` via DI.
- Modify: `src/KasaMonitor.Web/Components/Pages/Dashboard.razor` — add a top bar with total instant W + hourly cost.

- [ ] Step 1: In `Program.cs`, after the existing `AddHostedService` calls, register the two missing workers:

```csharp
builder.Services.AddHostedService(sp => new HistorySyncWorker(
    sp.GetRequiredService<IKasaClient>(),
    sp.GetRequiredService<IServiceScopeFactory>(),
    () => DateTime.UtcNow,
    sp.GetRequiredService<ILogger<HistorySyncWorker>>()));

builder.Services.AddHostedService(sp => new RollupWorker(
    sp.GetRequiredService<IServiceScopeFactory>(),
    sp.GetRequiredService<ILogger<RollupWorker>>()));
```

**Note:** `RollupWorker`'s current constructor takes `Func<KasaDbContext>` and `ISampleRepository`. Like the previous worker refactor, change it to take `IServiceScopeFactory` (consistency with Discovery/Polling/HistorySync). Update the existing test in `RollupWorkerTests.cs` to pass `TestScopeFactory(f.CreateContext)`.

- [ ] Step 2: Register `TariffCalculator` (from `KasaMonitor.Core.Costing`) as a singleton:

```csharp
var tariff = builder.Configuration.GetValue("KasaMonitor:TariffRonPerKwh", 1.30m);
builder.Services.AddSingleton(new KasaMonitor.Core.Costing.TariffCalculator(tariff));
```

(Place this near the other singleton registrations.)

- [ ] Step 3: Update `src/KasaMonitor.Web/Components/Pages/Dashboard.razor`:

Add `@inject KasaMonitor.Core.Costing.TariffCalculator Tariff` after the existing `@inject` lines.

Replace the `<h1>Plugs</h1>` section with a top-bar block:

```razor
<div class="top-bar">
    <h1>KasaMonitor</h1>
    <div class="totals">
        <span class="total-power">@TotalPowerW.ToString("0") W</span>
        <span class="total-cost">@(HourlyCostRon.ToString("0.000")) RON/h</span>
    </div>
</div>
```

Add to the `@code { ... }` block:

```csharp
private double TotalPowerW => _states.Values
    .Where(s => s.IsOnline && s.LastReading is not null)
    .Sum(s => s.LastReading!.PowerMw / 1000.0);

private decimal HourlyCostRon => Tariff.CostFromWh((long)TotalPowerW);
```

Add styling:

```css
.top-bar { display: flex; justify-content: space-between; align-items: baseline; padding: 0.5em 0; border-bottom: 1px solid #eee; margin-bottom: 1em; }
.top-bar .totals { font-size: 1.2em; }
.top-bar .total-power { font-weight: bold; margin-right: 1em; }
.top-bar .total-cost { color: #666; }
```

- [ ] Step 4: Build clean. Run all tests (70 still pass after RollupWorker test update).

- [ ] Step 5: Smoke-test the app briefly — confirm the top bar renders and top-bar values update with live data.

- [ ] Step 6: Commit:

```bash
git add src/KasaMonitor.Web/Program.cs src/KasaMonitor.Web/Components/Pages/Dashboard.razor src/KasaMonitor.Workers/RollupWorker.cs tests/KasaMonitor.Workers.Tests/RollupWorkerTests.cs
git commit -m "feat(web): wire history-sync + rollup hosted services; show total power + RON/h on dashboard"
```

### Task 6.2: Healthcheck CLI mode

**Files:**
- Modify: `src/KasaMonitor.Web/Program.cs`

The Dockerfile's `HEALTHCHECK` runs `dotnet KasaMonitor.Web.dll --healthcheck`, which should exit 0 if the local server returns a 200 from `/health`, exit 1 otherwise. No `curl`/`wget` dependency in the runtime base image.

- [ ] Step 1: At the very top of `Program.cs` (before `WebApplication.CreateBuilder(args)`), add:

```csharp
if (args is ["--healthcheck"])
{
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        var resp = await http.GetAsync("http://localhost:8910/health");
        Environment.Exit(resp.IsSuccessStatusCode ? 0 : 1);
    }
    catch
    {
        Environment.Exit(1);
    }
}
```

This requires `Program.cs` to be top-level statements (which it is). The port 8910 matches the Docker `EXPOSE` and `KASA_MONITOR__URLS` env var (set in the Dockerfile).

- [ ] Step 2: Build clean.

- [ ] Step 3: Smoke-test locally:
  - Start the app on port 5174 (default launch profile).
  - The healthcheck CLI hard-codes 8910, so it'll fail locally — that's expected; only Docker uses it. Don't try to make it work outside the container.

- [ ] Step 4: Commit:

```bash
git add src/KasaMonitor.Web/Program.cs
git commit -m "feat(web): add --healthcheck CLI mode for Docker HEALTHCHECK probe"
```

### Task 6.3: Dockerfile (multi-stage, linux/amd64)

**Files:**
- Create: `deploy/Dockerfile`
- Create: `deploy/.dockerignore` (extends repo-root .dockerignore for build context relative to `deploy/`)

The build is done from the repo root because `Dockerfile` references `src/` and `tests/`.

- [ ] Step 1: `deploy/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KasaMonitor.sln Directory.Build.props ./
COPY src/ src/
COPY tests/ tests/
RUN dotnet restore
RUN dotnet publish src/KasaMonitor.Web/KasaMonitor.Web.csproj \
    -c Release -o /app --no-self-contained -r linux-x64

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8910 \
    KasaMonitor__DbPath=/data/kasa.db \
    TZ=Europe/Bucharest
EXPOSE 8910
VOLUME ["/data"]
HEALTHCHECK --interval=30s --timeout=5s --retries=3 \
  CMD ["dotnet", "KasaMonitor.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "KasaMonitor.Web.dll"]
```

- [ ] Step 2: Verify Docker is available locally: `docker --version`. If not installed (or daemon not running), skip the local-build step (3) but still commit the Dockerfile — the user can build on a machine with Docker.

- [ ] Step 3 (optional): Build the image locally:

```bash
docker build -t kasamonitor:dev -f deploy/Dockerfile .
```

Expected: builds successfully. Image size ~120 MB.

- [ ] Step 4: Commit:

```bash
git add deploy/Dockerfile
git commit -m "feat(deploy): add multi-stage Dockerfile (linux/amd64, .NET 10, healthcheck via CLI)"
```

### Task 6.4: docker-compose.yml + deploy README

**Files:**
- Create: `deploy/docker-compose.yml`
- Create: `deploy/README.md`

- [ ] Step 1: `deploy/docker-compose.yml`:

```yaml
services:
  kasamonitor:
    image: andysf/kasamonitor:latest
    container_name: kasamonitor
    network_mode: host          # REQUIRED for UDP broadcast discovery on the LAN
    restart: unless-stopped
    volumes:
      - /share/Container/kasamonitor/data:/data
    environment:
      TZ: Europe/Bucharest
      KasaMonitor__BroadcastNetworks: "192.168.254.255"
      KasaMonitor__TariffRonPerKwh: "1.30"
      KasaMonitor__PollIntervalSeconds: "10"
```

- [ ] Step 2: `deploy/README.md` — short deployment guide:

```markdown
# KasaMonitor — Deployment

## Local build & push to Docker Hub

```powershell
docker build -t andysf/kasamonitor:latest -t andysf/kasamonitor:0.1.0 -f deploy/Dockerfile .
docker login
docker push andysf/kasamonitor:latest
docker push andysf/kasamonitor:0.1.0
```

## QNAP / Portainer deployment

1. SSH to QNAP, ensure `/share/Container/kasamonitor/data` exists.
2. In Portainer → Stacks → Add stack → paste contents of `docker-compose.yml`.
3. Deploy. Wait ~30s.
4. Browse to `http://<qnap-ip>:8910/`.

## Configuration overrides

All `KasaMonitor__*` env vars are read on startup. The DB at `/data/kasa.db` persists across container restarts.

| Var | Default | Notes |
|---|---|---|
| `KasaMonitor__BroadcastNetworks` | (empty → use 255.255.255.255) | Comma-separated IPv4 broadcast addrs |
| `KasaMonitor__TariffRonPerKwh` | `1.30` | Cost calculation rate |
| `KasaMonitor__PollIntervalSeconds` | `10` | Plug poll interval |
| `KasaMonitor__DbPath` | `/data/kasa.db` | SQLite file location |

## Troubleshooting

- **No plugs discovered** → confirm `network_mode: host` is set; Container Station sometimes resets it. Without host networking, the UDP broadcast can't reach the LAN.
- **Database locked errors** → another container instance may be running against the same volume. Stop all but one.
- **Health endpoint 503** → check container logs; usually means EF migration failed at startup.
```

- [ ] Step 3: Commit:

```bash
git add deploy/docker-compose.yml deploy/README.md
git commit -m "feat(deploy): add docker-compose.yml for QNAP/Portainer + deployment README"
```

### Task 6.5: Chunk-6 verification gate

- [ ] Step 1: `dotnet build` → 0/0.
- [ ] Step 2: `dotnet test` → 70+ passed.
- [ ] Step 3: `git status` clean, log shows 4 chunk-6 commits.
- [ ] Step 4 (optional, if Docker is available): `docker build -t kasamonitor:dev -f deploy/Dockerfile .` succeeds.

---

## Chunk 7: UI completeness — NavMenu, Plugs, Settings, History pages

Final UI chunk. Adds the three missing pages and a left-side navigation. History uses ApexCharts.Blazor (the spec's default chart lib).

**Expected commit count:** 5. **Expected test count after Chunk 7:** 70 (UI is manually smoke-tested).

### Task 7.1: NavMenu + MainLayout + Plugs page

**Files:**
- Create: `src/KasaMonitor.Web/Components/Layout/NavMenu.razor`
- Modify: `src/KasaMonitor.Web/Components/Layout/MainLayout.razor`
- Create: `src/KasaMonitor.Web/Components/Pages/Plugs.razor`

`NavMenu.razor` — left sidebar with 4 links:

```razor
<nav class="nav-menu">
    <NavLink href="/" Match="NavLinkMatch.All">Dashboard</NavLink>
    <NavLink href="/history">History</NavLink>
    <NavLink href="/plugs">Plugs</NavLink>
    <NavLink href="/settings">Settings</NavLink>
</nav>

<style>
.nav-menu { display: flex; gap: 1em; padding: 0.5em 1em; background: #f5f5f5; border-bottom: 1px solid #ddd; }
.nav-menu a { text-decoration: none; color: #333; padding: 0.4em 0.8em; border-radius: 4px; }
.nav-menu a:hover { background: #e8e8e8; }
.nav-menu a.active { background: #4c4; color: white; }
</style>
```

Update `MainLayout.razor` to include `<NavMenu />` above `@Body`:

```razor
@inherits LayoutComponentBase

<NavMenu />
<main style="padding: 1em;">
    @Body
</main>

<div id="blazor-error-ui" data-nosnippet>
    An unhandled error has occurred.
    <a href="." class="reload">Reload</a>
    <a class="dismiss">&#x2716;</a>
</div>
```

`Plugs.razor` — list, rename alias, archive toggle:

```razor
@page "/plugs"
@rendermode InteractiveServer
@inject IServiceScopeFactory ScopeFactory
@implements IDisposable

<PageTitle>KasaMonitor — Plugs</PageTitle>
<h1>Plugs</h1>

@if (_plugs is null)
{
    <p><em>Loading…</em></p>
}
else if (_plugs.Count == 0)
{
    <p><em>No plugs in the database yet. Run discovery first.</em></p>
}
else
{
    <table class="plugs-table">
        <thead>
            <tr><th>Alias</th><th>MAC</th><th>IP</th><th>Model</th><th>SwVer</th><th>First seen</th><th>Last seen</th><th>Archived</th><th></th></tr>
        </thead>
        <tbody>
            @foreach (var p in _plugs.OrderBy(p => p.Alias))
            {
                <tr class="@(p.IsArchived ? "archived" : "")">
                    <td>
                        @if (_editingId == p.Id)
                        {
                            <input @bind="_editAlias" />
                            <button @onclick="() => SaveAlias(p.Id)">Save</button>
                            <button @onclick="CancelEdit">Cancel</button>
                        }
                        else
                        {
                            <span>@p.Alias</span> @if (p.AliasIsCustom) { <small>(custom)</small> }
                            <button @onclick="() => StartEdit(p)">Edit</button>
                        }
                    </td>
                    <td><code>@p.Mac</code></td>
                    <td>@p.IpAddress</td>
                    <td>@p.Model</td>
                    <td>@p.SwVer</td>
                    <td>@p.FirstSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")</td>
                    <td>@p.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")</td>
                    <td>
                        <button @onclick="() => ToggleArchive(p)">@(p.IsArchived ? "Unarchive" : "Archive")</button>
                    </td>
                </tr>
            }
        </tbody>
    </table>
}

<style>
.plugs-table { width: 100%; border-collapse: collapse; }
.plugs-table th, .plugs-table td { padding: 0.4em 0.6em; border-bottom: 1px solid #eee; text-align: left; }
.plugs-table tr.archived { opacity: 0.5; }
.plugs-table button { margin-left: 0.4em; }
.plugs-table input { padding: 0.2em 0.4em; }
</style>

@code {
    private List<Plug>? _plugs;
    private Guid? _editingId;
    private string _editAlias = "";
    private System.Threading.Timer? _refresh;

    protected override async Task OnInitializedAsync()
    {
        await ReloadAsync();
        _refresh = new System.Threading.Timer(_ => InvokeAsync(ReloadAsync), null, 5000, 5000);
    }

    private async Task ReloadAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        _plugs = (await repo.ListAllAsync(CancellationToken.None)).ToList();
        StateHasChanged();
    }

    private void StartEdit(Plug p)
    {
        _editingId = p.Id;
        _editAlias = p.Alias;
    }

    private void CancelEdit()
    {
        _editingId = null;
        _editAlias = "";
    }

    private async Task SaveAlias(Guid plugId)
    {
        using var scope = ScopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        await repo.SetAliasAsync(plugId, _editAlias, isCustom: true, CancellationToken.None);
        _editingId = null;
        _editAlias = "";
        await ReloadAsync();
    }

    private async Task ToggleArchive(Plug p)
    {
        using var scope = ScopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        await repo.SetArchivedAsync(p.Id, !p.IsArchived, CancellationToken.None);
        await ReloadAsync();
    }

    public void Dispose() => _refresh?.Dispose();
}
```

Note: the `_Imports.razor` already has `@using KasaMonitor.Core.Models` from Chunk 5 Task 5.3. Add `@using Microsoft.Extensions.DependencyInjection` if it's missing (needed for `GetRequiredService`).

- [ ] Step 1: Create the 3 files (NavMenu, modified MainLayout, Plugs).
- [ ] Step 2: Build clean.
- [ ] Step 3: Run all tests (70 still pass).
- [ ] Step 4: Smoke-test in browser: `/` and `/plugs` should render with NavMenu visible at top.
- [ ] Step 5: Commit `feat(web): add NavMenu + Plugs management page (rename alias + archive)`.

### Task 7.2: Settings page

**Files:**
- Create: `src/KasaMonitor.Web/Components/Pages/Settings.razor`

The settings page edits a few key/value entries via `ISettingsRepository`. Loads on init, saves on submit.

```razor
@page "/settings"
@rendermode InteractiveServer
@inject IServiceScopeFactory ScopeFactory

<PageTitle>KasaMonitor — Settings</PageTitle>
<h1>Settings</h1>

@if (_loading)
{
    <p><em>Loading…</em></p>
}
else
{
    <div class="settings-form">
        <label>Tariff (RON / kWh)</label>
        <input @bind="_tariff" type="number" step="0.01" />

        <label>Poll interval (seconds)</label>
        <input @bind="_pollSec" type="number" min="5" max="300" />

        <label>Broadcast networks (comma-separated)</label>
        <input @bind="_broadcast" />

        <div class="actions">
            <button @onclick="SaveAsync">Save</button>
            @if (_saved) { <span class="saved-msg">Saved.</span> }
        </div>
        <p class="note"><em>Note: poll interval and broadcast networks are read at startup. Restart the container for them to take effect. Tariff updates live.</em></p>
    </div>
}

<style>
.settings-form { display: grid; grid-template-columns: max-content 1fr; gap: 0.6em; max-width: 600px; align-items: center; }
.settings-form .actions { grid-column: 1 / -1; margin-top: 1em; }
.settings-form .actions button { padding: 0.4em 1em; }
.settings-form .saved-msg { margin-left: 1em; color: #4a4; }
.settings-form .note { grid-column: 1 / -1; color: #888; font-size: 0.85em; }
</style>

@code {
    private bool _loading = true;
    private bool _saved;
    private decimal _tariff;
    private int _pollSec;
    private string _broadcast = "";

    protected override async Task OnInitializedAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();

        var tariffStr = await repo.GetAsync("TariffRonPerKwh", CancellationToken.None);
        _tariff = decimal.TryParse(tariffStr, out var t) ? t : 1.30m;

        var pollStr = await repo.GetAsync("PollIntervalSeconds", CancellationToken.None);
        _pollSec = int.TryParse(pollStr, out var p) ? p : 10;

        _broadcast = await repo.GetAsync("BroadcastNetworks", CancellationToken.None) ?? "";

        _loading = false;
    }

    private async Task SaveAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
        await repo.SetAsync("TariffRonPerKwh", _tariff.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        await repo.SetAsync("PollIntervalSeconds", _pollSec.ToString(), CancellationToken.None);
        await repo.SetAsync("BroadcastNetworks", _broadcast, CancellationToken.None);
        _saved = true;
        StateHasChanged();
        await Task.Delay(2000);
        _saved = false;
        StateHasChanged();
    }
}
```

- [ ] Step 1: Create the file.
- [ ] Step 2: Build clean. Tests pass (70).
- [ ] Step 3: Smoke-test `/settings` — load + save round-trips persist.
- [ ] Step 4: Commit `feat(web): add Settings page (tariff + poll interval + broadcast networks)`.

### Task 7.3: History page (charts via ApexCharts.Blazor)

**Files:**
- Modify: `src/KasaMonitor.Web/KasaMonitor.Web.csproj` (add ApexCharts package ref)
- Create: `src/KasaMonitor.Web/Components/Pages/History.razor`

Add to `_Imports.razor`:

```razor
@using ApexCharts
```

Add to `KasaMonitor.Web.csproj`:

```xml
<PackageReference Include="Blazor-ApexCharts" Version="*" />
```

Run `dotnet restore` to pull the package.

Then `History.razor`:

```razor
@page "/history"
@rendermode InteractiveServer
@inject IServiceScopeFactory ScopeFactory

<PageTitle>KasaMonitor — History</PageTitle>
<h1>History</h1>

@if (_plugs is null || _plugs.Count == 0)
{
    <p><em>No plugs available.</em></p>
}
else
{
    <div class="history-controls">
        <label>Plug:
            <select @bind="_selectedPlugId" @bind:after="ReloadDataAsync">
                @foreach (var p in _plugs.OrderBy(p => p.Alias))
                {
                    <option value="@p.Id">@p.Alias</option>
                }
            </select>
        </label>
        <label>Range:
            <select @bind="_rangeDays" @bind:after="ReloadDataAsync">
                <option value="7">Last 7 days</option>
                <option value="30">Last 30 days</option>
                <option value="365">Last 12 months</option>
            </select>
        </label>
    </div>

    @if (_dailyData is null)
    {
        <p><em>Loading data…</em></p>
    }
    else if (_dailyData.Count == 0)
    {
        <p><em>No daily history yet — wait for the HistorySync worker to run (every 6h, or on plug discovery).</em></p>
    }
    else
    {
        <h3>Energy per day (kWh)</h3>
        <ApexChart TItem="DailyKwhPoint" Title="kWh / day"
                   Options="_chartOptions">
            <ApexPointSeries TItem="DailyKwhPoint"
                             Items="_dailyData"
                             Name="kWh"
                             SeriesType="SeriesType.Bar"
                             XValue="@(d => d.Date)"
                             YValue="@(d => (decimal)(d.EnergyWh / 1000.0))" />
        </ApexChart>
    }
}

<style>
.history-controls { display: flex; gap: 1em; margin-bottom: 1em; }
.history-controls label { display: flex; gap: 0.4em; align-items: center; }
.history-controls select { padding: 0.3em; }
</style>

@code {
    private List<Plug>? _plugs;
    private Guid _selectedPlugId;
    private int _rangeDays = 30;
    private List<DailyKwhPoint>? _dailyData;
    private ApexChartOptions<DailyKwhPoint> _chartOptions = new()
    {
        Chart = new Chart { Toolbar = new Toolbar { Show = false } }
    };

    public record DailyKwhPoint(string Date, long EnergyWh);

    protected override async Task OnInitializedAsync()
    {
        using var scope = ScopeFactory.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IPlugRepository>();
        _plugs = (await repo.ListAllAsync(CancellationToken.None)).Where(p => !p.IsArchived).ToList();
        if (_plugs.Count > 0)
        {
            _selectedPlugId = _plugs.First().Id;
            await ReloadDataAsync();
        }
    }

    private async Task ReloadDataAsync()
    {
        if (_selectedPlugId == Guid.Empty) return;
        using var scope = ScopeFactory.CreateScope();
        var historyRepo = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
        var to = DateOnly.FromDateTime(DateTime.UtcNow);
        var from = to.AddDays(-_rangeDays);
        var entries = await historyRepo.GetDailyRangeAsync(_selectedPlugId, from, to, CancellationToken.None);
        _dailyData = entries.Select(d => new DailyKwhPoint(
            $"{d.Year:D4}-{d.Month:D2}-{d.Day:D2}", d.EnergyWh)).ToList();
        StateHasChanged();
    }
}
```

- [ ] Step 1: Add ApexCharts package + restore.
- [ ] Step 2: Add the @using ApexCharts to _Imports.
- [ ] Step 3: Create History.razor.
- [ ] Step 4: Build clean. Tests pass.
- [ ] Step 5: Smoke-test `/history` — confirm dropdowns work; chart renders if any DailyEnergy rows exist.
- [ ] Step 6: Commit `feat(web): add History page with kWh-per-day bar chart (ApexCharts.Blazor)`.

### Task 7.4: ApexCharts JS reference in App.razor

ApexCharts.Blazor needs the JS bundle loaded. Update `src/KasaMonitor.Web/Components/App.razor` `<head>` section to include the ApexCharts JS via the package's static web asset path.

Read the package docs (or check `obj/Release/staticwebassets`) for the exact URL. Typically it's:

```html
<script src="_content/Blazor-ApexCharts/js/blazor-apexcharts.js"></script>
```

Add the `<script>` tag to `App.razor` before the closing `</body>`:

```razor
<script src="_framework/blazor.web.js"></script>
<script src="_content/Blazor-ApexCharts/js/blazor-apexcharts.js"></script>
```

Verify by smoke test.

- [ ] Step 1: Edit `App.razor`.
- [ ] Step 2: Smoke-test the History page — chart should render without console errors.
- [ ] Step 3: Commit `fix(web): load ApexCharts JS bundle in App.razor`.

### Task 7.5: Chunk-7 verification gate

- [ ] Step 1: `dotnet build` clean.
- [ ] Step 2: `dotnet test` 70 passed.
- [ ] Step 3: Manual smoke: visit `/`, `/history`, `/plugs`, `/settings` — all four pages render via NavMenu.
- [ ] Step 4: `git status` clean. Log shows ~5 chunk-7 commits.

---
