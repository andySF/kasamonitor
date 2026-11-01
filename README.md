# KasaMonitor

Self-hosted monitoring for TP-Link smart plugs with energy metering: **Kasa** (e.g. KP115, legacy XOR protocol on
port 9999) and **Tapo** (e.g. P110/P115, KLAP and AES "securePassthrough" protocols), plus **Shelly Gen2/Gen3**
plugs and energy meters (e.g. Shelly EM Gen3, local JSON-RPC; the only write is the relay switch, after a confirmation), and a
**Viessmann gas boiler** read through the ViCare cloud API (read-only). Blazor Server app (.NET 10)
with SQLite, a web UI on port 8910 and a Prometheus `/metrics` endpoint. An optional Prometheus + Grafana stack
with a ready-made dashboard and alert rules is included.

Version: **0.7.0** (`Directory.Build.props`). Docker image: `duobit/kasamonitor`.

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
- **Roles** (`/plugs`): every channel is a *plug* (counts in the totals), the *main meter* of the house (one per
  installation) or a *sub-meter*. See [Roles, live stream, Contor page, relay](#roles-live-stream-contor-page-relay).
- **Live stream**: the main meter every second, plugs (Shelly, Kasa, Tapo) every 1–10 s (adaptive), raw rows only on
  change; minute aggregates from every reading.
- **Contor page** (`/contor`): the house in real time (1 s), rest of the house, today per minute, energy per day, relay.
- **Gas boiler** (`/centrala`, menu „Centrală”): a Viessmann boiler through the ViCare cloud API: burner, modulation,
  temperatures, hot water, messages, gas per day for hot water vs heating with cost, raw features. See
  [Gas boiler (Viessmann ViCare)](#gas-boiler-viessmann-vicare).
- **Appliance detection (NILM)** (`/consumatori`, menu „Consumatori”): switch-on/off events of the appliances found in
  the main meter's 1-second power, grouped into signatures (one per appliance), labelled automatically from the plugs,
  by you, or by a nightly ML.NET classifier; runs, energy per day, „unidentified” energy; cards „Acum pornite” and
  „Azi” on the Dashboard and `/contor`. See [Appliance detection (NILM)](#appliance-detection-nilm).
- **Version in the menu**: „v0.7.0” at the right of the navigation bar; the tooltip shows the commit and the build time.
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
| Live stream | `KasaMonitor__LiveStreamEnabled` | `true` | stream of the channels ticked „flux 1 s” on `/plugs` |
| Live stream base interval (s) | `KasaMonitor__LiveStreamIntervalSeconds` | `1` | minimum 1; the main meter's interval, the plugs' fastest (they slow down to 10 s when quiet) |
| Keep raw live samples (days) | `KasaMonitor__LiveRawRetentionDays` | `30` | `0` = unlimited; the minute aggregates are never deleted; a smaller value asks for confirmation |
| Allow relay control | `KasaMonitor__AllowRelayControl` | `true` | shows the Shelly relay switch (`Switch.Set`); off = hidden and refused. No authentication in KasaMonitor: LAN only |
| Burst trigger (W) | `KasaMonitor__LiveBurstTriggerW` | `50` | jump of the main meter between two readings that sends every plug back to 1 s for 60 s |
| Remote write | `KasaMonitor__RemoteWriteEnabled` | `false` | Prometheus remote write of the live samples (`kasa_live_*`) |
| Remote write URL | `KasaMonitor__RemoteWriteUrl` | — | e.g. `http://192.168.1.10:9090/api/v1/write` |
| Remote write user / password | `KasaMonitor__RemoteWriteUser`, `KasaMonitor__RemoteWritePassword` | — | optional basic auth; stored encrypted |
| Appliance detection | `KasaMonitor__NilmEnabled` | `true` | events, signatures and runs from the main meter (needs a main meter with the live stream) |
| Event threshold (W) | `KasaMonitor__MinEventDeltaW` | `30` | 5–10000; smallest steady-state step of the house power recorded as an event |
| Re-clustering window (days) | `KasaMonitor__ClusterWindowDays` | `30` | 1–365; events grouped again every night at 03:30 |
| ML model | `KasaMonitor__MlEnabled` | `true` | nightly ML.NET classifier over the labelled events; saved as `<db dir>/nilm-model.zip` |
| Keep raw readings (months) | `KasaMonitor__ReadingsRetentionMonths` | `0` = unlimited | raw 10-second readings only; daily history and minute aggregates are never deleted. Saving a value that would delete existing readings asks for confirmation with the exact row count |
| Gas boiler | `KasaMonitor__HeatingEnabled` | `true` | reads the Viessmann boiler once the ViCare account is connected (`/centrala`) |
| Viessmann Client ID | `KasaMonitor__ViessmannClientId` | — | client of the Viessmann developer portal; the OAuth tokens are never configured, they come from „Conectează ViCare” and are stored encrypted |
| Boiler poll interval (s) | `KasaMonitor__ViessmannPollIntervalSeconds` | `120` | minimum 60; one API call per poll (120 s = 720 of the 1450 daily calls) |
| Gas price (RON / kWh) | `KasaMonitor__GasPricePerKwh` | `0.31` | cost = m³ × kWh/m³ × price |
| Gas calorific factor (kWh / m³) | `KasaMonitor__GasKwhPerM3` | `10.55` | converts the boiler's m³ to kWh |
| Gas unit fallback | `KasaMonitor__GasUnit` | `m3` | `m3` or `kWh`; only used when the API sends a gas value without `unit` |
| Keep boiler readings (days) | `KasaMonitor__HeatingReadingsRetentionDays` | `0` = unlimited | `HeatingReadings` older than this are deleted once a day; daily gas history is never deleted |
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

Stat panels (power now, plugs online/total, energy and cost for 24 h and 30 days, all on `role="plug"`), a table per plug (online, relay,
W, V, A, kWh 24 h / 30 d, cost, last online, `model`, `protocol`), and per-plug time series for power, energy per day,
online/offline, relay, voltage and current (legend `{{alias}}`). Row **Contor general** (`role="main"`): house power,
rest of the house (house − plugs), grid voltage with 207/253 V thresholds, power factor, energy per day. Row
**Sub-contoare** (`role="sub"`): power and energy per day per sub-meter channel. Row **Consumatori detectați**: power
of the detected appliances (stacked), their energy in the selected range, unidentified energy and events in 24 h. Dashboard variables at the top: `tariff` (price per
kWh, default 1.11) and `currency` (only used in titles).

Energy is computed as the **sum of the counter's 5-minute increases, clamped at 0**:
`sum_over_time(clamp_min(kasa_power_total - kasa_power_total offset 5m, 0)[<range>:5m]) / 1000`, not with
`increase()`. `kasa_power_total` is re-seeded from the plug's own total when KasaMonitor restarts (and Tapo plugs
report month-to-date energy), so the series can go down a little; `increase()` treats any decrease as a counter
reset and would count the whole cumulative value as new consumption. The trade-off: a scrape gap longer than
5 minutes loses the energy of that gap.

### Metrics (`/metrics`)

Every series has the labels `alias` (plug name, MAC if empty), `mac`, `model` (e.g. `KP115`, `P115`,
`S3EM-002CXCEU`, without the region suffix), `protocol` (`legacy` | `klap` | `aes` | `shelly-rpc`) and `role`
(`plug` | `main` | `sub`, the role set on `/plugs`). For the channels ≥ 1 of a multi-channel Shelly meter `mac` is
`<mac>-<channel>` (channel 0 keeps the plain MAC). `/metrics` exports every channel; filter by role in the query:

```promql
sum(kasa_power_load{role="plug"})                                     # all plugs
kasa_power_load{role="main"}                                          # the house (main meter)
sum(kasa_power_load{role="main"}) - sum(kasa_power_load{role="plug"}) # rest of the house
kasa_voltage{role="main"}                                             # grid voltage
```

Changing a role (or renaming) removes the series with the old labels; the new ones start at the next poll.

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

The gas boiler has its own `heating_*` family, see [Gas boiler (Viessmann ViCare)](#gas-boiler-viessmann-vicare).

Appliance detection (see [Appliance detection (NILM)](#appliance-detection-nilm)):

| Metric | Type | Unit | Labels | Meaning |
|---|---|---|---|---|
| `kasa_appliance_power_w` | gauge | W | `appliance`, `category`, `source` | power (switch-on step) of each detected appliance running now |
| `kasa_appliance_energy_wh_total` | counter | Wh | `appliance`, `category` | energy of the finished runs since KasaMonitor started |
| `kasa_nilm_events_total` | counter | events | — | events recorded (starts from the stored count) |
| `kasa_nilm_unidentified_wh_total` | counter | Wh | — | house energy not explained by plugs or detected appliances, cumulative since start |

Only **detected** appliances that you named or that have at least 10 events in the window are exported (appliances
measured by a plug have their own `kasa_*` series; hidden ones are left out). `appliance` is the name normalised for
Prometheus (diacritics removed, only letters, digits, space, `_` and `-`, at most 40 characters: „Necunoscut #12” →
`Necunoscut 12`), `category` one of `fridge`, `boiler`, `cooking`, `washer`, `hvac`, `lighting`, `electronics`, `other`,
`source` = `user` (named by you) or `cluster` („Necunoscut #n”). Series of an appliance that is renamed, hidden, merged
or no longer exported are removed. These counters restart at 0 with the application: use `increase()`.

### Live samples via remote write (`kasa_live_*`)

Two metric families reach Prometheus:

- `kasa_*` (above): scraped from `/metrics` (typically every 15 s), the last value of every plug at scrape time.
- `kasa_live_*`: pushed by KasaMonitor with **Prometheus remote write 1.0** (protobuf `WriteRequest` + snappy,
  `Content-Encoding: snappy`, `X-Prometheus-Remote-Write-Version: 0.1.0`), with each sample's own timestamp, so the
  second-by-second fluctuations of the live stream reach Grafana without scraping every second:
  `kasa_live_power_w` and, when reported, `kasa_live_voltage_v`, `kasa_live_current_a`, `kasa_live_power_factor`.
  Same labels as `kasa_*` (`alias`, `mac`, `model`, `protocol`, `role`). Only the samples stored as raw rows are sent
  (power changed by more than max(1 W, 1 %), or every 60 s), so a quiet plug costs one sample a minute.

Enable it in `/settings` → *Integrare: Prometheus remote write* (URL, optional basic auth; or the
`KasaMonitor__RemoteWrite*` variables). Prometheus must run with `--web.enable-remote-write-receiver` (already in
`deploy/docker-compose.monitoring.yml`). Samples are queued in memory and sent every second (or at 500 samples);
a failed request is retried with backoff 1 → 60 s, an HTTP 4xx other than 429 drops the batch (e.g. out-of-order
samples), and samples older than 5 minutes in the queue are dropped. State in `/settings` and on `/metrics`:
`kasa_remote_write_samples_total`, `kasa_remote_write_failures_total`, `kasa_remote_write_dropped_samples_total`,
`kasa_remote_write_last_success_timestamp_seconds`. Example: `kasa_live_power_w{role="main"}` in a Grafana time
series with a 1 s min interval.

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
- **Meters vs. plugs** — decided by the channel's **role** (see below), not by the model: new `em1`/`em` channels
  start as sub-meters, `switch`/`pm1` channels as plugs.
- **Reversed clamp** — the Dashboard card shows „inversat?” when a Shelly channel reads below −10 W (simple rule on
  the current reading, accepted instead of "negative for a while"); fix the clamp direction (`reverse`) in the Shelly
  app if it is not real export.
- **Writes** — reads use only `Shelly.Get*`, `EM1.GetStatus`/`PM1.GetStatus`/`Switch.GetStatus`/`EM.GetStatus`,
  `EM1Data.Get*`. The single write is `Switch.Set` (relay switch, below); KasaMonitor never changes configuration
  (`*.SetConfig`), never reboots, resets or updates the device.

See `docs/device-api-capabilities.md` for what is shown, what exists and is not shown, and what would need writes.

## Roles, live stream, Contor page, relay

**Roles** (`Plugs.Role`, `/plugs` → column *Rol*, icons plug / house / meter): *plug* (`0`, default for Kasa, Tapo
and Shelly `switch`/`pm1`), *main meter* (`1`, the channel that measures the house supply; at most one, also enforced
by a partial unique index; setting a new one turns the previous one into a sub-meter; button „Setează ca contor
general”) and *sub-meter* (`2`, default for Shelly `em1`/`em` channels). Only plugs count in totals: Dashboard
header, History „Toate” (stacked bars; the house is a separate „Casă” line, never added to the plugs), Grafana totals
(`role="plug"`). Upgrading from 0.3.0 marks the existing Shelly meter channels (`S3EM…`, `SPEM…`, apps `EMG3`,
`ProEM`, `Pro3EM`) as sub-meters; other `em1`/`em` devices already in the database (another model code) stay plugs:
set their role on `/plugs`. Choose the main meter once on `/plugs`. Roles cannot be changed on withdrawn rows.

**Live stream** (`/plugs` → checkbox „flux 1 s” per channel, `Plugs.StreamEnabled`; default on for the main meter
and every plug, off for sub-meters such as an unused clamp; changing the role resets it to the role's default):

- **Shelly**: one persistent WebSocket per device (`ws://<ip>/rpc`; it does not count against the 6 non-persistent
  RPC channels). First frame `Shelly.GetStatus` (valid `src`, so notifications come on it too), then
  `EM1.GetStatus {id}` (or `EM`/`PM1`/`Switch.GetStatus`) per streamed channel, answers matched by `id`, 2 s timeout.
  `NotifyStatus`/`NotifyEvent` are logged at Debug and update the relay state. Why not notifications: the device
  measures every 1.0 s, but `NotifyStatus` comes only every 4–16 s and `em1data` every 60 s; an RPC on the open
  socket answers in ~21 ms. With a device password the socket uses Shelly's WebSocket digest (`auth` object after
  the 401 challenge), implemented from the documentation, not yet verified on a device with a password.
- **Kasa legacy**: `emeter.get_realtime` through the same client as the 10 s polling (the legacy client opens one
  short TCP connection per request, as it always did).
- **Tapo (KLAP/AES)**: one `get_energy_usage` per reading on the transport's cached session, shared with polling and
  re-authenticated by the transport when it expires (needs the TP-Link account in `/settings`).
- **Timing**: ticks at the start of every wall-clock second, each reading stamped with the nearest second (never
  more than one per channel and second). The **main meter** is read every second (the base interval
  `LiveStreamIntervalSeconds`, minimum 1, never less). **Every other channel is adaptive**: 1 s while its power
  changed by more than max(1 W, 1 %) during the last 60 s, then one second slower after each quiet reading, up to
  10 s; back to 1 s at the first change, or for 60 s when the main meter jumps by more than `LiveBurstTriggerW`
  (default 50 W) between two seconds (a „house event”: every plug is read at once). Measured on 2026-11-01: the
  house's main meter moves by a median 17 W per second, so a 15 W trigger kept every plug at 1 s all the time. A device that stops answering (3 ticks with every request
  failing, or a closed WebSocket) is re-opened with backoff 1 → 30 s.
- **Storage** (`LiveSamples`: `PlugId`, `Ts` = UTC unix second, `PowerW`, `ApparentVa`, `VoltageV`, `CurrentA`, `Pf`;
  index (`PlugId`, `Ts`)): a row is written only when the power differs by more than max(1 W, 1 %) from the last
  **written** row, or 60 s after it (a witness), in batches (one transaction every 10 s). `LiveMinuteAggregates`
  (per channel and UTC minute: min/avg/max W, avg V, energy, samples, covered seconds) are computed from **every**
  reading received, not only the written ones: each reading stands for the interval it was scheduled with (capped at
  the time since the previous reading, so a missed second adds nothing); energy = Σ W × seconds / 3600. A second part
  of a minute (restart in the middle of a minute, a late answer) is merged into the stored minute, never overwritten.
  Raw rows older than `LiveRawRetentionDays` (default 30, `0` = keep) are deleted once a day (a smaller value asks
  for confirmation in `/settings`); aggregates are kept forever. The 10 s polling (`Samples`, `/metrics`) is unchanged
  and the live stream is **not** exported to `/metrics`.
- **Volume** (≈ 146 bytes per raw row including the index; measured on 2026-11-01 with the EM Gen3 and five KP115):
  a quiet plug writes ≈ 1 row/min (the witness) ≈ 1,440 rows ≈ 0.2 MB/day; a plug with a cycling load (UPS,
  network rack with fans/compressor) wrote 30–42 % of its readings at 1 s ≈ 26,000–36,000 rows ≈ 3.8–5.3 MB/day; the
  main meter at 1 s wrote 94 % ≈ 81,000 rows ≈ 11.8 MB/day (a whole house rarely holds within 1 %). With the default
  30 days: ≈ 350 MB for the main meter + 6–160 MB per plug. Minute aggregates: 1,440 rows per channel per day.
- To stop it: untick „flux 1 s” on a channel, or *Settings → Flux live* off (applies within 10 s).

**Contor page** (`/contor`, menu „Contor”): alias of the main meter, power now (1 s; falls back to the 10 s poll when
the stream is older than 5 s), rest of the house (main − online plugs), voltage (red outside 207–253 V), current, power
factor, frequency, relay; live chart of the last 10 minutes (main meter, plus an optional plug chosen in „Suprapune o
priză”, from the same stream; only the new point is pushed to ApexCharts each time, the whole 10-minute window once a
minute); today per minute (min/avg/max from the aggregates); energy per day for 30 days (from `DailyEnergy`) with
cost; the sub-meters; the last 20 relay commands. Without a main meter it links to `/plugs`. The Dashboard shows a
„Casă” card (house power, rest of the house, relay) above the plugs.

**Relay**: on channel 0 of a Shelly meter with `switch:N` (EM Gen3: `switch:0`, a contactor) and on Shelly
`switch` plugs (Plus Plug S, …) a Pornit/Oprit switch appears on `/contor`, in the „Casă” card and in the *Detalii*
row of `/plugs`. Every command needs a confirmation („Releul comandă un contactor; sigur?”) and sends
`Switch.Set {id: <channel>, on}` over HTTP RPC (digest when a password is set); the server refuses channels without a
relay. Every attempt is logged in `RelayCommands` (`Ts`, `PlugId`, `On`, `Source` = `ui`, `Result` = `ok` or the
error) and in the application log. The shown state is the newest of the 10 s poll (`switch:N.output`) and the
WebSocket notifications. *Settings → Permite comanda releului* off hides the switch and refuses commands.
**Risk:** relay control is on by default and KasaMonitor has **no authentication**: anyone who can open the web UI
can switch the contactor (the confirmation only prevents mistakes). Expose KasaMonitor only inside the LAN (never
through a port forward or a reverse proxy without authentication), or turn relay control off. TODO: Kasa/Tapo relay
commands (different protocols; not in this version).

## Gas boiler (Viessmann ViCare)

KasaMonitor reads a Viessmann boiler (mapping built on a Vitodens 050-W B0KA, model `E3_Vitodens_050_0822`, Wi-Fi
built in) through the **ViCare cloud API**; there is no local API. It never sends a command to the boiler.

**One-time setup**

1. Log in at the [Viessmann developer portal](https://developer.viessmann-climatesolutions.com) with the ViCare
   account → *Your clients* → *+ Add*: any name, reCAPTCHA **off**, redirect URI = the address you open KasaMonitor
   with + `/centrala/callback`, e.g. `http://192.168.1.10:8910/centrala/callback` (add one per address you use, e.g.
   also `http://localhost:8910/centrala/callback`). A new client can take up to an hour to become active.
2. `/settings` → *Centrală (Viessmann)*: paste the Client ID, save.
3. `/centrala` → **Conectează ViCare**: KasaMonitor redirects to Viessmann (OAuth 2.0 authorization code with PKCE
   S256, scope `IoT User offline_access`, random `state`); after you approve, Viessmann redirects to
   `/centrala/callback`, which checks the `state` (unknown, expired after 10 minutes or reused → rejected; it must also
   match the `vicare_oauth` cookie — HttpOnly, SameSite=Lax, a hash of the state — set by `/centrala/connect` in the
   same browser; at most 10 pending requests) and exchanges the code for the tokens. Access and refresh tokens are stored encrypted (DataProtection, like the TP-Link password)
   and shown masked; nothing secret is logged. The access token (1 h) is refreshed 5 minutes before it expires. Only a
   refused grant (HTTP 400/401/403 with `invalid_grant` or `invalid_token`) disconnects the account (the page then shows
   **Reconectează**); any other token-endpoint error (429, 5xx, network) is temporary and retried with the backoff below.

**Polling and limits.** After connecting, one call to `GET /iot/v2/equipment/installations?includeGateways=true`
finds the installation, gateway and boiler; then every `ViessmannPollIntervalSeconds` (default 120 s, minimum 60 s)
**one** bulk call `GET /iot/v2/features/installations/{id}/gateways/{serial}/features?includeDevicesFeatures=true`
returns every feature (about 70 KB). The limits belong to the client registered on the developer portal (free plan:
1450 calls per sliding 24 h and 120 per 10 minutes); KasaMonitor keeps its API calls inside both windows (in memory),
but the API's HTTP 429 stays the authority: KasaMonitor then stops until the reset time the API gives (or at least
10 minutes) and logs a warning. After any failed call (5xx, network error, gateway offline, a repeated 401, no boiler
found, a token refresh answered with 5xx/429) the next call waits 1×, 2×, 4× … the interval, at most 30 minutes (never
less than the interval), until the first success. An HTTP 401 triggers one token refresh and one retry.
`heating_api_calls_today` counts the HTTP requests actually made (API and token endpoint) during the local calendar
day; it resets at midnight and when KasaMonitor restarts.

**What is mapped** (Vitodens 050-W): supply temperature (`heating.boiler.sensors.temperature.commonSupply`, same as
circuit 0 supply), boiler target (`heating.boiler.temperature`, 20 °C when idle: a target, not a sensor, exported as
`heating_boiler_target_c`), room
temperature from the Zigbee room sensor, hot water temperature and target, burner on/off, modulation, starts and hours,
internal pump target, circuit 0 mode/program and program targets, the heating schedule (shown only), hot water mode,
comfort/one-time charge/scald protection, frost protection, messages (`device.messages.status|service|info.raw`:
status code such as `S.6`; an entry whose priority is not `status`, or the malfunction lock, is the active error; the
API's message times are UTC and shown in local time),
gateway Wi-Fi, room-sensor battery and Zigbee link, gas type. Sensors the model lacks (outside, return, boiler
temperature, pressure: `heating.sensors.pressure.supply` is `notConnected`) stay empty and are neither shown nor
exported; boilers that have them (PyViCare names) fill them. Every other feature is kept in the raw dump and listed
under *Nemapate* on `/centrala`; unknown features are not an error.

**Gas, heat and electricity.** The boiler reports `heating.gas.consumption.summary.dhw|heating` (today, last 7 days,
this/last month, this/last year) in **m³** (other models: kWh; the unit comes from the answer, `GasUnit` is only a
fallback), plus heat produced (`heating.heat.production.summary.*`, kWh) and its own electricity
(`heating.power.consumption.summary.*`, kWh). The Vitodens 050-W has no `day[]` series, so **the daily history is built
by KasaMonitor**: every poll writes the boiler's "today" value on the **local day of KasaMonitor's reading** (the
container's `TZ`; the feature's timestamp is not used, the gateway re-sends features in bulk) into `HeatingEnergyDaily`,
which keeps per purpose the day's highest value (`GasHeatingMaxM3`, `GasDhwMaxM3`) and the last value seen
(`…LastM3`). A closed day counts with its highest value, the current day with its last value; a value lower than the
stored highest in the same local day (e.g. the boiler resetting its counter some time after midnight) is logged at
Information with both values, so the first nights show how the boiler behaves. Boilers with a `day[]` series (day[0] =
today) fill every day of the series. The period values are stored as they come (`HeatingEnergyPeriods`). The page shows hot water
vs heating for today / 7 days / month / last month / year with the cost, a stacked bar chart of the last 30 days and
the apparent efficiency of last month (heat produced / gas × kWh/m³). **The consumption is estimated by the boiler's
controller, not measured by a gas meter**: compare it with the gas bill before relying on it.

**Storage.** `HeatingDevices`, `HeatingReadings` (one row per poll: about 720 rows a day at 120 s; kept forever unless
`HeatingReadingsRetentionDays` > 0),
`HeatingEnergyDaily`, `HeatingEnergyPeriods` and `HeatingRawFeatures` (only the latest raw answer, overwritten).

**Metrics** (`/metrics`, labels `device` = e.g. `Vitodens 050`, `model` = e.g. `E3_Vitodens_050_0822`; a value the
boiler does not report has no series):

| Metric | Type | Unit | Source |
|---|---|---|---|
| `heating_burner_active` | gauge | 0/1 | `heating.burners.0.active` |
| `heating_burner_modulation_percent` | gauge | % | `heating.burners.0.modulation` |
| `heating_boiler_temperature_c` | gauge | °C | `heating.boiler.sensors.temperature.main` (not on 050-W) |
| `heating_boiler_target_c` | gauge | °C | `heating.boiler.temperature` (target; 20 °C when idle on a 050-W) |
| `heating_supply_temperature_c` | gauge | °C | `…commonSupply` / `heating.circuits.0.sensors.temperature.supply` |
| `heating_return_temperature_c`, `heating_outside_temperature_c` | gauge | °C | `heating.sensors.temperature.return` / `.outside` (not on 050-W) |
| `heating_dhw_temperature_c`, `heating_dhw_target_c` | gauge | °C | `heating.dhw.sensors.temperature.*`, `heating.dhw.temperature.main` |
| `heating_room_temperature_c` | gauge | °C | `heating.circuits.0.sensors.temperature.room` |
| `heating_pump_percent` | gauge | % | `heating.boiler.pumps.internal.target` |
| `heating_pressure_bar` | gauge | bar | `heating.sensors.pressure.supply` (only when connected) |
| `heating_burner_starts_total`, `heating_burner_hours_total` | counter | starts, h | `heating.burners.0.statistics` |
| `heating_gas_consumption_m3{purpose,period}` | gauge | m³ | summary values; `purpose` = `heating` / `dhw`, `period` = `day` / `last7days` / `month` / `lastmonth` / `year` / `lastyear` |
| `heating_gas_consumption_m3_total{purpose}` | counter | m³ | built by KasaMonitor: Σ highest value of the closed days + last value of today; exported as a counter that never goes down (a lower sum, e.g. a stale value replaced by a late reset, holds the counter until the sum passes it again) |
| `heating_heat_production_kwh{purpose,period}`, `heating_heat_production_kwh_total{purpose}` | gauge, counter | kWh | `heating.heat.production.summary.*` |
| `heating_power_consumption_kwh{purpose,period}`, `heating_power_consumption_kwh_total{purpose}` | gauge, counter | kWh | `heating.power.consumption.summary.*` |
| `heating_dhw_mode_info{mode}`, `heating_program_info{program}` | gauge | 1 | hot water mode, active program of circuit 0 |
| `heating_gateway_wifi_dbm` | gauge | dBm | `gateway.wifi` / `tcu.wifi` |
| `heating_room_sensor_battery_percent` | gauge | % | `device.power.battery` of the Zigbee room sensor |
| `heating_error_active` | gauge | 0/1 | an error message or the malfunction lock |
| `heating_api_calls_today` | gauge | calls | HTTP requests to Viessmann (API + token endpoint) actually made today, local calendar day; resets at midnight and at a restart |
| `heating_api_last_success_timestamp_seconds` | gauge | s | last successful read |
| `heating_online` | gauge | 0/1 | last read succeeded within three poll intervals |

```promql
increase(heating_gas_consumption_m3_total[1d])                     # gas per day, hot water vs heating
heating_gas_consumption_m3{period="lastmonth"}                     # the boiler's own value for last month
sum(heating_heat_production_kwh{period="lastmonth"}) / (sum(heating_gas_consumption_m3{period="lastmonth"}) * 10.55)  # apparent efficiency
```

The Grafana dashboard `kasamonitor` has a row **Centrală**: burner, modulation, gas today (hot water vs heating),
online/API calls, temperatures, modulation over time and gas per day for 30 days (stacked bars).

## Appliance detection (NILM)

KasaMonitor recognises appliances in the power of the whole house (non-intrusive load monitoring) with a
deterministic, explainable pipeline, and uses the plugs it already measures as automatic labels. It needs a **main
meter** (`/plugs`) with the live stream on (1 s). Page `/consumatori`, menu „Consumatori”.

1. **Events** (`MeterEventDetector`, table `MeterEvents`): steady-state transitions of the main meter's 1-second
   power. The level *before* is the mean of the last 5 s of a steady state; a second outside max(5 W, 3 %) of it
   starts a transition, which ends when 3 consecutive seconds are within max(5 W, 3 %) of their mean (the level
   *after*). An event when |ΔP| ≥ `MinEventDeltaW` (default **30 W**). Features: ΔP (signed W), ΔQ (reactive power
   |Q| = √(S² − P²) from the apparent power, after − before), Δpf, transient ratio (peak in the first 2 s / ΔP; motors
   3–8, resistive loads ≈ 1), rise seconds, time. A plateau of ≥ 2 s inside a transition that is left in the same
   direction (two appliances switched 2 s apart) gives two events; a peak that falls back (motor inrush) gives one
   event with a high transient ratio. Slow drift moves the level without an event. Missing seconds (the raw table
   keeps a row only on change, or every 60 s) are filled with the previous value for up to 65 s. An event within
   ±2 s of a stored one, in the same direction and of the same size, is a duplicate (a second re-processed after a
   restart) and is not stored again.
2. **Backfill**: at start, the stored `LiveSamples` of the main meter after the marker `NilmLastProcessedTs` (Settings
   table; the first time: all of them, i.e. `LiveRawRetentionDays`) go through the detector in 6-hour batches (the
   plugs' readings of a batch are loaded once); a backfill of more than 6 hours is re-clustered right away. The page
   stays usable meanwhile (it reads its own snapshot and shows „Procesare în curs”).
3. **Plug labels**: 45 s after an event (the ±15 s window plus two polls), the plugs' 10-second readings around it (kept in memory for the last
   3 minutes; loaded per batch during a backfill) are compared. A plug labels the event (source `auto-plug`) when it
   made a **clean step**: the middle of the interval between its two readings is within ±15 s of the event, the
   readings before and after the step agree with it (a variable load is not a step), the step is ΔP ± max(10 W, 15 %),
   it is the **only** plug that moved in the same direction by ≥ 60 % of |ΔP| in that window (counting every plug,
   also those excluded from learning: a UPS that moved at the same time makes the event ambiguous), and that plug step
   has not labelled another event. Events detected late (after a long backfill or nightly job) read the plugs'
   readings from `Samples`. A plug that matches **more than 20 events in an hour** (a UPS, a PC: a variable load) is
   excluded from learning automatically (`Plugs.NilmIgnored`; badge on `/plugs` and `/consumatori`, with a button to
   undo it — an undone plug is never excluded automatically again; a plug can also be excluded by hand). Excluding
   only stops new labels and training: the plug's labels no longer count for linking new signatures or for the
   classifier, but signatures already linked to it stay „measured by the plug”.
4. **Signatures** (`ApplianceSignatures`): DBSCAN (own implementation) over |ΔP| (relative, i.e. log scale), ΔQ seen
   from the switch-on side and the transient ratio of switch-ons; eps(|ΔP|) = 8 % of |ΔP| + 3 W, eps(ΔQ) = 8 % of |ΔP|
   + 15 VAR, minPts 3; a cluster wider than (1 + 2 × 8 %) × its smallest |ΔP| + 6 W is cut into narrower bands (a dense
   band of small loads would otherwise chain into one cluster). Each point enters the expansion queue once (memory
   O(n)). Switch-ons and switch-offs of the same magnitude join the same signature. A new event joins the nearest
   signature within eps, otherwise a new „Necunoscut #n”. Every night at 03:30 the **newest 20,000 events** of the last
   `ClusterWindowDays` (30) are clustered again on a low-priority thread, outside the processing lock (clusters keep the
   signature, name and category most of their events had; model labels are re-evaluated; empty automatic signatures
   are deleted). Counts and means are SQL aggregates over the window.
5. **Measured by a plug**: when at least 3 events and ≥ 60 % of a signature's events were labelled by the same (not
   excluded) plug, the signature is linked to that plug (below 3 it stays „Necunoscut #n”, marked „posibil <plug>” on
   the page; `ApplianceSignatures.PlugId`; it takes the plug's name unless you named it).
   The link is by `PlugId`, not by name: renaming does not change it. These signatures are listed separately on
   `/consumatori` („Măsurate de prize”): the exact consumption is the plug's own (its card, `kasa_*`), so they are not
   in „Acum pornite”, not in the detected energy, not in `kasa_appliance_*`, and never subtracted twice from the rest
   of the house.
6. **Your labels** (`/consumatori`): rename a signature, choose its category (fridge, boiler, hob/oven, washing
   machine, AC/heat pump, lighting, electronics, other), hide it, merge one into another (one selector above the
   table), split it (re-clustered alone with half the eps), and label any event of the last 24 h („a fost boilerul”;
   Enter or the button): the event moves to the signature with that name if there is one; otherwise its own signature
   takes the name when it is an automatic one, else a new signature is created. Names are trimmed to 64 characters.
   Signatures you named or merged are kept as they are by the nightly re-clustering. A hidden signature is left out of
   the cards and the metrics (its energy stays „unidentified”). Lists are paged by 50.
7. **ML model** (`MlEnabled`): with ≥ 20 events labelled by plugs or by you in ≥ 2 classes, a multiclass ML.NET
   classifier (`SdcaMaximumEntropy`; features ΔP, log |ΔP|, ΔQ, transient, rise, hour of day as sin/cos, day of week)
   is trained every night on the newest 20,000 labelled events (low-priority thread); its accuracy is measured by
   5-fold cross-validation and shown on the page with a confusion matrix — **measured only on the appliances that have
   a plug or that you labelled**, not on the whole house. A new event without a plug label gets the prediction only
   when the probability is ≥ 0.7 **and** the event is within 2 × eps of that class's centre (an open-set check: an
   appliance no plug has seen is otherwise forced into the nearest known class); it then joins the signature with that
   name, otherwise it stays in its DBSCAN signature. Saved as `<db dir>/nilm-model.zip`. ML.NET adds ≈ 5.5 MB to the
   published application (measured `dotnet publish -r linux-x64`: 31 MB in total); the trainer is managed code.
8. **Runs and energy** (`ApplianceRuns`): a switch-on opens a run; it closes with the next switch-off of the same
   signature whose magnitude is within eps (first in, first out; `EndReason` 0), or with any switch-off that brings the
   house back to (or below) its level before that switch-on, within the detector's tolerance (`EndReason` 1: whatever
   turned off, that appliance cannot still be on; a simple heuristic, wrong when something else turned on in between
   and off again). A run open longer than **6 hours** is closed as uncertain (`EndReason` 2) with **no energy** (its
   switch-off was never seen): it is not in „Azi”, in `kasa_appliance_energy_wh_total` or in „Neidentificat”, only
   counted as „rulări incerte” next to its signature, and no longer shown as running. When the nightly job rebuilds
   the runs, only those that ended in the last 24 hours get the house-power correction. Energy = ΔP × duration; for a paired run with no other event during it,
   the house's mean power above its level before the switch-on is used instead (kept only between ½ and 2× the
   nominal value). Energy per day per signature (a run across midnight is split proportionally), and
   **„Neidentificat”** = house today − Σ plugs − Σ runs of the detected appliances (never below 0). The house energy
   comes from the minute aggregates (only the minutes the stream ran), so Σ plugs (the Kasa/Tapo day counters, like the
   Dashboard header; Shelly plugs have none and are not subtracted) is scaled to the same covered share of the day.
9. **Cards** „Acum pornite” (detected appliances running now, with W) and „Azi” (top 5 + unidentified) on the
   Dashboard and `/contor`; metrics `kasa_appliance_*`, `kasa_nilm_*` (see Metrics).
10. **Retention**: events and finished runs older than 365 days are deleted every night (signatures stay).

**Limits.** Appliances with a variable power (induction hobs, inverter compressors and heat pumps, dimmers, PCs)
have no steady state: their changes either do not settle within 30 s (no event) or appear as many small events.
Appliances switched within 2 s of each other in opposite directions give one event (the net step). Steps below the
threshold (30 W) are ignored; the measured main meter moves by a median 17 W per second, and at 15 W a 16-minute test
gave 76 events, mostly from a UPS. **Known limit:** small loads in the 15–50 W band (a UPS, lamps, chargers, a PC)
have nearly the same ΔP, ΔQ and transient, so their signatures stay mixed (measured on the real house: the clusters
are cut into narrow |ΔP| bands, but each band holds events of several appliances). A slow ramp faster than ~3 W/s on a low base can be seen as a step. **ΔQ is a
whole-house difference**: |Q| is computed for the house, so a capacitive load (a switched-mode supply) switched on in
an inductive house lowers |Q| while the same load in a house with Q ≈ 0 raises it — its sign depends on what else is
running; that is why its eps is wider. The run pairing assumes one switch-off per switch-on (multi-stage appliances
such as a washing machine appear as several signatures). The plug labels need the plug's clean step and the house step
in the same ±15 s, so a plug offline, or two plugs moving together, give no label.

## Data retention

| Data | Table | Written | Deleted |
|---|---|---|---|
| Raw readings (every poll, default 10 s) | `Samples` | ≈ 128 bytes per reading ≈ 1.1 MB per plug per day ≈ 400 MB per plug per year | never by default; only if `ReadingsRetentionMonths` > 0 (daily purge, in batches) |
| Raw live samples (on change or every 60 s) | `LiveSamples` | ≈ 146 bytes per row: quiet plug ≈ 0.2 MB/day, cycling load ≈ 4–5 MB/day, main meter ≈ 12 MB/day | after `LiveRawRetentionDays` (default 30; `0` = never), daily |
| Live minute aggregates | `LiveMinuteAggregates` | 1,440 rows per streamed channel per day | never |
| Relay commands | `RelayCommands` | one row per command | never |
| Appliance events, signatures, runs | `MeterEvents`, `ApplianceSignatures`, `ApplianceRuns` | one row per event (≥ 30 W step of the house) / appliance / run | events and finished runs after 365 days (nightly); signatures never |
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
- `Live stream <mac> (<ip>) connected: EM1.GetStatus for channels 0` — Information (Shelly); a device that fails
  logs one Warning (`…; retrying with backoff`), repeats at Debug, then `receiving again after N failed attempts`.
- `Relay command ON|OFF for <alias> (<ip>, switch:N) from ui: ok|<error>` — Information.

## Build and test

```bash
dotnet build KasaMonitor.sln
dotnet test KasaMonitor.sln
```

## Docker image

From the repository root, with Docker running and `docker login` done once:

```powershell
.\deploy\build-and-push.ps1 -Version 0.7.0          # build :0.7.0 + :latest and push both
.\deploy\build-and-push.ps1 -Version 0.7.0 -NoPush  # local build only
```

or manually:

```bash
docker build -t duobit/kasamonitor:0.7.0 -t duobit/kasamonitor:latest -f deploy/Dockerfile .
docker push duobit/kasamonitor:0.7.0
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
   the rows with `Channel > 0` deleted first, otherwise re-creating the unique index on `Mac` fails. Upgrading to
   0.4.0 runs `AddRolesAndLiveStream`: adds `Plugs.Role` (0; Shelly meter channels `S3EM…`/`SPEM…`/`EMG3`/`ProEM`/`Pro3EM`
   become 2 = sub-meter) and `Plugs.StreamEnabled` (1; 0 for those sub-meters), a partial unique index (one main
   meter) and the empty tables `LiveSamples`, `LiveMinuteAggregates`, `RelayCommands`; no existing table is rewritten.
   After the update, set the main meter on `/plugs` and change Grafana dashboards that used
   `model!~"S3EM.*|SPEM.*"` to `role="plug"`. Prometheus series written before the upgrade have no `role` label, so
   `{role="plug"}` does not see them: for the 30-day panels use `{role!~"main|sub", model!~"S3EM.*|SPEM.*"}` until
   30 days have passed after the upgrade, then `{role="plug"}`. Upgrading to 0.7.0 runs `AddNilm`: it adds `Plugs.NilmIgnored`
   (0) and creates the empty tables `MeterEvents`, `ApplianceSignatures`, `ApplianceRuns`; at the first start the
   detector processes the stored `LiveSamples` of the main meter (backfill), then re-clusters them.
2. `docker compose -f deploy/docker-compose.yml pull && docker compose -f deploy/docker-compose.yml up -d`
   (Portainer: *Update the stack* with *Re-pull image and redeploy*).
3. Check `http://<host>:8910/health`, `/plugs` (Scan now) and `curl -s http://<host>:8910/metrics | grep kasa_online`.

See also `deploy/README.md` (troubleshooting).
