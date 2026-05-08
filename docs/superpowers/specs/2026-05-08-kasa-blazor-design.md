# KasaMonitor — Design Document

**Date:** 2026-05-08
**Status:** Approved (pending spec review)
**Author:** Andy + Claude (brainstorming session)

---

## 1. Context

User has 5 TP-Link Kasa **KP115(EU)** smart plugs with energy monitoring on a home LAN (`192.168.254.0/24`). Current state:

- 5 plugs already discovered, all online, MAC OUI `C0:06:C3` (TP-Link).
- Plug aliases and roles known: `Birou` (PC), `Sufeagerie` (TV standby), `Switch 2.5` (network switch), `Camera tehnica` (NAS/server), `Ups Birou` (UPS-protected gear).
- Total instantaneous load ~470 W; YTD energy ~935 kWh across 5 plugs.
- Existing throwaway Python dashboard at `C:\Users\andys\kasa_dashboard.py` proves protocol works.
- One plug runs older firmware (1.0.16 from 2021); rest run 1.0.20 (2022).

User wants a **Blazor application** to replace the Python prototype, deployed as a **Docker container on a QNAP NAS via Portainer**, with **automatic historical data persistence**.

## 2. Problem Statement

The Python prototype is ephemeral (runs from a workstation, dies on reboot, no history). The Kasa app on phone shows real-time data but limited historical analysis and no per-tariff cost computation. We need a **persistent, self-hosted, always-on monitoring service** that:

- Auto-discovers Kasa plugs on the LAN.
- Polls each plug every 10 seconds for real-time energy data.
- Synchronizes the device-side daily/monthly energy history (canonical source).
- Persists historical data in a local database with sensible retention/rollup.
- Renders a live dashboard over the LAN (Blazor Server, SignalR push).
- Computes cost in RON using a configurable tariff.

Out of scope (explicitly): on/off control, automation rules, schedules, external notifications, multi-user/auth.

## 3. Goals & Non-Goals

### Goals (MUST)
- Run as a single Docker image on QNAP (linux/amd64) under Portainer.
- Survive reboots: state persists in a mounted volume.
- Live dashboard refreshes every ≤10s without page reload.
- Historical charts show kWh/day, kWh/month, and power-vs-time at sub-hourly resolution.
- Cost displayed alongside energy using configurable RON/kWh tariff.
- Plug list managed via auto-discovery + UI overrides.
- Zero data loss for up to 30 days of downtime (recovered from device-side daystat).

### Non-Goals
- Plug control (relay on/off) — pure read-only.
- Automation engine (schedules, threshold rules, scenes).
- External notifications (email, Telegram, webhooks).
- Authentication / multi-user — LAN access only.
- Multi-arch Docker images — x86_64/amd64 only.
- Tapo (newer TP-Link) protocol support — only Kasa.

## 4. Architecture Overview

**Stack choice:**
- **.NET 10 LTS** (supported until Nov 2028; .NET 8 LTS expires Nov 2026, .NET 9 STS expires May 2026).
- **Blazor Server** (single-replica, SignalR push, no client-side WASM bundle).
- **EF Core 10 + SQLite** (file-based, single-volume backup, sufficient for projected data volumes).
- **Hosted services** (`IHostedService` / `BackgroundService`) for discovery, polling, history sync, rollup.
- **Custom Kasa protocol implementation** (no third-party NuGet — `TPLink-SmartHome` is unmaintained since 2022; protocol is ~150 LoC and we already have the Python reference).
- **Docker host networking** required for UDP broadcast discovery (Container Station on QNAP supports this).
- **Serilog** structured logging to stdout (JSON), captured by Container Station.

**Why Blazor Server (not WASM or Hybrid):**
- SignalR sticky session = trivial real-time push to the dashboard.
- Single deployable image (no API + WASM bundle split).
- LAN-only single-replica deployment doesn't trigger any of the scaling concerns where WASM/Hybrid would help.

**Why SQLite (not Postgres/InfluxDB/TimescaleDB):**
- 5 plugs × 10s × 30 days raw = ~1.3M rows; with `(PlugId, TimestampUtc DESC)` index, query latency stays in single-digit ms.
- 1-min aggregates after 30-day rollup: 5 × 1440 × 365 = ~2.6M/year. Still trivial.
- DB size projected < 200 MB after years of operation.
- Eliminates a second container, secrets, network config, backup story.
- WAL mode (`PRAGMA journal_mode=WAL; busy_timeout=5000`) prevents reader/writer contention between UI charts and the poller.

## 5. Solution Layout

```
KasaMonitor.sln
├── src/
│   ├── KasaMonitor.Core/         # logic only: protocol, models, costing
│   │   ├── Protocol/             # KasaClient, XOR codec, JSON DTOs
│   │   ├── Models/               # PlugInfo, EmeterReading, DailyKwh, ...
│   │   └── Costing/              # tariff math (RON/kWh)
│   ├── KasaMonitor.Data/         # EF Core, migrations, repositories
│   │   ├── KasaDbContext.cs
│   │   ├── Entities/             # PlugEntity, SampleEntity, ...
│   │   ├── Repositories/         # IPlugRepository, ISampleRepository
│   │   └── Migrations/
│   ├── KasaMonitor.Workers/      # IHostedService implementations
│   │   ├── DiscoveryWorker.cs
│   │   ├── PollingWorker.cs
│   │   ├── HistorySyncWorker.cs
│   │   └── RollupWorker.cs
│   └── KasaMonitor.Web/          # ASP.NET Core + Blazor Server host
│       ├── Components/           # Pages + reusable Razor components
│       ├── Services/             # PlugStateService (in-process pub/sub for UI)
│       └── Program.cs            # DI wiring, middleware
├── tests/
│   ├── KasaMonitor.Core.Tests/
│   └── KasaMonitor.Data.Tests/
├── deploy/
│   ├── Dockerfile
│   ├── docker-compose.yml
│   └── README.md
└── KasaMonitor.sln
```

**Boundary rules:**
- `Core` knows nothing about EF, ASP.NET, or DI containers — it's pure logic + DTOs.
- `Data` references `Core`, depends on `Microsoft.EntityFrameworkCore.Sqlite`. Repositories implement interfaces declared in `Core` (DIP).
- `Workers` reference `Core` + `Data`. They're pure services; they raise events on a typed channel that `Web` subscribes to.
- `Web` references `Core` + `Data` + `Workers`. Owns DI wiring, Blazor components, SignalR.
- No project depends on a project "above" it.

## 6. Components

### 6.1 `KasaClient` (Core/Protocol)

Static (or singleton) class implementing the Kasa wire protocol.

**Methods:**
- `Task<PlugInfo> GetSysInfoAsync(IPAddress ip, CancellationToken ct)`
- `Task<EmeterReading> GetRealtimeAsync(IPAddress ip, CancellationToken ct)`
- `Task<List<DailyKwh>> GetDayStatAsync(IPAddress ip, int year, int month, CancellationToken ct)`
- `Task<List<MonthlyKwh>> GetMonthStatAsync(IPAddress ip, int year, CancellationToken ct)`
- `IAsyncEnumerable<DiscoveredPlug> DiscoverAsync(IEnumerable<IPAddress> broadcastAddrs, TimeSpan timeout, CancellationToken ct)`

**Wire details:**
- TCP: 4-byte big-endian length prefix + XOR-encrypted JSON. Port 9999.
- UDP: raw XOR-encrypted JSON (no length prefix). Port 9999.
- XOR cipher: key starts at 171, each byte XORed with previous encrypted byte.
- Fixed timeout 2.5s per call. **No internal retry** — caller decides offline policy.

**Tested via:** captured JSON fixtures from real plugs (encoded + decoded round-trip), parser tests against `kasa_full.json`.

### 6.2 `DiscoveryWorker`

Periodic broadcast-based plug discovery.

- **Cadence:** at startup + every 5 minutes.
- **Logic:** enumerate local IPv4 interfaces → compute broadcast addresses → call `KasaClient.DiscoverAsync` → upsert `PlugEntity` keyed on MAC.
- **Fields updated:** `IpAddress`, `Alias` (only if user has not customized), `Model`, `HwVer`, `SwVer`, `LastSeenUtc`.
- **Never deletes plugs.** A plug missing from discovery keeps its row; it just stops getting `LastSeenUtc` bumps.
- **Plug discovered for the first time** raises a `PlugDiscovered` event consumed by `HistorySyncWorker` to backfill device history immediately.

### 6.3 `PollingWorker`

Real-time data collection, fan-out parallel.

- **Cadence:** every `PollIntervalSeconds` (default 10).
- **Concurrency:** `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = 8`. Each plug call has its own 2.5s timeout, so a worst-case cycle = 2.5s regardless of plug count.
- **Per plug:** `GetRealtimeAsync` → `SampleEntity` (online=true). On timeout/error → `SampleEntity` with `Online=false` and `ErrorMessage` populated.
- **After insert:** publish `PlugLiveState` to `IPlugStateBroadcaster` (in-process channel) so the UI sees it within 100ms.
- **No internal retry**: an offline sample is the signal. UI shows "offline 12s" countdown based on `LastSampleUtc`.

### 6.4 `HistorySyncWorker`

Reconciles device-side history into the DB.

- **Cadence:** triggered by `PlugDiscovered` event for first-time plugs (full sync), and every 6 hours for known plugs (incremental).
- **Per plug, every cycle:**
  - `GetMonthStatAsync(currentYear)` and `GetMonthStatAsync(currentYear-1)`.
  - `GetDayStatAsync(currentYear, currentMonth)` and one month back.
  - **First-time only:** all months returned by the device for the prior year (typically 12 months).
- **Persistence:** `INSERT OR REPLACE` into `DailyEnergy` and `MonthlyEnergy`. Idempotent — partial run + crash + restart = no duplicates, no gaps.

### 6.5 `RollupWorker`

Nightly aggregation and retention.

- **Cadence:** 03:00 local time daily.
- **Step 1 — aggregate:** for each day not yet aggregated:
  ```sql
  INSERT OR IGNORE INTO OneMinuteAggregates
    SELECT PlugId,
           datetime((strftime('%s', TimestampUtc) / 60) * 60, 'unixepoch'),
           AVG(PowerMw), MAX(PowerMw), MIN(PowerMw),
           COUNT(*), SUM(CASE WHEN Online=1 THEN 1 ELSE 0 END)
    FROM Samples
    WHERE TimestampUtc BETWEEN @dayStart AND @dayEnd
    GROUP BY PlugId, bucket;
  ```
  Then analogous rollup `OneMinuteAggregates → OneHourAggregates`. `OneHourAggregates.EnergyWh` computed as `PAvgMw * 3600 / 1e6`.
- **Step 2 — retention:**
  - `DELETE FROM Samples WHERE TimestampUtc < now() - 30 days`
  - `DELETE FROM OneMinuteAggregates WHERE BucketStartUtc < now() - 365 days`
  - `OneHourAggregates`, `DailyEnergy`, `MonthlyEnergy` kept forever.
- **Step 3 — VACUUM:** monthly (first run of month) only, since VACUUM rewrites the entire DB.

### 6.6 `PlugStateService` (Web)

Singleton in-process state broadcaster for the dashboard.

- Holds `Dictionary<Guid, PlugLiveState>` with the latest known state per plug.
- API:
  - `IReadOnlyDictionary<Guid, PlugLiveState> Snapshot()` — synchronous read for initial render.
  - `event Action<PlugLiveState> StateChanged` — components subscribe in `OnInitializedAsync`, unsubscribe in `Dispose`.
- Receives updates from `PollingWorker.Publish(...)`. Workers do not reference Blazor.

### 6.7 UI — Pages

| Route | Purpose |
|-------|---------|
| `/` | Live dashboard. One card per plug: alias, instant power, V/A, total kWh, RSSI, uptime, online/offline badge. Top bar shows total instant W and current hourly cost (RON). |
| `/history` | Plug picker + interval picker (24h / 7d / 30d / 12 months). Bar chart for kWh/day from `DailyEnergy`. Line chart for power from `OneMinuteAggregates` (24h / 7d) or `OneHourAggregates` (30d / 12m). |
| `/plugs` | Manage plugs: rename alias (DB-only, doesn't touch device), archive, view MAC/IP/firmware, add manual IP (in case discovery fails). |
| `/settings` | Tariff RON/kWh, polling interval (5–300s), retention (raw 1–365 days, 1-min 30–3650 days), broadcast addresses (comma-separated). All validated server-side. |

UI charts use a lightweight library — **ApexCharts.Blazor** or **ChartJs.Blazor** (decision deferred to plan stage; both are mature and Blazor-native). No raw JS interop hand-rolling.

## 7. Data Model (SQLite)

```
Plugs
  Id              GUID PK
  Mac             TEXT UNIQUE NOT NULL    (lowercase, no separators)
  IpAddress       TEXT NOT NULL
  Alias           TEXT NOT NULL           (default = device alias; user can override)
  AliasIsCustom   INTEGER NOT NULL DEFAULT 0
  Model           TEXT, HwVer TEXT, SwVer TEXT, OemId TEXT
  FirstSeenUtc    DATETIME, LastSeenUtc DATETIME
  IsArchived      INTEGER NOT NULL DEFAULT 0
  Notes           TEXT NULL

Samples                                    (raw 10s; retention default 30 days)
  Id              INTEGER PK AUTOINCREMENT
  PlugId          GUID FK -> Plugs(Id)
  TimestampUtc    DATETIME NOT NULL
  Online          INTEGER NOT NULL
  PowerMw         INTEGER NULL
  VoltageMv       INTEGER NULL
  CurrentMa       INTEGER NULL
  TotalWh         INTEGER NULL
  Rssi            INTEGER NULL
  RelayState      INTEGER NULL
  ErrorMessage    TEXT NULL
  INDEX idx_samples_plug_time (PlugId, TimestampUtc DESC)

OneMinuteAggregates                        (retention default 365 days)
  PlugId          GUID
  BucketStartUtc  DATETIME
  PAvgMw          INTEGER
  PMaxMw          INTEGER
  PMinMw          INTEGER
  SampleCount     INTEGER
  OnlineCount     INTEGER
  PRIMARY KEY (PlugId, BucketStartUtc)

OneHourAggregates                          (retention forever)
  PlugId          GUID
  BucketStartUtc  DATETIME
  PAvgMw          INTEGER
  PMaxMw          INTEGER
  EnergyWh        INTEGER
  SampleCount     INTEGER
  PRIMARY KEY (PlugId, BucketStartUtc)

DailyEnergy                                (canonical, from device daystat)
  PlugId          GUID
  Date            TEXT (ISO YYYY-MM-DD, local)
  EnergyWh        INTEGER
  PRIMARY KEY (PlugId, Date)

MonthlyEnergy
  PlugId          GUID
  Year            INTEGER
  Month           INTEGER
  EnergyWh        INTEGER
  PRIMARY KEY (PlugId, Year, Month)

Settings                                   (single-row key/value store)
  Key             TEXT PK
  Value           TEXT
  -- Keys: TariffRonPerKwh, PollIntervalSeconds, RetentionRawDays,
  --       RetentionMinuteDays, BroadcastAddresses, AliasOverridesEnabled
```

**Energy reconciliation strategy:**
- `DailyEnergy` (from device daystat) is the canonical kWh/day source for charts and totals at day-and-above granularity.
- `OneHourAggregates.EnergyWh` is computed from samples and is used **only** for sub-day drill-down (zoom < 24h).
- This bounds discrepancies between integrals to within an hour, never a day or month.

## 8. Data Flow

### Startup flow
```
Container starts
  → EF Core migration applies (idempotent)
  → DI builds host
  → DiscoveryWorker.StartAsync()
      → broadcast UDP on each interface
      → upsert Plugs
      → for each new plug, raise PlugDiscovered
  → HistorySyncWorker (triggered by PlugDiscovered)
      → fetch monstat (current year + previous)
      → fetch daystat (current month + previous)
      → UPSERT DailyEnergy / MonthlyEnergy
  → PollingWorker.StartAsync()
      → loop: every 10s, parallel-fetch all active plugs
      → insert SampleEntity
      → publish PlugLiveState
  → ASP.NET Core listens on :8910
  → UI client connects → reads PlugStateService.Snapshot()
                       → subscribes to StateChanged
                       → SignalR pushes updates as poller runs
```

### Nightly flow (03:00 local)
```
RollupWorker.RunAsync()
  → for each ungraduated day: aggregate Samples → OneMinuteAggregates
  → for each ungraduated day: aggregate OneMinuteAggregates → OneHourAggregates
  → DELETE old Samples, OneMinuteAggregates per retention
  → if first run of month: VACUUM
HistorySyncWorker (every 6h, independent)
  → re-fetch current month daystat (device updates these throughout day)
  → UPSERT DailyEnergy
```

## 9. Error Handling

| Failure | Response | UI Visibility |
|---------|----------|---------------|
| `GetRealtimeAsync` timeout | Sample with `Online=false`, `ErrorMessage="timeout"` | Card grayed, "offline Ns" badge |
| Connection refused | Sample offline, log warn | Same |
| Malformed JSON (firmware corruption) | Sample offline, log error with hex dump | Same + "protocol error" badge |
| 3 consecutive offline polls | In-memory `IsStale=true` | Card with orange background |
| 24h offline | DB `IsArchived=true` | Moved to "Archived" section |
| UDP discovery fails (broadcast blocked) | Log error, retain known plugs | Banner "discovery offline — check host network" |
| DB locked / IO error | Retry × 3 with exponential backoff (50ms, 200ms, 1s), then drop sample, increment `samples_dropped` counter | Counter visible at `/health` |
| EF migration fails at startup | App refuses to start, exit 1, log clear reason | Container restart loop, Portainer shows logs |
| Discovery returns plug with unknown model | Persist with `Model=raw`, never crash | Card shows raw model name |

**Key principles:**
1. **Never lose known plugs.** Discovery only adds; deletion is manual via UI.
2. **Polling is per-plug isolated.** One bad plug doesn't block others.
3. **DB writes never stop polling.** When SQLite is busy (rollup/VACUUM), in-memory buffer up to 1000 samples for 60s, then flush. Buffer overflow drops oldest, increments counter.
4. **All history operations idempotent.** `UPSERT` everywhere; partial run + crash = no corruption.
5. **Health endpoint** `/health` returns 200 only if: DB responds, ≥1 plug discovered, last polling cycle < 30s ago. Used by Docker `HEALTHCHECK`.
6. **Graceful shutdown.** SIGTERM → stop polling → flush buffer → close DbContext → exit < 10s.
7. **Settings input validation.** `TariffRonPerKwh ∈ (0, 100)`, `PollIntervalSeconds ∈ [5, 300]`, `RetentionRawDays ∈ [1, 365]`. Invalid values → log + fallback to default, never crash.

## 10. Testing

| Layer | Type | Coverage |
|-------|------|----------|
| `Core.Tests` | Unit (xUnit, in-process) | XOR codec round-trip, JSON parsing of sysinfo/emeter/daystat/monstat from real captured fixtures, costing math, edge cases (empty stat list, DST boundary) |
| `Data.Tests` | Integration (SQLite in-memory) | Migrations apply clean, `UpsertPlug` idempotent, rollup 10s→1min→1h on seed dataset, retention prune, settings KV persistence |
| `Workers.Tests` | Integration (mock `IKasaClient`) | Polling marks offline correctly on simulated timeout, history sync no duplicates after re-run, discovery upsert preserves user-customized alias |
| `Web` | Smoke manual + minimal Razor render tests | DI wires up, dashboard renders without exceptions on empty DB |

**Coverage targets:** Core ≥ 90%, Data ≥ 80%, Workers ≥ 70%. Web has no target (logic is minimal).

**Real-world fixtures:** captured from `kasa_full.json` (current production data) into `tests/fixtures/`. Tests parse against authentic payloads, not invented ones.

## 11. Deployment

### 11.1 Dockerfile (multi-stage, linux/amd64)

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY KasaMonitor.sln ./
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
  CMD curl -fsS http://localhost:8910/health || exit 1
ENTRYPOINT ["dotnet", "KasaMonitor.Web.dll"]
```

Final image size: ~120 MB.

### 11.2 Versioning & Docker Hub push

Tag scheme:
- `andysf/kasamonitor:latest` — latest build of `main`.
- `andysf/kasamonitor:0.1.0` — semver per release.
- `andysf/kasamonitor:0.1` — major.minor floating tag.
- `andysf/kasamonitor:sha-<gitsha>` — exact pin for debugging.

**Two paths supported (documented in `deploy/README.md`):**

1. **Local build + push:**
   ```powershell
   docker build -t andysf/kasamonitor:0.1.0 -t andysf/kasamonitor:latest -f deploy/Dockerfile .
   docker login
   docker push andysf/kasamonitor:0.1.0
   docker push andysf/kasamonitor:latest
   ```

2. **GitHub Actions** on `git tag v*`: workflow under `.github/workflows/release.yml` runs tests, builds image, pushes all tags. Requires `DOCKERHUB_USERNAME` + `DOCKERHUB_TOKEN` GitHub Secrets.

### 11.3 docker-compose for Portainer/QNAP

```yaml
services:
  kasamonitor:
    image: andysf/kasamonitor:latest
    container_name: kasamonitor
    network_mode: host          # REQUIRED for UDP broadcast discovery
    restart: unless-stopped
    volumes:
      - /share/Container/kasamonitor/data:/data
    environment:
      TZ: Europe/Bucharest
      KasaMonitor__BroadcastNetworks: "192.168.254.255"
      KasaMonitor__TariffRonPerKwh: "1.30"
      KasaMonitor__PollIntervalSeconds: "10"
```

**Port choice:** `8910` (not 8080) to avoid conflict with QNAP admin UI which uses 8080 by default.

**User workflow after deploy:**
1. Portainer → Stacks → Add stack → paste compose → Deploy.
2. Browser → `http://<qnap-ip>:8910/`.
3. Wait ~30s for initial discovery + history sync.
4. Dashboard shows all 5 plugs with full history.

## 12. Configuration

All configuration is layered (later overrides earlier):
1. `appsettings.json` (baked into image; reasonable defaults).
2. Environment variables prefixed `KasaMonitor__` (Portainer-managed).
3. `Settings` table in DB (UI-editable; takes priority for runtime tunables like tariff).

**Keys:**
| Key | Default | Notes |
|---|---|---|
| `DbPath` | `/data/kasa.db` | Volume-mounted |
| `BroadcastNetworks` | (auto-detect) | Comma-separated IPv4 broadcast addrs |
| `PollIntervalSeconds` | `10` | DB-overridable |
| `TariffRonPerKwh` | `1.30` | DB-overridable |
| `RetentionRawDays` | `30` | DB-overridable |
| `RetentionMinuteDays` | `365` | DB-overridable |
| `RollupTimeLocal` | `03:00` | DB-overridable |

## 13. Open Questions / Future Work

- **Chart library decision** (ApexCharts vs ChartJs.Blazor) deferred to plan stage. Will pick after a quick spike comparing bundle size and Blazor-Server WebSocket overhead.
- **Tapo support** explicitly out of scope; if user later adds Tapo plugs, this becomes a separate spec.
- **Multi-arch Docker image** (arm64 for future ARM-based QNAP) — not now, but Dockerfile structure supports it via `--platform`.
- **Backup / restore UI** — currently SQLite file backup is "stop container, copy `/data/kasa.db`". Could add UI button for hot backup later.

## 14. Glossary

- **Kasa**: TP-Link's older smart-plug ecosystem (KP-series). Uses port 9999 with XOR-encrypted JSON.
- **KP115(EU)**: 16A smart plug with energy monitoring, EU plug type.
- **daystat / monstat**: Device-side per-day / per-month energy counters retained ~30 days / ~12 months on the plug itself.
- **Sample**: One 10-second snapshot of a plug's real-time state (power, voltage, current, total).
- **Aggregate**: A bucketed summary (1-min or 1-h) computed from samples during nightly rollup.
- **Rollup**: The nightly process that converts raw samples into aggregates and prunes old data per retention.
