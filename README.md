# KasaMonitor

Self-hosted monitoring for TP-Link smart plugs with energy metering: **Kasa** (e.g. KP115, legacy XOR protocol on
port 9999) and **Tapo** (e.g. P110/P115, KLAP and AES "securePassthrough" protocols), plus **Shelly Gen2/Gen3**
plugs and energy meters (e.g. Shelly EM Gen3, local JSON-RPC, read-only). Blazor Server app (.NET 10)
with SQLite, a web UI on port 8910 and a Prometheus `/metrics` endpoint. An optional Prometheus + Grafana stack
with a ready-made dashboard and alert rules is included.

Version: **0.3.0** (`Directory.Build.props`). Docker image: `duobit/kasamonitor`.

## Quick start

```bash
git clone https://github.com/andySF/kasamonitor.git
cd kasamonitor
# edit deploy/docker-compose.yml: KasaMonitor__BroadcastNetworks = your LAN broadcast address (e.g. 192.168.1.255)
docker compose -f deploy/docker-compose.yml up -d
```

Open `http://<host>:8910/`. Plugs on the same LAN are discovered within a few seconds; for Tapo plugs enter your
TP-Link (Tapo app) account in `/settings` and press **Scanează acum** on `/plugs`.

`network_mode: host` is required: discovery uses UDP broadcasts (9999 for Kasa, 20002 for Tapo) and mDNS multicast
(224.0.0.251:5353 for Shelly) that do not leave a Docker bridge network. The database and the key ring live in `deploy/data/` (volume `./data:/data`).

## Features

- **Automatic discovery** every N minutes: UDP broadcast on 9999 (Kasa legacy) and the TP-Link discovery probe
  on UDP 20002 (Tapo / newer Kasa). New plugs are added automatically (setting "Adaugă automat").
- **Scan now** (`/plugs`, "Scanează acum"): runs discovery immediately and lists each plug as new / added / known /
  unsupported; with auto-add off, new plugs are added with one click.
- **Add by IP** (`/plugs`): tries legacy TCP 9999, then the UDP 20002 probe (+ KLAP/AES), then KLAP and AES
  directly (with TP-Link credentials), then Shelly (`GET /rpc/Shelly.GetDeviceInfo`). Useful for devices on another
  subnet, where broadcasts and mDNS do not reach.
- **Withdraw / restore** (`/plugs`, "Retrage" / "Readu"): a withdrawn plug is no longer polled and disappears from
  `/metrics` and the dashboard immediately; "Arată retrase" shows it again in the list.
- **Unsupported plugs** (they answer discovery but cannot be queried) are listed with IP/MAC and the reason:
  missing TP-Link credentials, authentication failed, Kasa firmware on KLAP (not implemented), or an unknown
  protocol such as TPAP. For TPAP the UI shows the usual fix: in the Tapo app, device settings → *Third-Party
  Compatibility* off and on again, then restart the plug; it normally comes back on KLAP.

- **Device details** (Dashboard, "Details" on a plug card): settings read from the plug on demand (cached 60 s) —
  relay, uptime, default state after power loss, LED, auto-off / countdown, power protection, Wi-Fi signal, firmware,
  device time, cloud, schedules. Read-only; see `docs/device-api-capabilities.md` for everything the local APIs offer.
- **Browser errors in the server log**: JavaScript errors and the Blazor error banner are reported to
  `POST /api/client-error` and logged at Warning (category `KasaMonitor.ClientErrors`), so `docker logs` shows them.

The web UI uses Bootstrap 5 and Bootstrap Icons, served locally (no CDN). Texts on Plugs/Settings/Integration are in Romanian.

## Settings

Discovery settings and the TP-Link account are edited in `/settings`, stored in the SQLite `Settings` table and
applied without a restart. Environment variables are **initial values only**: they are used while the key has
not been saved from `/settings`.

| Setting (`/settings`) | Environment variable (initial value) | Default | Notes |
|---|---|---|---|
| Automatic discovery | `KasaMonitor__AutoDiscoveryEnabled` | `true` | |
| Discovery interval (minutes) | `KasaMonitor__AutoDiscoveryIntervalMinutes` | `5` | 1–1440 |
| Broadcast networks | `KasaMonitor__BroadcastNetworks` | empty → `255.255.255.255` | comma-separated IPv4, e.g. `192.168.1.255` |
| Auto-add discovered plugs | `KasaMonitor__AutoAddDiscoveredPlugs` | `true` | |
| TP-Link user | `KasaMonitor__TapoUsername` | — | Tapo app account e-mail |
| TP-Link password | `KasaMonitor__TapoPassword` | — | stored encrypted (ASP.NET DataProtection), shown masked; used for KLAP and AES |
| Tariff per kWh | `KasaMonitor__TariffRonPerKwh` | `1.30` | web UI cost; read at startup |
| Poll interval (s) | `KasaMonitor__PollIntervalSeconds` | `10` | read at startup |
| Shelly discovery | `KasaMonitor__ShellyDiscoveryEnabled` | `true` | mDNS `_shelly._tcp.local` + the probe hosts below |
| Shelly password | `KasaMonitor__ShellyPassword` | — | optional; admin password of Shelly devices with authentication on (digest auth, user `admin`); stored encrypted like the TP-Link password |
| Shelly probe hosts | `KasaMonitor__ShellyProbeHosts` | — | optional; comma-separated IPs, ranges (`192.168.1.10-20`) or CIDR (`/22`–`/32`, max. 1024 addresses), each probed with `Shelly.GetDeviceInfo` (1 s) on every discovery |
| Keep raw readings (months) | `KasaMonitor__ReadingsRetentionMonths` | `0` = unlimited | raw 10-second readings only; daily history and minute aggregates are never deleted. Saving a value that would delete existing readings asks for confirmation with the exact row count |
| — | `KasaMonitor__DbPath` | `/data/kasa.db` (image) | |

The DataProtection keys are kept next to the database, in `<db dir>/dp-keys` (`/data/dp-keys` in the container,
on the persistent volume). If that directory is lost, the stored password cannot be decrypted and must be entered
again in `/settings` (the log shows `Stored secret could not be decrypted`). On Linux the keys are stored
unencrypted on disk (ASP.NET logs "No XML encryptor configured" at startup); protect the volume.

Do not commit credentials; prefer `/settings`.

## Monitoring with Prometheus & Grafana

**Start with the `/integration` page** of the web UI ("Integrare" in the menu, `http://<host>:8910/integration`).
It shows everything in one place, filled in with this server's real address:

1. **What is exposed now** — the `/metrics` URL (with `network_mode: host` it is served on every IP of the
   machine; all of them are listed), a live table of the `kasa_*` metrics (type, unit, description, number of
   series, example value), the last scrapes (time, client IP, User-Agent; "no scrape yet" until Prometheus is
   configured) and the plugs that are *not* exported (withdrawn or unsupported).
2. **Prometheus** — a ready `scrape_configs` snippet and a downloadable `prometheus.yml` with the real
   `host:port`, the `host.docker.internal` / `extra_hosts` explanation, a link to `/targets` and a **Check**
   button that queries `<prometheus>/api/v1/targets` from the server (5 s timeout) and tells whether job `kasa` is up.
3. **Grafana** — download the dashboard and the alert rules (optionally rewritten for your data source UID),
   manual import and provisioning instructions, and a **complete bundle** (zip: `docker-compose.monitoring.yml`,
   `prometheus/prometheus.yml` with the real target, `grafana/provisioning/`).

The files offered there are the ones in `deploy/` (embedded in the application at build time), so the page,
the bundle and this repository never disagree. The rest of this section describes the same setup by hand.

### Quick start

```bash
docker compose -f deploy/docker-compose.yml up -d             # KasaMonitor (host network, port 8910)
docker compose -f deploy/docker-compose.monitoring.yml up -d  # Prometheus (9090) + Grafana (3000)
```

- Grafana: `http://<host>:3000`, user `admin`, password from `GRAFANA_ADMIN_PASSWORD` (default `admin`; set it
  before the first start, e.g. `GRAFANA_ADMIN_PASSWORD=... docker compose -f deploy/docker-compose.monitoring.yml up -d`).
  The **KasaMonitor** dashboard is provisioned (folder *KasaMonitor*), together with the
  Prometheus data source (uid `prometheus`) and two alert rules.
- Prometheus: `http://<host>:9090`, retention 10 years (`--storage.tsdb.retention.time=10y`, volume
  `prometheus_data`). *Status → Targets* must show job `kasa` UP.
- Image versions can be overridden with `PROMETHEUS_VERSION` / `GRAFANA_VERSION`.

Files:

| File | Purpose |
|---|---|
| `deploy/docker-compose.monitoring.yml` | Prometheus + Grafana services and volumes |
| `deploy/prometheus/prometheus.yml` | scrape job `kasa` → `host.docker.internal:8910` |
| `deploy/grafana/provisioning/datasources/prometheus.yml` | data source, fixed uid `prometheus` |
| `deploy/grafana/provisioning/dashboards/dashboards.yml` + `kasamonitor.json` | dashboard provider and the dashboard |
| `deploy/grafana/provisioning/alerting/rules.yml` | alert rules "plug offline" (15 min) and "no data" (10 min) |

**Reaching KasaMonitor from Prometheus.** KasaMonitor uses host networking, so it listens on the host's port 8910.
The Prometheus container reaches the host as `host.docker.internal`: built into Docker Desktop, and mapped on Linux
by `extra_hosts: host.docker.internal:host-gateway` (Docker 20.10+). If that does not work (old Docker, firewall on
the Docker bridge) or KasaMonitor runs on another machine, put the host's LAN IP in `deploy/prometheus/prometheus.yml`,
e.g. `targets: ["192.168.1.10:8910"]`, and restart Prometheus. An existing Prometheus only needs the same `kasa`
job; import `kasamonitor.json` and choose your data source (or give it the uid `prometheus`).

### Dashboard

Stat panels (power now, plugs online/total, energy and cost for 24 h and 30 days), a table per plug (online, relay,
W, V, A, kWh 24 h / 30 d, cost, last online, `model`, `protocol`), and per-plug time series for power, energy per day,
online/offline, relay, voltage and current (legend `{{alias}}`). Dashboard variables at the top: `tariff` (price per
kWh, default 1.11) and `currency` (only used in titles).

Energy is computed as the **sum of the counter's 5-minute increases, clamped at 0**:
`sum_over_time(clamp_min(kasa_power_total - kasa_power_total offset 5m, 0)[<range>:5m]) / 1000`, not with
`increase()`. `kasa_power_total` is re-seeded from the plug's own total when KasaMonitor restarts (and Tapo plugs
report month-to-date energy), so the series can go down a little; `increase()` treats any decrease as a counter
reset and would count the whole cumulative value as new consumption. The trade-off: a scrape gap longer than
5 minutes loses the energy of that gap.

### Metrics (`/metrics`)

Every series has the labels `alias` (plug name, MAC if empty), `mac`, `model` (e.g. `KP115`, `P115`,
`S3EM-002CXCEU`, without the region suffix) and `protocol` (`legacy` | `klap` | `aes` | `shelly-rpc`). For the
channels ≥ 1 of a multi-channel Shelly meter `mac` is `<mac>-<channel>` (channel 0 keeps the plain MAC).

| Metric | Type | Unit | Legacy (Kasa) source | Tapo (KLAP/AES) source |
|---|---|---|---|---|
| `kasa_online` | gauge | 0/1 | 1 when the last poll succeeded | same |
| `kasa_power_load` | gauge | W | `emeter.get_realtime.power_mw` | `get_energy_usage.current_power` |
| `kasa_voltage` | gauge | V | `voltage_mv` | `get_emeter_data.voltage_mv` (absent if not reported) |
| `kasa_current` | gauge | A | `current_ma` | `get_emeter_data.current_ma` (absent if not reported) |
| `kasa_relay_state` | gauge | 0/1 | 1 when the poll succeeds | same |
| `kasa_power_total` | counter | Wh, cumulative | `total_wh` (plug lifetime total) | `month_energy` (resets monthly on the plug) |
| `kasa_rssi` | gauge | dBm | declared, not set yet | same |

Shelly (`shelly-rpc`) sources, per measured channel: `kasa_power_load` = `em1:N.act_power` (signed: negative with a
reversed clamp or exported energy) / `pm1:N.apower` / `switch:N.apower` / `em:0.total_act_power`; `kasa_voltage`,
`kasa_current`; `kasa_power_total` = `em1data:N.total_act_energy` / `aenergy.total` / `emdata:0.total_act` (Wh,
device lifetime); `kasa_relay_state` = `switch:N.output` (EM Gen3: `switch:0` on channel 0; no series when the device
has no relay). Metrics only exported for devices that report them (Shelly):

| Metric | Type | Unit | Shelly source |
|---|---|---|---|
| `kasa_power_factor` | gauge | ratio | `em1:N.pf` / `pf` |
| `kasa_apparent_power_va` | gauge | VA | `em1:N.aprt_power` / `em:0.total_aprt_power` |
| `kasa_frequency_hz` | gauge | Hz | `em1:N.freq` / `freq` |
| `kasa_energy_returned_wh_total` | counter | Wh, cumulative | `em1data:N.total_act_ret_energy` / `ret_aenergy.total` |
| `kasa_device_temperature_c` | gauge | °C | `switch:N.temperature.tC` |

Note on `kasa_power_total`: it is exported as a Prometheus counter, but it is **not a strictly monotonic counter**
across KasaMonitor restarts (it is re-seeded from the plug's total, which can be slightly lower). Use the 5-minute
increase method above, not `rate()`/`increase()`, for energy.

Withdrawn or unsupported plugs have no series. Renaming a plug removes the series with the old alias.

### Alerts and notifications

Two rules are provisioned in the folder *KasaMonitor* (Grafana → Alerting → Alert rules):

- **KasaMonitor: plug offline** — `kasa_online < 1` for 15 minutes, one alert per plug.
- **KasaMonitor: no data** — `count(kasa_online) or vector(0) < 1` for 10 minutes (KasaMonitor down, scrape failing,
  or no plugs).

No contact point is provisioned, so alerts are visible in Grafana but not sent anywhere. To get notifications:

1. Grafana → Alerting → **Contact points** → *Add contact point*, e.g. type **Telegram** (bot token from
   @BotFather and the chat ID), or e-mail (requires `GF_SMTP_*` settings), Slack, webhook, ...
2. Alerting → **Notification policies** → set it as the default contact point (or add a policy matching
   `severity=warning|critical`).

You can also provision a contact point as a file (`deploy/grafana/provisioning/alerting/contactpoints.yml`); keep
tokens out of git (use `$__env{TELEGRAM_BOT_TOKEN}` style environment references).

Your own rules (e.g. "the UPS plug draws 0 W for 10 minutes": `kasa_power_load{alias="My UPS"} < 1`, for 10m) can
be added in the UI or next to `rules.yml`; an example is commented at the end of that file.

## Tapo protocols

Implemented natively (no NuGet package), following python-kasa (`transports/klaptransport.py`,
`transports/aestransport.py`, `protocols/smartprotocol.py`).

- **Discovery** — TDP datagram on UDP 20002 (16-byte header `>BBHHBBII`, CRC32; both the plain 16-byte probe and
  the RSA-key probe are sent). The JSON answer gives `device_type`, `device_model`, `mac`, `ip`,
  `mgt_encrypt_schm.encrypt_type`, `http_port`, `lv`. Transport selection for `SMART.*` devices: `KLAP` → `klap`,
  `AES` → `aes`, anything else (TPAP, ...) → unsupported, with the hint above.
- **KLAP** — `POST /app/handshake1` with a 16-byte local seed → remote seed + hash and cookie `TP_SESSIONID`;
  `auth_hash = sha256(sha1(user) + sha1(password))` (v2); check `sha256(local + remote + auth)`, then
  `POST /app/handshake2` with `sha256(remote + local + auth)`. Requests: `POST /app/request?seq=N`, AES-128-CBC
  with key/IV/signature derived from the seeds (`lsk` / `iv` / `ldk`), body = `sha256(sig + seq + cipher) + cipher`.
- **AES (securePassthrough)** — `POST /app` `{"method":"handshake","params":{"key":"<RSA-1024 public key>"}}` →
  RSA-encrypted AES key + IV and cookie `TP_SESSIONID`; `login_device` through `securePassthrough` with
  `username = base64(hex(sha1(email)))` and, for login v2, `password2 = base64(hex(sha1(password)))` (v1:
  `password = base64(password)`) → token; further requests go to `/app?token=<token>`. v2 is tried first, then v1.
- **Methods** — `get_device_info` (alias = base64 `nickname`, model, firmware), `get_energy_usage` (`current_power`
  mW, `month_energy` Wh), `get_emeter_data` (`voltage_mv`, `current_ma`).

Transports implement `ISmartTransport`; TPAP (SPAKE2+) can be added as another implementation. Daily history (`HistorySyncWorker`, every 6 h): legacy plugs via `emeter.get_daystat`/`get_monthstat`; Tapo plugs
via `get_energy_data` (interval 1440 = daily Wh, last 60 days, one request per month with a fallback to the quarter
start some firmware require), upserted per plug and day.

## Shelly Gen2 / Gen3

Shelly devices of the second and third generation (local JSON-RPC over HTTP, `GET http://<ip>/rpc/<Method>`), protocol
`shelly-rpc`. Verified on a **Shelly EM Gen3** (`S3EM-002CXCEU`, firmware 1.6.x); other Gen2+ devices are mapped from
the official component schema without having been tested:

| Device (examples) | Components read | Channels |
|---|---|---|
| Shelly EM Gen3, Shelly Pro EM | `em1:N` + `em1data:N` | one plug per channel (EM Gen3: 2) |
| Shelly Pro 3EM | `em:0` + `emdata:0` | one plug with the three-phase totals |
| Shelly Plus PM Mini | `pm1:N` | one per `pm1` |
| Shelly Plus Plug S, Plus 1PM, Pro xPM | `switch:N` with `apower` | one per metered switch |

- **Discovery** — mDNS query for `_shelly._tcp.local` (TXT `gen`, `app`; devices with `gen` < 2 are ignored) sent to
  224.0.0.251:5353 from an ephemeral port on every IPv4 interface (on a multi-homed host the default multicast route
  often points at a Docker/Hyper-V bridge; responders answer by unicast), plus the optional *Shelly probe hosts*
  list for devices mDNS does not reach. Each answer is read with `Shelly.GetDeviceInfo`, `Shelly.GetStatus` and
  `Shelly.GetConfig`. Add by IP on `/plugs` tries Shelly after legacy and Tapo. Components are found from the keys of
  `Shelly.GetStatus`, never from the model.
- **Channels** — one plug per measured channel, identity (MAC, channel). Default alias: the channel name
  (`em1:N.name`), otherwise the device name / id plus ` ch0`, ` ch1`; rename on `/plugs` like any plug.
- **Password** — devices with authentication on (`auth_en: true`) need the *Shelly password* in `/settings`
  (HTTP digest, RFC 7616, `SHA-256`, user `admin`). Without it they are listed as `nesuportat: parolă Shelly lipsă`.
- **Polling** — one `Shelly.GetStatus` per device and cycle (all channels, 3 s timeout); `Shelly.GetDeviceInfo` +
  `Shelly.GetConfig` the first time and then hourly (model, firmware, default aliases).
- **History** — meters with `em1data` (EM Gen3, Pro EM): daily kWh from the device's per-minute records
  (`EM1Data.GetData`, one request per local day, 60 days back on the first sync, then from the last stored day).
  Other Shelly devices keep no history on the device: daily kWh comes from KasaMonitor's own readings of the energy
  counter. A day already stored is never replaced by a smaller value. A day whose records could not all be read
  (page limit reached) is not stored and is retried at the next sync. Records are stamped with the device's own
  clock: minutes written before the device synchronised its time (e.g. right after a reboot) can land on the wrong
  day, and records dated more than 5 minutes in the future are ignored.
- **Meters vs. plugs** — a meter on the main panel already includes the plugs behind it, so totals leave out models
  matching `S3EM.*|SPEM.*` (Dashboard header, History "Toate", and the Grafana stats via `model!~"S3EM.*|SPEM.*"`).
  The Grafana dashboard has a row *Contoare (Shelly EM)* with power, power factor, voltage and daily energy per channel.
- **Reversed clamp** — the Dashboard card shows „inversat?” when a Shelly channel reads below −10 W (simple rule on
  the current reading, accepted instead of "negative for a while"); fix the clamp direction (`reverse`) in the Shelly
  app if it is not real export.
- **Read-only** — only `Shelly.Get*`, `EM1Data.Get*` are called: KasaMonitor never switches the relay
  (`Switch.Set`), never changes configuration (`*.SetConfig`), never reboots or updates the device.

See `docs/device-api-capabilities.md` for what is shown, what exists and is not shown, and what would need writes.

## Data retention

| Data | Table | Written | Deleted |
|---|---|---|---|
| Raw readings (every poll, default 10 s) | `Samples` | ≈ 128 bytes per reading ≈ 1.1 MB per plug per day ≈ 400 MB per plug per year | never by default; only if `ReadingsRetentionMonths` > 0 (daily purge, in batches) |
| Minute aggregates | `OneMinuteAggregates` | ≈ 120 bytes per plug-minute ≈ 65 MB per plug per year | never |
| Daily / monthly energy | `DailyEnergy`, `MonthlyEnergy` | one row per plug per day / month | never |

Up to and including 0.2.1, raw readings older than 30 days were deleted automatically; from the next version nothing is deleted unless you set
a retention. SQLite does not shrink the file after deletions (the space is reused); run `VACUUM` manually if needed.

## Logs

- `Discovery on <networks>: found N, new N, unsupported N (auto-add on|off)` — Information, every scan.
- `Discovered new plug <mac> (<alias>, <model>, <protocol>) at <ip> -> Id <id>` — Information.
- `Poll failed for plug <alias> (<ip>, <protocol>), N consecutive failures: <reason>` — Warning, on the first
  failure, then after 10 more consecutive failures or 10 minutes, whichever comes first, per plug.
- `Plug <alias> (<ip>) is responding again after N failed polls` — Information.
- `History sync failed for plug <alias> (<ip>, <id>)` — Warning.

## Build and test

```bash
dotnet build KasaMonitor.sln
dotnet test KasaMonitor.sln
```

## Docker image

From the repository root, with Docker running and `docker login` done once:

```powershell
.\deploy\build-and-push.ps1 -Version 0.3.0          # build :0.3.0 + :latest and push both
.\deploy\build-and-push.ps1 -Version 0.3.0 -NoPush  # local build only
```

or manually:

```bash
docker build -t duobit/kasamonitor:0.3.0 -t duobit/kasamonitor:latest -f deploy/Dockerfile .
docker push duobit/kasamonitor:0.3.0
docker push duobit/kasamonitor:latest
```

To publish under your own Docker Hub account, change the image name (`-Repo` parameter of the script and
`image:` in `deploy/docker-compose.yml`).

## Updating

1. Back up `deploy/data/kasa.db` (with the container stopped, or `sqlite3 kasa.db ".backup kasa.bak"`). Upgrading
   from 0.1.0 runs the migration `AddPlugProtocol`, which only adds the columns `Plugs.Protocol` and
   `Plugs.UnsupportedReason`; existing data stays and existing plugs become `legacy`. Upgrading to 0.3.0 runs
   `AddPlugChannel`: adds `Plugs.Channel` (0 for every existing plug) and replaces the unique index on `Mac` with one
   on (`Mac`, `Channel`); no other table is touched. Downgrading to 0.2.x after that (the migration's `Down`) needs
   the rows with `Channel > 0` deleted first, otherwise re-creating the unique index on `Mac` fails.
2. `docker compose -f deploy/docker-compose.yml pull && docker compose -f deploy/docker-compose.yml up -d`
   (Portainer: *Update the stack* with *Re-pull image and redeploy*).
3. Check `http://<host>:8910/health`, `/plugs` (Scan now) and `curl -s http://<host>:8910/metrics | grep kasa_online`.

See also `deploy/README.md` (troubleshooting).
