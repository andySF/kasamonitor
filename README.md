# KasaMonitor

Self-hosted monitoring for TP-Link smart plugs with energy metering: **Kasa** (e.g. KP115, legacy XOR protocol on
port 9999) and **Tapo** (e.g. P110/P115, KLAP and AES "securePassthrough" protocols). Blazor Server app (.NET 10)
with SQLite, a web UI on port 8910 and a Prometheus `/metrics` endpoint. An optional Prometheus + Grafana stack
with a ready-made dashboard and alert rules is included.

Version: **0.2.0** (`Directory.Build.props`). Docker image: `duobit/kasamonitor`.

## Quick start

```bash
git clone https://github.com/andySF/kasamonitor.git
cd kasamonitor
# edit deploy/docker-compose.yml: KasaMonitor__BroadcastNetworks = your LAN broadcast address (e.g. 192.168.1.255)
docker compose -f deploy/docker-compose.yml up -d
```

Open `http://<host>:8910/`. Plugs on the same LAN are discovered within a few seconds; for Tapo plugs enter your
TP-Link (Tapo app) account in `/settings` and press **Scanează acum** on `/plugs`.

`network_mode: host` is required: discovery uses UDP broadcasts (9999 for Kasa, 20002 for Tapo) that do not leave
a Docker bridge network. The database and the key ring live in `deploy/data/` (volume `./data:/data`).

## Features

- **Automatic discovery** every N minutes: UDP broadcast on 9999 (Kasa legacy) and the TP-Link discovery probe
  on UDP 20002 (Tapo / newer Kasa). New plugs are added automatically (setting "Adaugă automat").
- **Scan now** (`/plugs`, "Scanează acum"): runs discovery immediately and lists each plug as new / added / known /
  unsupported; with auto-add off, new plugs are added with one click.
- **Add by IP** (`/plugs`): tries legacy TCP 9999, then the UDP 20002 probe (+ KLAP/AES), then KLAP and AES
  directly (with TP-Link credentials). Useful for plugs on another subnet, where broadcasts do not reach.
- **Withdraw / restore** (`/plugs`, "Retrage" / "Readu"): a withdrawn plug is no longer polled and disappears from
  `/metrics` and the dashboard immediately; "Arată retrase" shows it again in the list.
- **Unsupported plugs** (they answer discovery but cannot be queried) are listed with IP/MAC and the reason:
  missing TP-Link credentials, authentication failed, Kasa firmware on KLAP (not implemented), or an unknown
  protocol such as TPAP. For TPAP the UI shows the usual fix: in the Tapo app, device settings → *Third-Party
  Compatibility* off and on again, then restart the plug; it normally comes back on KLAP.

The web UI texts are in Romanian.

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
| — | `KasaMonitor__DbPath` | `/data/kasa.db` (image) | |

The DataProtection keys are kept next to the database, in `<db dir>/dp-keys` (`/data/dp-keys` in the container,
on the persistent volume). If that directory is lost, the stored password cannot be decrypted and must be entered
again in `/settings` (the log shows `Stored secret could not be decrypted`). On Linux the keys are stored
unencrypted on disk (ASP.NET logs "No XML encryptor configured" at startup); protect the volume.

Do not commit credentials; prefer `/settings`.

## Monitoring with Prometheus & Grafana

### Quick start

```bash
docker compose -f deploy/docker-compose.yml up -d             # KasaMonitor (host network, port 8910)
docker compose -f deploy/docker-compose.monitoring.yml up -d  # Prometheus (9090) + Grafana (3000)
```

- Grafana: `http://<host>:3000`, user `admin`, password from `GRAFANA_ADMIN_PASSWORD` (default `admin`; set it
  before the first start, e.g. `GRAFANA_ADMIN_PASSWORD=... docker compose -f deploy/docker-compose.monitoring.yml up -d`).
  The **KasaMonitor** dashboard is provisioned (folder *KasaMonitor*, also the home dashboard), together with the
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

Every series has the labels `alias` (plug name, MAC if empty), `mac`, `model` (e.g. `KP115`, `P115`, without the
region suffix) and `protocol` (`legacy` | `klap` | `aes`).

| Metric | Type | Unit | Legacy (Kasa) source | Tapo (KLAP/AES) source |
|---|---|---|---|---|
| `kasa_online` | gauge | 0/1 | 1 when the last poll succeeded | same |
| `kasa_power_load` | gauge | W | `emeter.get_realtime.power_mw` | `get_energy_usage.current_power` |
| `kasa_voltage` | gauge | V | `voltage_mv` | `get_emeter_data.voltage_mv` (absent if not reported) |
| `kasa_current` | gauge | A | `current_ma` | `get_emeter_data.current_ma` (absent if not reported) |
| `kasa_relay_state` | gauge | 0/1 | 1 when the poll succeeds | same |
| `kasa_power_total` | counter | Wh, cumulative | `total_wh` (plug lifetime total) | `month_energy` (resets monthly on the plug) |
| `kasa_rssi` | gauge | dBm | declared, not set yet | same |

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

Transports implement `ISmartTransport`; TPAP (SPAKE2+) can be added as another implementation. Daily/monthly
history (`HistorySyncWorker`) exists only for legacy plugs.

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
.\deploy\build-and-push.ps1 -Version 0.2.0          # build :0.2.0 + :latest and push both
.\deploy\build-and-push.ps1 -Version 0.2.0 -NoPush  # local build only
```

or manually:

```bash
docker build -t duobit/kasamonitor:0.2.0 -t duobit/kasamonitor:latest -f deploy/Dockerfile .
docker push duobit/kasamonitor:0.2.0
docker push duobit/kasamonitor:latest
```

To publish under your own Docker Hub account, change the image name (`-Repo` parameter of the script and
`image:` in `deploy/docker-compose.yml`).

## Updating

1. Back up `deploy/data/kasa.db` (with the container stopped, or `sqlite3 kasa.db ".backup kasa.bak"`). Upgrading
   from 0.1.0 runs the migration `AddPlugProtocol`, which only adds the columns `Plugs.Protocol` and
   `Plugs.UnsupportedReason`; existing data stays and existing plugs become `legacy`.
2. `docker compose -f deploy/docker-compose.yml pull && docker compose -f deploy/docker-compose.yml up -d`
   (Portainer: *Update the stack* with *Re-pull image and redeploy*).
3. Check `http://<host>:8910/health`, `/plugs` (Scan now) and `curl -s http://<host>:8910/metrics | grep kasa_online`.

See also `deploy/README.md` (troubleshooting).
