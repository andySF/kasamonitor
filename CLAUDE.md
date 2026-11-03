# KasaMonitor

Monitors a house's electricity and gas on the local network: TP-Link Kasa/Tapo and Shelly plugs and meters
(polling plus a 1 s live stream of the main meter), a Viessmann boiler and Miele appliances (cloud), climate sensors
(Govee, Shelly H&T, Open-Meteo), with a Blazor UI in Romanian, Prometheus metrics, and appliance detection (NILM)
from the main meter. .NET 10, EF Core + SQLite, Blazor, ML.NET. Features and settings: `README.md`.

## Build and test

```bash
dotnet build KasaMonitor.sln        # warnings are errors (TreatWarningsAsErrors)
dotnet test KasaMonitor.sln         # about 1,030 tests as of 0.10.x, 3 skipped on purpose
```

The 3 skipped tests are opt-in checks on a real database copy (they need the environment variable
`KASA_NILM_TESTDB`). Treat the count as a reference: a release should not lower it without a reason.
Run `dotnet build` before `dotnet test --no-build`; never run two builds in one tree at once.

## Conventions

- **Language.** Code, comments, commit messages and these docs are in English. Everything the user sees in the UI is
  in Romanian, with correct diacritics.
- **No secrets, no real identifiers in the repository.** No tokens, passwords, API keys or account names. Test
  fixtures use synthetic or anonymised data; MAC addresses and serial numbers in tests are `aabbccddee00`-style
  placeholders. Secrets reach the app through environment variables or mounted files read once at start-up and are
  never written to the database, the UI or the log.
- **Version.** One number in `Directory.Build.props`. At every release also update the README (version, the section
  of the feature, the metrics table if it changed, the *Updating* notes), `deploy/README.md` and the default of
  `deploy/build-and-push.ps1`. The README is updated with every version, not afterwards.
- **Rules are pure.** Put decisions in `KasaMonitor.Core` as functions with unit tests; workers and pages only wire
  them. A rule exists in exactly one place (period names, thresholds, decimal rules, the NILM merge).
- **Tests.** A new rule gets a unit test; a new table or migration gets a repository test and a migration test; a
  new page gets a page test in `KasaMonitor.Web.ApiTests`. Use measured numbers from real data in test names or
  comments when a threshold came from a measurement.

## Structure

`src/KasaMonitor.Core` (rules, protocols), `.Data` (EF Core), `.Workers` (background services, states),
`.Web` (Blazor, `/metrics`, API); tests mirror them under `tests/`; `deploy/` has the Dockerfile and compose files.
Layers, data flow, tables, workers and how to add an integration: `docs/architecture.md`.
Why things are the way they are: `docs/decisions.md`. The appliance detection: `docs/nilm.md`. What the plugs' local
APIs offer: `docs/device-api-capabilities.md`.

## Do not

- Do not run the app on the port a production instance uses (the Docker image listens on 8910; development uses
  5174 from `launchSettings.json`), and do not point a development run at a production database.
- Do not commit database files (`*.db`, `*.db-wal`, `*.db-shm`, `data/`) or anything from a real installation.
- Do not call real devices or clouds from tests; use fakes and fixtures.
- Do not add a migration, a table or a setting without updating the README.

Operating an installation (deployment, backups, monitoring, alert routing) is outside this repository.
