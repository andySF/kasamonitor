# KasaMonitor — Deployment

Main documentation (settings, Tapo, metrics, Prometheus & Grafana): `../README.md`. The web UI page
`/integration` generates the Prometheus/Grafana configuration below with the real KasaMonitor address.

Since 0.12.0 the data are in **SQL Server 2019 or newer** (any edition; Express is limited to 10 GB per database). The
container keeps only the DataProtection keys and the NILM classifier in `/data`.

The monitoring files (`docker-compose.monitoring.yml`, `prometheus/`, `grafana/provisioning/`) are embedded in
`KasaMonitor.Web.dll` (`<EmbeddedResource>` in `src/KasaMonitor.Web/KasaMonitor.Web.csproj`; the `Dockerfile` copies
them into the build stage) and served by `/integration`. Edit them here; the next build picks them up.

## Files

| File | Purpose |
|---|---|
| `docker-compose.yml` | the KasaMonitor stack: every setting as an environment variable, secrets as `${…}` from the stack's environment, host networking, port 8910, volume `/data` |
| `sql/create-database.sql` | creates the database `kasamonitor` (collation `Latin1_General_100_CS_AS_SC`, recovery SIMPLE), the login and the user `kasamonitor` (`db_owner` of that database only) |
| `docker-compose.monitoring.yml` | optional Prometheus + Grafana |
| `prometheus/prometheus.yml` | scrape job `kasa` |
| `grafana/provisioning/` | data source, dashboard `KasaMonitor`, alert rules |
| `Dockerfile`, `build-and-push.ps1` | image build (`duobit/kasamonitor`) |
| `../tools/KasaMonitor.DataMigration` | `kasa-migrate`: one-off copy of a SQLite `kasa.db` (0.11 and earlier) into SQL Server |

## 1. Database and login (once)

As a sysadmin of the SQL Server, with `sqlcmd` (in the official SQL Server container: `/opt/mssql-tools/bin/sqlcmd`,
or `/opt/mssql-tools18/bin/sqlcmd` on newer images), choose a password for the `kasamonitor` login and run:

```bash
sqlcmd -S <server>,1433 -U sa -C -i deploy/sql/create-database.sql -v PASSWORD="<kasamonitor password>"
```

The script is idempotent (it skips what exists). The app never creates the database: it connects as `kasamonitor` and
applies its EF Core migrations (one `InitialCreate` in 0.12.0) at every start. The database collation is case- and
accent-sensitive whatever the server's collation is: MAC addresses, sensor ids and setting keys compare exactly, as they
did in SQLite.

To see the schema before the first start: `dotnet ef migrations script --project src/KasaMonitor.Data` (the SQL the app
applies).

## 2. The stack (Portainer or docker compose)

`docker-compose.yml` is the template of the stack. In Portainer: *Stacks → Add stack → Web editor*, paste the file,
adjust the plain values (broadcast networks, tariffs, Client IDs …) and set under *Environment variables*:

| Variable | Required | Value |
|---|---|---|
| `KASA_SQL_CONNECTION` | yes | `Server=<sql host>,1433;Database=kasamonitor;User Id=kasamonitor;Password=<…>;TrustServerCertificate=True`. Microsoft.Data.SqlClient encrypts by default (`Encrypt=True`); `TrustServerCertificate=True` accepts the self-signed certificate of a SQL Server container. With a certificate from a CA the host trusts, leave it out. Do not set `Encrypt=False`. |
| `KASA_DATA_DIR` | yes (the stack refuses to start without it) | absolute host path for `/data`, e.g. `/share/Container/kasamonitor/data`; on an existing installation the same directory as before (its `dp-keys/` decrypt the saved passwords and tokens) |
| `KASA_TAPO_PASSWORD`, `KASA_SHELLY_PASSWORD`, `KASA_REMOTE_WRITE_PASSWORD` | no | initial values of these passwords (better: `/settings`) |
| `KASA_MIELE_CLIENT_SECRET`, `KASA_GOVEE_API_KEY`, `KASA_MQTT_PASSWORD` | no | when they are not mounted as files (see below) |
| `KASA_NILM_API_TOKEN` | no | `X-Api-Key` of `/api/nilm/*` |

With docker compose, put the same variables in an env file next to the compose file (`--env-file`), never in the
compose file. Keep `network_mode: host` (discovery, see the README); the app reaches SQL Server at its LAN address.

At start-up the app waits up to 120 s for SQL Server (a log line every 10 s with the reason: `Waiting for the database
server (20 s of 120 s): Login failed for user 'kasamonitor'.`), then applies the migrations (3 attempts, 5 s apart, each
one a single try of at most the connect timeout, 15 s): with the server missing the process stops after about 3 minutes
and Docker restarts it; the image's HEALTHCHECK has a start period of 180 s. Later outages do not stop it: `/health` answers 503 `degraded: the database does not answer`, the live
stream keeps up to 50,000 samples in memory and stores them when the server is back, transient errors are retried by
EF Core (6 times, up to 10 s apart). Browse to `http://<host>:8910/` after ~30 s.

### Secrets: files or variables

Every secret can come from a read-only file or from a variable of the stack. **The file wins when it exists and is not
empty**, otherwise the variable. Never written to the database, the UI or the log; `/settings` shows only where a secret
came from („fișierul …” / „variabila …”).

| Secret | File (default path) | Variable |
|---|---|---|
| SQL Server password | — | inside `ConnectionStrings__Kasa` (`KASA_SQL_CONNECTION`) |
| Miele client secret | `KasaMonitor__MieleClientSecretFile` (`/run/secrets/miele-client.secret`) | `KasaMonitor__MieleClientSecret` |
| Govee API key | `GOVEE_API_KEY_FILE` / `KasaMonitor__GoveeApiKeyFile` (`/run/secrets/govee-api.key`) | `KasaMonitor__GoveeApiKey` |
| MQTT broker password (0.11) | `KasaMonitor__MqttPasswordFile` (`/run/secrets/mqtt-kasamonitor.password`) | `KasaMonitor__MqttPassword` |
| TP-Link password, Shelly password | — (saved encrypted from `/settings`) | `KasaMonitor__TapoPassword`, `KasaMonitor__ShellyPassword` (initial values) |

A file mounted on the QNAP, for example:

```yaml
    volumes:
      - /share/Container/kasamonitor/data:/data
      - /share/Container/kasamonitor/secrets/miele-client.secret:/run/secrets/miele-client.secret:ro
      - /share/Container/kasamonitor/secrets/govee-api.key:/run/secrets/govee-api.key:ro
```

Owner-only permissions on the files (`chmod 600`). Files and variables are read once at start-up (redeploy the stack
after a change). Variables are visible in `docker inspect` and in Portainer to whoever administers it; files are not.

The Miele Client ID is set in `/settings` → Miele, then the account is connected from `/aparate` („Conectează Miele”)
with KasaMonitor opened at the address the redirect URI is built from, e.g. `http://192.168.254.2:8910/miele/callback`.
For Govee, `/settings` → Govee lists the devices of the account; tick „acasă” on the thermometers to read.

## 3. Moving the data from SQLite (0.11 and earlier)

1. Update the old installation to the last SQLite release (0.11.x) and let it start once: `kasa.db` gets its last schema.
2. Stop it and take a consistent copy of `kasa.db` (the container stopped, or `sqlite3 kasa.db ".backup kasa-copy.db"`);
   a copy taken while the app runs needs its `-wal` file taken at the same instant, so prefer `.backup`.
3. Create the database (step 1), do **not** start 0.12.0 yet.
4. Copy the data, from a machine with the .NET 10 SDK that reaches SQL Server (the connection string from the
   environment, so the password does not stay in the shell history):

   ```bash
   export ConnectionStrings__Kasa='Server=<sql host>,1433;Database=kasamonitor;User Id=kasamonitor;Password=<…>;TrustServerCertificate=True'
   dotnet run --project tools/KasaMonitor.DataMigration -c Release -- --source kasa-copy.db --report migrate-report.md
   ```

   The tool applies the migrations, refuses a database that already has rows (`--truncate` replaces everything,
   `--resume` continues an interrupted copy from the first table not verified), copies every table in batches of 5,000
   rows with `SqlBulkCopy` (ids kept, identity seeds moved past them) and checks every table: `COUNT(*)` and the sum of
   every numeric column (energy, power, counters, ids), the total length of every text, the number of distinct values
of every GUID and the sum of every date, on both sides. Exit code 0 = everything verified. Measured on a
   427 MB `kasa.db` (2.85 million rows) against SQL Server 2022 on the same machine: 19 s; over a 1 Gbit/s LAN expect a
   few minutes.
5. Copy the `dp-keys/` directory of the old `/data` into the new data directory (`KASA_DATA_DIR`): the passwords and
   tokens saved in `/settings` are encrypted with those keys (without them they must be entered again), and
   `nilm-model.zip` if present.
6. Start the 0.12.0 stack, check `/health`, `/dispozitive`, `/history`.

## 4. Backup

The SQLite file is gone: back up the database with SQL Server, e.g. a SQL Agent job step

```sql
BACKUP DATABASE [kasamonitor] TO DISK = N'/var/opt/mssql/data/backups/kasamonitor/kasamonitor_<yyyyMMdd_HHmmss>.bak'
    WITH COMPRESSION, CHECKSUM, INIT;
```

every night, with the folder copied off the server (and a retention of a few days). Recovery model SIMPLE (set by
`create-database.sql`): a full backup per day is the restore point. Keep the `/data` volume (the `dp-keys/`) in the
backups too. Restore: `RESTORE DATABASE [kasamonitor] FROM DISK = …` with the stack stopped, then start it.

## Local build & push to Docker Hub

```powershell
.\deploy\build-and-push.ps1 -Version 0.12.0          # build :0.12.0 + :latest and push both
.\deploy\build-and-push.ps1 -Version 0.12.0 -NoPush   # local build only
```

## Configuration

All `KasaMonitor__*` variables are listed, with their defaults, in `docker-compose.yml` and in the settings table of
`../README.md`. The ones saved in `/settings` are only initial values (the database wins once saved); the database
connection, the data directory, the poll interval and the secrets are read at start-up.

| Var | Default | Notes |
|---|---|---|
| `ConnectionStrings__Kasa` | — (required) | SQL Server connection string; the app does not start without it |
| `KasaMonitor__DataDir` | `/data` (image) | DataProtection keys (`dp-keys/`) and `nilm-model.zip` |
| `KasaMonitor__BroadcastNetworks` | (empty → 255.255.255.255) | comma-separated IPv4 broadcast addresses, e.g. `192.168.1.255` |
| `KasaMonitor__TariffRonPerKwh` | `1.30` | price per kWh in the web UI |
| `KasaMonitor__PollIntervalSeconds` | `10` | plug poll interval |

## Troubleshooting

- **The container restarts at start-up** → the log says `The SQL Server connection string is missing` (set
  `KASA_SQL_CONNECTION`), or `Waiting for the database server …` then `Applying the database migrations failed`:
  check the address and port from the host (`network_mode: host`), the login (`Login failed for user 'kasamonitor'`),
  that the database exists (`Cannot open database "kasamonitor"`) and the certificate (`TrustServerCertificate=True`
  for a self-signed one).
- **Health endpoint 503 `degraded`** → SQL Server does not answer (stopped, network, database offline); the app keeps
  running and recovers by itself.
- **No plugs discovered** → confirm `network_mode: host` is set (some NAS UIs reset it). Without host networking the
  UDP broadcast cannot reach the LAN. Check that `BroadcastNetworks` is your LAN's broadcast address; for plugs in
  another subnet use "Add by IP" on `/dispozitive` (Adaugă).
- **Tapo plug "unsupported"** → see the reason on `/dispozitive`: missing/wrong TP-Link credentials, or a protocol such
  as TPAP (toggle *Third-Party Compatibility* in the Tapo app and restart the plug).
- **Prometheus target `kasa` DOWN** → see "Reaching KasaMonitor from Prometheus" in `../README.md`
  (`host.docker.internal` vs. the host's LAN IP).
- **Stored secret could not be decrypted** (log) → the `dp-keys/` of `/data` were lost or not copied; enter the
  passwords again in `/settings` and reconnect ViCare / Miele.
- **Healthcheck failing** → the container's HEALTHCHECK runs `dotnet KasaMonitor.Web.dll --healthcheck` which probes
  `http://localhost:8910/health`. If you changed the port via `ASPNETCORE_URLS`, the healthcheck won't match.

## Build verification

The Dockerfile uses `mcr.microsoft.com/dotnet/sdk:10.0` for the build stage and `mcr.microsoft.com/dotnet/aspnet:10.0`
for the runtime stage. Built for `linux/amd64` only — multi-arch (arm64) is out of scope.
