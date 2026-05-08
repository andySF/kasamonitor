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

Expected: 5 passed, 0 failed.

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

Expected: 21 passed, 0 failed.

Tally: KasaCodec = 6 (3 facts + 3 theory rows on `RoundTrip_PreservesAnyAsciiInput`), KasaWireProtocol = 8, DtoMapping = 5, KasaClient = 2.

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

Expected: 21 passed, 0 failed.

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
