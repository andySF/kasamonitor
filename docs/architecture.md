# KasaMonitor architecture

This document describes how the solution is organised, how data flows through it and how to add a new integration.
It complements the [README](../README.md), which is the reference for features, settings, metrics and the HTTP API;
here the README sections are linked instead of repeated. Design rationale lives in [decisions.md](decisions.md), the
appliance-detection pipeline in [nilm.md](nilm.md), and what the devices' local APIs offer in
[device-api-capabilities.md](device-api-capabilities.md).

Target: .NET 10, Blazor (server-rendered, interactive), EF Core with SQL Server (0.12.0; SQLite before), `prometheus-net`, ML.NET. The UI is in
Romanian; code, comments and commit messages are in English.

## 1. Solution layout

```
KasaMonitor.sln
├── src/
│   ├── KasaMonitor.Core      pure logic, no I/O frameworks: models, protocol clients/parsers, rules
│   ├── KasaMonitor.Data      EF Core DbContext, entities, migrations, repositories (SQL Server)
│   ├── KasaMonitor.Workers   BackgroundServices and in-memory state services; wiring of repositories
│   └── KasaMonitor.Web       Blazor UI, /metrics, minimal HTTP endpoints, Program.cs, formatting helpers
├── tests/
│   ├── KasaMonitor.Core.Tests        rules, parsers, fixtures (tests/…/fixtures)
│   ├── KasaMonitor.Data.Tests        repositories, model constraints, start-up on SQLite in memory
│   ├── KasaMonitor.Workers.Tests     workers and services, SQLite in memory
│   ├── KasaMonitor.Web.Tests         metrics, formatting, integration status rules
│   ├── KasaMonitor.Web.ApiTests      the running app in-process (on a SQLite file): pages, endpoints, /api/nilm
│   └── KasaMonitor.SqlServer.Tests   SQL Server in Docker (Testcontainers): migration, collation, the repository
│                                     tests again, app start-up and /health, the data migration tool
├── tools/
│   ├── KasaMonitor.DataMigration     kasa-migrate: one-off copy of a SQLite kasa.db into SQL Server
│   └── KasaMonitor.NilmReplay        kasa-nilm-replay: a stored meter series through the event detector, as it is and with a median
└── deploy/                   Dockerfile, the stack (docker-compose.yml), sql/create-database.sql, Prometheus/Grafana provisioning, build script
```

Dependencies point one way: `Web → Workers → Data → Core`, and `Workers → Core`. Core knows nothing about EF Core,
ASP.NET or the hosting model, which is why almost every rule (the NILM clusterer, the burner timeline, the
period-comparison rules, the climate write rule, the home-page breakdown) is a pure function with a unit test.

| Project | What lives there |
|---|---|
| **Core** | `Protocol/` (Kasa legacy, Tapo KLAP/AES, Shelly RPC clients and parsers), `Models/` (`Plug`, roles, live state), `Abstractions/` (repository and service interfaces), `Metering/`, `Costing/` (tariffs, `EnergyPrices`), `Reports/` (`ReportPeriod`, `HouseReport`, `HeatingReport`, same-fraction comparison), `Heating/` (Viessmann API model, `BoilerClock`, `BurnerTimeline`), `Miele/` (API, SSE parser, cycle tracker, ground-truth rule), `Climate/` (sources, write rule, freshness, aggregation, Govee/Open-Meteo/Shelly parsers), `Nilm/` (events, features, clusterer, merger, signature profile and sorting), `Home/` (breakdown, Sankey and bubble rules), `History/`, `Devices/` (`DeviceCategory`, `IntegrationKeys`) |
| **Data** | `KasaDbContext`, `Entities/`, `Migrations/`, `Repositories/` implementing the Core interfaces. Time columns are stored as UTC ticks; the entities are plain records with no logic |
| **Workers** | One class per background loop, the in-memory states the UI reads (`HeatingState`, `MieleState`, `ClimateState`, `PlugStateBroadcaster`, `LiveStreamBroadcaster`), the services that compose repositories into page data (`HomeService`, `HistoryService`, `EnergyReportService`, `DeviceCatalog`, `NilmService`), and the settings services |
| **Web** | `Components/Pages` (one page per route), `Components/Shared`, `HomeCards`, `Istoric`, `Settings` (one tab component per integration), `Devices` (row editors and „Adaugă” panels), `Ui/` (formatting, the integration registry, SVG helpers), `*Metrics.cs` (Prometheus families), `Api/NilmApi.cs`, `Integration/` (generated Prometheus/Grafana files) |

## 2. Data flow

```
 devices / clouds                  Workers                          storage / state                 consumers
 ───────────────                  ─────────                        ───────────────                 ─────────
 Kasa/Tapo/Shelly (LAN) ──poll 10 s──► PollingWorker ──────────────► Samples, daily counters
                       └─stream 1 s──► LiveStreamWorker ───────────► LiveSamples (on change),
 main meter (Shelly EM) ──1 s────────►                    │            LiveMinuteAggregates
                                                          └─► LiveStreamBroadcaster ─► NILM, Home, /contor
 Viessmann cloud ──────poll 2 min───► HeatingPollWorker ───────────► HeatingReadings, daily/period tables
 Miele cloud ──────────SSE stream────► MieleStreamWorker ──────────► MieleStateSnapshots, MieleCycles
                       └─6 h / after a cycle─► MieleHistorySync ───► (merged into MieleCycles)
 Govee / Open-Meteo / Shelly H&T / boiler thermostat
                       ────────────► ClimateWorker ───────────────► ClimateSensors, ClimateReadings
 main-meter 1 s power ────────────► NilmService ────────────────────► MeterEvents, ApplianceSignatures, ApplianceRuns
                                                                     │
 repositories ───────────────────► HomeService / HistoryService ───► page data cache (PeriodCache)
                                                                     │
        Blazor pages ◄───────────── in-memory states + caches ◄─────┘
        /metrics (prometheus-net) ◄─ *Metrics.cs read the same states
        PrometheusRemoteWriter ────► optional remote write of live samples (kasa_live_*)
```

The path of one reading, end to end:

1. **Discovery.** `DiscoveryWorker` wakes every 30 s and, when the configured interval has passed, scans the LAN
   (UDP broadcast for Kasa legacy, the TP-Link discovery probe, mDNS for Shelly). `PlugDiscoveryService` also serves
   „Scanează acum” and „Adaugă după IP”. New plugs go to `Plugs` (or wait for one click when auto-add is off).
2. **Polling.** `PollingWorker` reads every active plug on the base interval (10 s) and writes `Samples`; Kasa legacy
   daily/monthly counters are refreshed every 5 minutes, Shelly device info hourly. `PollFailureTracker` throttles
   the failure log lines.
3. **Live stream.** `LiveStreamWorker` reads the main meter every second and other channels adaptively (1 → 10 s).
   A raw row is stored only when the value changes (or every 60 s); a minute aggregate is built from every reading.
   `LiveStreamBroadcaster` hands the values to the in-process consumers (home page bubbles, NILM, the Prometheus
   remote writer) without going through the database.
4. **Aggregation.** `RollupWorker` (daily) rolls raw `Samples` into minute aggregates; `HistorySyncWorker` (every 6 h)
   pulls the plugs' own daily/monthly history; `ReadingsRetentionService` purges only if a retention is configured.
5. **Reports.** `EnergyReportService` builds house and boiler reports per period; `HomeService` and `HistoryService`
   cache them in `PeriodCache` (deduplicated computation on the thread pool, stale-while-revalidate, TTL 1 min for
   today, 10 min for a period in progress, until the day changes for the past).
6. **Pages and metrics.** Blazor pages read the in-memory states and the caches; `/metrics` exposes the same states
   (`kasa_*`, `heating_*`, `miele_*`, `climate_*`, `kasa_appliance_*`, `kasa_nilm_*`, `kasa_house_*`). See README
   [Monitoring with Prometheus & Grafana](../README.md#monitoring-with-prometheus--grafana).

Time handling: all instants are UTC in storage. „Days” are local days in the process time zone (`TZ`), except the
boiler's gas/heat/electricity counters, which follow the boiler's own day (`BoilerTimeZone`, see
[decisions.md](decisions.md), „Local day vs the boiler's day”).

## 3. Tables

All tables are in one SQL Server database (`kasamonitor`, created once by `deploy/sql/create-database.sql` with a
case- and accent-sensitive collation) and migrated by EF Core at start-up: `InitialCreate` since 0.12.0 (the 14
SQLite migrations were folded into it) and the migrations added after it. Strings in keys and indexes have explicit lengths (`KasaDbContext` constants);
free texts and JSON are `nvarchar(max)`; `KasaDbContext.PrepareForStore` cuts longer texts (refuses longer identifiers)
and turns NaN/±Infinity in nullable doubles into NULL before every save. Explicit transactions run inside the
execution strategy (`EnableRetryOnFailure`, compatibility level 150 = SQL Server 2019); the live batch is idempotent
(unique `(PlugId, Ts)` on `LiveSamples`, `ExecuteInTransactionAsync` with a check of the stored batch).
The only raw SQL (`HeatingRepository.ListReadingChangesAsync`, window functions) is portable to the SQLite test
database. Settings are flat key/value rows, so grouping them on tabs needs no migration.

| Table | Purpose |
|---|---|
| `Plugs` | One row per plug or meter channel: protocol, alias, role (plug / main meter / sub-meter), stream flag, NILM exclusion, single-appliance flag, archived flag |
| `Samples` | Raw polled readings (power, voltage, current, counters) |
| `OneMinuteAggregates`, `OneHourAggregates` | Rollups of `Samples` |
| `DailyEnergies`, `MonthlyEnergies` | Energy per plug per day / month (from the plugs' own counters) |
| `Settings` | All application settings and a few internal markers (NILM processing marker, last re-cluster) |
| `LiveSamples` | Raw stream rows, written on change or every 60 s; purged after `LiveRawRetentionDays` |
| `LiveMinuteAggregates` | One row per streamed channel per minute; the source of day/period totals for the main meter |
| `RelayCommands` | Audit of relay on/off commands |
| `HeatingDevices` | Boilers known through the Viessmann cloud |
| `HeatingReadings` | One row per boiler poll: burner, modulation, temperatures, gas counters |
| `HeatingEnergyDaily`, `HeatingEnergyPeriods` | Gas and energy per boiler day / period, with the highest and last value of the day counters |
| `HeatingRawFeatures` | Only the latest raw cloud answer, overwritten |
| `MeterEvents` | Steady-state steps of the main meter's power (the input of the appliance detection) |
| `ApplianceSignatures` | Clusters of events: one signature per appliance component, with name, category, plug link, flags, duration band |
| `ApplianceRuns` | A switch-on paired with its switch-off, with energy |
| `MieleDevices` | Appliances of the Miele account, alias, linked plug |
| `MieleStateSnapshots` | A row on every state change (and at least every 5 minutes) |
| `MieleCycles` | One row per program cycle: phases, the appliance's own energy/water, the linked plug's energy, source (observed / API / merged) |
| `ClimateSensors` | One row per sensor of any source: name, room, outdoor flag, archived, battery |
| `ClimateReadings` | Temperature/humidity readings, written on change or every 30 minutes |
| `ClimateDaily` | Min / time-weighted mean / max per sensor and local day, used by the history page |
| `EntityPrefs` | 0.11: per-entity display choices of the generated device page (shown, history, metrics, name, decimals) |
| `GenericDevices`, `GenericEntities` | 0.11: devices added from MQTT Discovery and their entities (description + raw discovery payload) |
| `GenericReadings` | 0.11: generic values on change (integer key, `WITHOUT ROWID`), purged after 90 days |
| `GenericHourly` | 0.11: hourly min / time-weighted mean / max / last / count / counted delta per generic entity, kept |

Retention (see README [Data retention](../README.md#data-retention)): events, signatures, runs, climate, Miele and
minute aggregates are kept without limit; only `LiveSamples` is purged by default (30 days).

## 4. Background workers and cadences

| Worker | Loop |
|---|---|
| `DiscoveryWorker` | Ticks every 30 s; scans when the configured interval has passed |
| `PollingWorker` | Every plug on the base interval (default 10 s); legacy counters every 5 min; Shelly info hourly |
| `LiveStreamWorker` | Main meter every 1 s; other channels 1–10 s adaptive; flush to the database every 10 s; reconnection backoff 1 → 30 s |
| `HistorySyncWorker` | Plugs' own history every 6 h (retry every minute while no plug has history) |
| `RollupWorker` | Daily: raw samples → minute aggregates; optional purge |
| `HeatingPollWorker` | Viessmann cloud, one bulk call per interval (default 120 s, minimum 60 s) within the daily API budget; backoff up to 30 min |
| `MieleStreamWorker` | One SSE stream; after 3 failed attempts polls `GET /devices` every 60 s and retries SSE at each backoff |
| `MieleHistorySync` | Program history after connection, every 6 h and 2 minutes after a cycle ends |
| `ClimateWorker` | Ticks every 15 s; each source on its own schedule: boiler thermostat 1 min, Govee 10 min (device list 6 h), Shelly 1 min (mDNS 5 min), Open-Meteo 15 min |
| `ClimateDailyWorker` | Fills `ClimateDaily`; recalculates the last 3 closed days, 10 minutes after midnight |
| `NilmService` | Processes the main meter's stream into events, labels them 45 s later from the plugs, refreshes its summary every 30 s, re-clusters nightly at 03:30 and trains the model |
| `HomeService` | Recomputes today and the current month every minute for the home page and its metrics |
| `PrometheusRemoteWriter` | Optional: batches live samples and pushes them (flush 1 s, buffer up to 5 min) |
| `KasaMetrics` | Refreshes the `/metrics` families from the states |
| `MqttDiscoveryWorker` | 0.11: one broker connection (reconnect 5 → 60 s, re-subscribe), one consumer channel; `GenericRecorder` flushes values every 5 s, rolls hours up every 10 min, purges daily |

Workers never talk to pages directly: they write the database and update an in-memory state; pages and metrics read
the state. One bad source cannot stop another (each source has its own try/backoff).

## 5. Integration registry (since 0.10)

An **integration** is how the app talks to a vendor: `tplink`, `shelly`, `viessmann`, `miele`, `govee`, `openmeteo`.
A **device category** says what a device is: plug, meter, boiler, appliance, climate sensor, weather source. The
relation is many-to-many (Shelly brings meters, plugs and climate sensors; Viessmann a boiler and a thermostat; four
integrations bring climate sensors), so the axis of the UI is the integration, not the category.

```
 Integrations.All (Web/Ui/Integrations.cs)
   key · vendor name · categories · settings tab component · „Adaugă” component · data page · status function
        │
        ├─► /settings/{tab}     one tab per integration (+ „Aplicație”, „Export”)
        ├─► /dispozitive        „Firmă” filter, state chips, „+ Adaugă” menu
        └─► IntegrationStatus   OK / Atenție / Eroare / Oprit / Neconfigurat, projected from existing states

 DeviceCatalog (Workers) ──► DeviceRow list: plugs, Miele devices, boilers, climate sensors  (no new table)
```

`IntegrationStatus` is computed by pure functions (`IntegrationStatusRules`) from states that already exist (the last
discovery scan and the plugs' live states, `ClimateState`, `HeatingState`, `MieleState`); nothing new is measured
or stored. `DeviceCatalog` projects the existing tables into one list of `DeviceRow`s with keys `plug:{id}`,
`miele:{id}`, `heating:{id}`, `climate:{id}`, and `/dispozitive` opens one expanded row per device as the single
place where it is configured.

### Entities and the generated page (since 0.11)

Every device can also be seen as a list of **entities** (`Core/Devices/Entities`): kind, data type, device class,
canonical unit, state class, category. Existing integrations are *projected* by an `IEntitySource` per row-key prefix
(`plug:`, `climate:`, `heating:`, `miele:`) without touching their tables; generic devices (`dev:`) are stored as entities
from the start (`GenericRecorder`, MQTT Discovery). `/dispozitive/{key}` draws any device from `EntityViewRules`. A
generic device enters house-level calculations only through an explicit adapter (0.11: climate → `ClimateRecorder`).

### Adding an integration

Taking a hypothetical cloud vendor „Acme” with climate sensors as the example:

1. **Key.** Add `IntegrationKeys.Acme = "acme"` (`Core/Devices/DeviceKinds.cs`). For climate sensors also add
   `ClimateSources.Acme` (`Core/Climate/ClimateModels.cs`) with its display name and its expected reading interval
   (used by the freshness colouring and `climate_online`).
2. **Protocol client and parser in Core.** A client class behind an interface plus a parser tested on fixtures.
   Fixtures must be anonymised or synthetic (see [CLAUDE.md](../CLAUDE.md)).
3. **Source of readings.** Climate sensors: add a branch in `ClimateWorker` with its own schedule; the generic
   `ClimateRecorder` applies the write rule (≥ 0.1 °C or ≥ 1 % RH change, or a 30-minute heartbeat) and creates the
   `ClimateSensors` row at the first value, so no migration is needed. Other categories (plugs, meters, appliances)
   need their own repository and, if the data is new, a migration.
4. **Settings.** A settings service (secrets: environment variable or a mounted file read once at start-up, never the
   database, UI or log) and a tab component in `Components/Settings`.
5. **Registry entry.** One `IntegrationInfo` in `Integrations.All`: key, vendor name, icon, categories, tab component,
   „Adaugă” component (`LanAddPanel`, `CloudConnectPanel`, or a dedicated panel), data page, and a status function
   built with `IntegrationStatusRules`.
6. **Catalog rows.** Make `DeviceCatalogRules` emit the integration's rows (for climate sources this is already generic
   via `ClimateSources`), and add the row editor component used by the expanded row.
7. **Metrics and docs.** A `*Metrics.cs` family listed in the catalog shown on `/integration`; a README section and the
   retention table row; tests for the parser, the recorder, the status rule and the page.

## 6. Version and deployment model

- **One version** in `Directory.Build.props` (`<Version>`); `AppVersion` shows it in the menu with the commit and build
  time. The README, `deploy/README.md` and the default of `deploy/build-and-push.ps1` carry the same number and are
  updated at every release.
- **Docker.** One image (`deploy/Dockerfile`, multi-stage, `linux-x64`, ML.NET included) listening on port 8910 with
  host networking (LAN discovery needs UDP broadcast and mDNS). Time zone from `TZ` (default `Europe/Bucharest`).
  Compose files for the app and an optional Prometheus/Grafana stack are under `deploy/`.
- **SQL Server.** `ConnectionStrings__Kasa` (required) points at the database; the app waits up to 120 s for the
  server at start-up, then applies the migrations (retried); `/health` says `degraded` (503) while the database does not
  answer, without stopping the process. `KasaMonitor__DataDir` (`/data`) keeps only the DataProtection keys and the
  NILM classifier. Backups are SQL Server's (`BACKUP DATABASE`, a SQL Agent job), plus the `/data` volume.
- **Stack.** `deploy/docker-compose.yml` is the Portainer stack: every setting is an environment variable, the
  secrets come from the stack's environment (`${…}`) or from mounted files (a file wins over its variable).
- **Update procedure.** Back up the database (`BACKUP DATABASE`), pull the new image, recreate the container, check `/health`,
  `/dispozitive` and `/metrics`. Version-specific notes (migrations, one-time repairs, first-start re-clustering) are
  in README [Updating](../README.md#updating).
- **Secrets** (TP-Link account, OAuth client secrets, API keys) are encrypted with ASP.NET Data Protection in the
  database where they must be stored, or read from files/environment variables where they must not be.
- **Observability.** `/metrics` (always on), optional remote write of 1 s samples, generated Prometheus and Grafana
  files from `/integration`, browser errors reported to the server log.

Operations of a particular installation (hosts, volumes, scheduling of backups, alert routing) are outside this
repository.
