# KasaMonitor

Self-hosted monitoring for TP-Link smart plugs with energy metering: **Kasa** (e.g. KP115, legacy XOR protocol on
port 9999) and **Tapo** (e.g. P110/P115, KLAP and AES "securePassthrough" protocols), plus **Shelly Gen2/Gen3**
plugs and energy meters (e.g. Shelly EM Gen3, local JSON-RPC; the only write is the relay switch, after a confirmation), and a
**Viessmann gas boiler** read through the ViCare cloud API (read-only). Blazor Server app (.NET 10)
with SQL Server, a web UI on port 8910 and a Prometheus `/metrics` endpoint. An optional Prometheus + Grafana stack
with a ready-made dashboard and alert rules is included.

Version: **0.14.0** (`Directory.Build.props`). Docker image: `duobit/kasamonitor`.

**Requirements:** SQL Server 2019+ (any edition reachable from the Docker host; Express is limited to 10 GB per
database), a Docker host on the same LAN as the devices (host networking). Up to 0.11 the data were in a SQLite file;
moving them is described in [Updating](#updating).

## Quick start

```bash
git clone https://github.com/andySF/kasamonitor.git
cd kasamonitor
# 1. the database, its login and user, once (as a sysadmin of the SQL Server):
sqlcmd -S <sql host>,1433 -U sa -C -i deploy/sql/create-database.sql -v PASSWORD="<kasamonitor password>"
# 2. the stack: edit deploy/docker-compose.yml (KasaMonitor__BroadcastNetworks = your LAN broadcast address, e.g.
#    192.168.1.255) and put the connection string in an env file, never in the compose file:
echo 'KASA_SQL_CONNECTION=Server=<sql host>,1433;Database=kasamonitor;User Id=kasamonitor;Password=<…>;TrustServerCertificate=True' > kasamonitor.env
echo 'KASA_DATA_DIR=/srv/kasamonitor/data' >> kasamonitor.env   # absolute: DataProtection keys and the NILM model
docker compose -f deploy/docker-compose.yml --env-file kasamonitor.env up -d
```

In Portainer, paste `deploy/docker-compose.yml` as a stack and set `KASA_SQL_CONNECTION` and `KASA_DATA_DIR` (both
required: the stack does not start without them) in the stack's environment variables (`deploy/README.md`). The app creates the tables itself (EF Core migrations at start-up)
and waits up to 120 s for SQL Server.

Open `http://<host>:8910/`. Plugs on the same LAN are discovered within a few seconds; for Tapo plugs enter your
TP-Link (Tapo app) account in `/settings/tplink` and press **Adaugă** → TP-Link → **Scanează acum** on `/dispozitive`.

`network_mode: host` is required: discovery uses UDP broadcasts (9999 for Kasa, 20002 for Tapo) and mDNS multicast
(224.0.0.251:5353 for Shelly) that do not leave a Docker bridge network. The data live in SQL Server; the container's
`/data` volume keeps only the key ring (`dp-keys/`) and the NILM classifier.

## Features

- **Home page „Acasă”** (`/`): the house at a glance — live power in bubbles (plugs, detected appliances, standby, the
  undetected rest), energy and cost of electricity and gas for a day / week / month / year with the comparison, bars per
  hour with the typical day, appliances, where the energy went (Sankey), today vs yesterday at the same time, the month
  with a bill estimate, the boiler, Miele and temperatures. Light and dark theme. See [Home page](#home-page-acasă).
- **Plugs page „Prize”** (`/prize`, the former Dashboard): one card per plug with power, voltage, today / month,
  device details and the relay.
- **Devices** (`/dispozitive`, menu „Dispozitive”): every device of every vendor in one table with filters, the state of
  each integration and „+ Adaugă”. See [Devices](#devices-dispozitive).
- **Automatic discovery** every N minutes: UDP broadcast on 9999 (Kasa legacy) and the TP-Link discovery probe
  on UDP 20002 (Tapo / newer Kasa). New plugs are added automatically (setting "Adaugă automat", Setări → Aplicație).
- **Scan now** (`/dispozitive` → Adaugă, "Scanează acum"): runs discovery immediately and lists each plug as new / added / known /
  unsupported; with auto-add off, new plugs are added with one click.
- **Add by IP** (`/dispozitive` → Adaugă): tries legacy TCP 9999, then the UDP 20002 probe (+ KLAP/AES), then KLAP and AES
  directly (with TP-Link credentials), then Shelly (`GET /rpc/Shelly.GetDeviceInfo`). Useful for devices on another
  subnet, where broadcasts and mDNS do not reach.
- **Withdraw / restore** (`/dispozitive`, "Retrage" / "Readu"): a withdrawn plug is no longer polled and disappears from
  `/metrics` and the dashboard immediately; "Arată retrase" shows it again in the list.
- **Unsupported plugs** (they answer discovery but cannot be queried) are listed with IP/MAC and the reason:
  missing TP-Link credentials, authentication failed, Kasa firmware on KLAP (not implemented), or an unknown
  protocol such as TPAP. For TPAP the UI shows the usual fix: in the Tapo app, device settings → *Third-Party
  Compatibility* off and on again, then restart the plug; it normally comes back on KLAP.

- **Device details** (`/dispozitive`, the plug's row; „Detalii” on a `/prize` card links there): settings read from the
  plug on demand (cached 60 s) — relay, uptime, default state after power loss, LED, auto-off / countdown, power
  protection, Wi-Fi signal, firmware, device time, cloud, schedules. Read-only; see `docs/device-api-capabilities.md`
  for everything the local APIs offer.
- **Roles** (`/dispozitive`, the plug's row): every channel is a *plug* (counts in the totals), the *main meter* of the house (one per
  installation) or a *sub-meter*. See [Roles, live stream, Contor page, relay](#roles-live-stream-contor-page-relay).
- **Live stream**: the main meter every second, plugs (Shelly, Kasa, Tapo) every 1–10 s (adaptive), raw rows only on
  change; minute aggregates from every reading.
- **Contor page** (`/contor`): the house in real time (1 s), rest of the house, today per minute, energy per day, relay.
- **Gas boiler** (`/centrala`, menu „Centrală”): a Viessmann boiler through the ViCare cloud API: burner, modulation,
  temperatures, hot water, a burner timeline (heating vs hot water, inferred), messages, gas per day for hot water vs heating with cost, raw features. See
  [Gas boiler (Viessmann ViCare)](#gas-boiler-viessmann-vicare).
- **Miele appliances** (`/aparate`, menu „Aparate”): washing machine and tumble dryer through the Miele 3rd Party API
  (OAuth, one SSE stream): status, program, phase, remaining time, temperature/spin, door, the appliance's own energy
  and water, program cycles with their phases, the linked plug's energy per cycle and cost, `miele_*` metrics, and
  ground truth for the appliance detection. Read-only. See [Miele appliances](#miele-appliances).
- **Climate** (`/clima`, menu „Climă”): temperature and humidity per room from the boiler's thermostat and
  temperatures (no extra API calls), Govee Wi-Fi thermometers (cloud, only those ticked „acasă”), Shelly H&T Gen3 /
  Plus Add-on (local) and the outdoor temperature of Open-Meteo; cards with the age of each value, 24 h / 7 / 30 days
  charts, min / mean / max, `climate_*` metrics. See [Climate](#climate).
- **Appliance detection (NILM)** (`/consumatori`, menu „Consumatori”): switch-on/off events of the appliances found in
  the main meter's 1-second power, grouped into signatures (one per appliance), labelled automatically from the plugs,
  by you, or by a nightly ML.NET classifier; runs, energy per day, „unidentified” energy; cards „Acum pornite” and
  „Azi” on the „Prize” page and `/contor`. See [Appliance detection (NILM)](#appliance-detection-nilm).
- **History** (`/history` „Istoric”, 0.9.2): one page and one period (day, week, month, year or a free interval, kept
  in the address) for electricity, the power over time (main meter and plugs, per minute / hour / day from the
  aggregates, against the previous period or the same interval last year; 0.14.0), gas and boiler, Miele, detected
  consumers, climate (with heating degree days and the energy signature), costs and events; Δ at the same share of the
  previous period, drill-down, CSV per section; and
  „Rapoarte” on `/centrala` (gas, boiler electricity, heat produced). See [History](#history-history-istoric).
- **Version in the menu**: „v0.14.0” at the right of the navigation bar; the tooltip shows the commit and the build time.
- **Browser errors in the server log**: JavaScript errors and the Blazor error banner are reported to
  `POST /api/client-error` and logged at Warning (category `KasaMonitor.ClientErrors`), so `docker logs` shows them.
- **HTTP API for the appliance detection** (`/api/nilm/*`, LAN only): list, sheet, label, merge, add to, hide, regroup,
  train and raw events with `curl`, the same actions as `/consumatori`, plus „acum am pornit X” marks (`/api/nilm/mark`),
  duration bands and the split of a signature by duration. See [HTTP API (LAN)](#http-api-lan).

The web UI uses Bootstrap 5 and Bootstrap Icons, served locally (no CDN). Texts on Plugs/Settings/Integration are in Romanian.

## Devices (`/dispozitive`)

One table of every device of every integration (since 0.10.0; `/plugs` redirects here with 301): plugs and meter
channels (TP-Link, Shelly), the boiler (Viessmann), the Miele appliances, the climate sensors (Govee, Shelly H&T / Plus
Add-on, the boiler's room thermostat) and the outdoor weather (Open-Meteo). Columns: name, vendor, category, model,
room, state (*Online* / *Întârziat* / *Offline* / *Nesuportat* / *Retras*), last reading (the last real value: for a
plug its last poll, not the last discovery), actions (details, data page, the vendor's settings tab, withdraw /
restore). Filters by category, vendor, state and „Arată retrase”, kept in the address (`?cat=`, `?firma=`, `?stare=`,
`?retrase=1`); `?d=<kind>:<id>` opens one row (the ✎ links of `/clima`, `/aparate`, `/prize`).

- **Expanded row = the only place a device is configured**: plug / meter (alias, role, „flux 1 s”, appliance learning,
  „un singur aparat pe priză”, relay, the settings read from the device), climate sensor (name, room, „afară”), Miele
  appliance (alias, the plug that feeds it), boiler (what was found in the ViCare account). The data pages keep their
  data and link the row.
- **Withdraw / restore** is the same for every kind and calls each owner: plug → no longer polled, gone from
  `/metrics`; Miele → hidden; boiler → no API calls (`HeatingDevices.Enabled`); climate sensor → hidden in Climă and
  not stored (a Govee thermometer through its „acasă” tick). History stays.
- **State of the integrations**: one chip per vendor above the table (OK / Atenție / Eroare / Oprit / Neconfigurat,
  with a short reason) linking its settings tab. The online rules stay those of each integration (and of their
  `*_online` metrics); the page only maps them to one state. No table of its own: a projection of `Plugs`,
  `MieleDevices`, `HeatingDevices` and `ClimateSensors`.
- **„+ Adaugă”** per integration: TP-Link and Shelly = scan now / add by IP; Viessmann and Miele = connect the account
  (the existing OAuth endpoints; without a Client ID, a link to the settings tab); Govee = the account's thermometers
  with the „acasă” tick (moved from the settings); Open-Meteo = the coordinates in its tab. **„Descoperite”** lists what
  was found but not added (the last scan, the Govee thermometers not ticked), each with „Adaugă”.
- A Shelly without a meter but with `temperature:N` (H&T Gen3, Plus Add-on) is a climate sensor, not a plug: the plug
  discovery skips it, and the rows an earlier auto-add stored in `Plugs` were archived once at the first start of
  0.10.0 (logged; a row brought back stays back).

### Device page (`/dispozitive/{key}`)

Since 0.11 every device has a generated page with **all its values** (the ▦ button of its row, and „Pagina cu toate
valorile” in the expanded row). Each integration is *projected* into a common model of entities (no table was
migrated: the plugs, the boiler, the Miele appliances and the climate sensors keep their tables and their pages); each
entity has a kind (sensor, binary, switch, number, select, button, text), a class (temperature, power, energy, door,
moisture …, mapped from Home Assistant's `device_class`), a canonical unit (W, Wh, V, A, °C, %, hPa …) and a state class
(`measurement`, `total_increasing` …). The page draws each entity by `Kind × StateClass × DeviceClass × EntityCategory`:

- a **measurement** (power, temperature) is a card with the value and a 24-hour sparkline, and a chart of the hourly
  min / mean / max below the cards;
- a **counter** (`total_increasing`: the plug's energy counter, Miele's program energy) shows its value; the totals,
  costs and per-period consumption stay on the integration's own page (the „Pagina integrării” button);
- a number **without a state class** (a target temperature, a remaining time) is a card without a chart; texts and
  enums (boiler mode, Miele program) are text cards; **binaries** are a badge with a Romanian text per class (ușă
  deschis/închis, baterie descărcată/OK, umezeală ud/uscat);
- a **switch / number / select / button** is shown with its value and „comandă (neactivată)”: KasaMonitor sends no
  command from this page in 0.11 (the relays keep their own control on the plug pages);
- **diagnostics** (the device's day/month counters, „online”, Wi-Fi signal, firmware details) are folded under
  „Diagnostic”.

History on this page comes only from what is already stored: a plug's power and voltage from `LiveMinuteAggregates`
(streamed channels), a climate sensor's temperature and humidity from `ClimateReadings`. The boiler and the Miele
appliances show their current values; their history stays on `/centrala` and `/aparate`.

**Per-entity preferences** live in the expanded row („Valorile afișate pe pagina dispozitivului”): shown or hidden,
in the history (a chart also for a value without a state class), a name and decimals of one's own. They are stored in
the table `EntityPrefs` (key = the entity key, e.g. `plug:{id}/power`; only what differs from the default). The
row's editor is chosen by the prefix of the device key (`plug:`, `climate:`, `miele:`, `heating:`, `dev:`), never by its
category. An unknown key answers 404.

## Generic devices (MQTT Discovery)

Since 0.11 KasaMonitor reads devices that **describe themselves** on an MQTT broker with Home Assistant's MQTT
Discovery: Zigbee2MQTT (with `homeassistant: {enabled: true}`) and ESPHome (with `mqtt:`). Tasmota (current firmware)
and Shelly do not publish discovery and are not read this way. **Read only**: KasaMonitor never publishes a command;
a switch, number, select or button is shown with its value and „comandă (neactivată)”.

- **Connection** (`/settings/mqtt`): broker (name or address), port, user, TLS (off on a LAN), discovery prefix
  (default `homeassistant`), „Adaugă automat ce se descoperă” (default off). The password is never stored nor shown: it
  comes from the file `KasaMonitor__MqttPasswordFile` (it wins when present, 0.12.0) or `KasaMonitor__MqttPassword` (default
  `/run/secrets/mqtt-kasamonitor.password`), read once at start-up. MQTT 3.1.1 through MQTTnet 5; a refused login is
  shown as „Eroare: autentificare refuzată”, not as connected; reconnection every 5 → 60 s, everything re-subscribed.
- **Discovery**: subscribes to `<prefix>/#` and reads both formats — one payload per entity
  (`<prefix>/<component>/[<node_id>/]<object_id>/config`) and one per device (`<prefix>/device/…/config` with
  `components`) — with the abbreviations (`stat_t`, `dev_cla`, `uniq_id`, `dev`, `ids` …) and the `~` topic base. An empty
  retained payload marks the entity removed (its history stays). An entity without `unique_id` cannot be added (listed
  under „Probleme” on the MQTT tab). Supported: `sensor`, `binary_sensor`, `switch`, `number`, `select`, `button`,
  `text`; other components (`light`, `climate`, `cover` …) are listed as „neacceptat”.
- **Templates**: `value_template` is accepted only in a small subset: `{{ value }}`, `{{ value_json.a.b }}`,
  `value_json['a']`, `value_json.a[0]`, the filters `float`, `int`, `round(n)`, `default(x)`, `lower`, `upper` and
  `* / + -`. Anything else (`{% … %}`, other filters) marks the entity „șablon neacceptat” — the raw payload is never
  used instead.
- **Availability**: `availability` / `availability_topic` with `availability_mode` (all / any / latest) and
  `expire_after` (an unknown availability payload is ignored). Without either, a value is fresh up to 2 expected
  intervals, „Întârziat” up to 6, then unavailable; the interval is the cadence observed (median gap of the last
  messages, 30 s – 12 h), else 1 h for a number and 12 h for an on/off or a text.
- **Retained states** (ESPHome retains them) are shown but are not readings: no row is stored and the time of the last
  live reading stays, also after every reconnection.
- **Untrusted input**: templates of at most 512 characters and 32 nesting levels, payloads up to 64 KB, no wildcard state
  topics, unique ids up to 128 characters, at most 500 devices; everything refused is listed under „Probleme”. A template
  that fails on a message is counted in memory and shown on the card („valoarea nu s-a putut citi …”).
- **Deduced state class**: without `state_class`, `measurement` is deduced only for temperature, humidity, pressure,
  illuminance, CO₂/CO, PM, VOC, voltage, current, power, signal strength and battery with a recognised unit („dedus”
  in the row); never for energy.
- **Adding**: Dispozitive → „+ Adaugă” → MQTT (or the „Descoperite” table) lists what the broker describes and is not
  added, with what it would be; nothing is stored before „Adaugă”. „Cere republicarea” publishes `online` on
  `<prefix>/status` (not retained), so the devices send their description again. One row per physical device: its
  capabilities are badges („+ releu”, „+ temperatură”).
- **Category** (automatic, can be overridden in the expanded row among Contor / Releu / Senzor climă / Alt dispozitiv):
  power + energy counter → Contor; a switch without power → Releu; a temperature without power → Senzor climă; anything
  else → Alt dispozitiv („Senzor ușă”, „Senzor CO₂” …). „Priză” is not offered: **the power and energy of a generic
  device never enter the house totals, the cost or the appliance detection.**
- **Climate**: the temperature (+ humidity and battery) of a generic device also goes to `ClimateReadings` (source
  `mqtt`): it appears in Climă, in Istoric → Climă and in `climate_*`, not again among the generic values. Its freshness
  comes from `expire_after` (half of it) or one hour (a Zigbee sensor reports on change), its „online” from its
  availability. At most one climate record per sensor every 10 s.
- **Storage**: `GenericDevices`, `GenericEntities` (description + the raw discovery payload), `GenericReadings`
  (integer key `(EntityId, Ts)`, `WITHOUT ROWID`) written on a change above a threshold (the entity's precision, else
  0.1 °C, 1 % RH, max(1 W, 2 %)), at least every 30 minutes, at most one row per entity every 10 s (a change inside the
  window is stored at its end); `GenericHourly` (min, time-weighted mean, max, last, count, what a counter counted — a
  `total_increasing` dip under 10 % is ignored, a drop below 90 % is a reset, what was counted during a gap goes to the
  hour after it)
  rolled up every 10 minutes and kept forever; the raw rows are deleted after 90 days (setting).
- **Where it shows**: the device page (`/dispozitive/dev:{id}`: cards, 24-hour charts, a counter's today / yesterday /
  month and 30 daily bars), Istoric → „Alte dispozitive” (the entities ticked „Istoric”: a line for a measurement,
  bars of Σ for a counter, minutes on for an on/off; CSV section `alte-dispozitive`) and `/metrics` (`entity_*`).

## Settings

`/settings` has one tab per address (`/settings/<tab>`, deep links): **Aplicație** (`aplicatie`: tariffs for electricity
and gas, LAN discovery, live stream, data retention, appliance detection, degree days, Grafana; the poll interval is shown
read-only, it comes from the configuration), **Export** (`export`: Prometheus remote write, and the link to the
`/integration` page, which left the top menu), then one tab per integration of the registry (`Ui/Integrations.cs`):
**TP-Link (Kasa, Tapo)** (`tplink`), **Shelly**, **Viessmann**, **Miele**, **Govee**, **Open-Meteo** (`openmeteo`), **MQTT** (`mqtt`, 0.11). An
integration's tab starts with its state (OK / Atenție / Eroare / Oprit / Neconfigurat, last reading, requests today) and
holds only how the app talks to that vendor (account, key, intervals, API budget); what is done with each device
(name, room, role, links, withdraw) is on the devices page. Each tab has its own „Salvează” and writes only its own keys
(the other tabs' values are kept even if they were changed meanwhile); leaving a tab with unsaved changes asks first.
Values are stored in the `Settings` table and applied without a restart. Environment variables are **initial
values only**: they are used while the key has not been saved from `/settings`.

| Setting | Tab (`/settings/…`) | Environment variable (initial value) | Default | Notes |
|---|---|---|---|---|
| Automatic discovery | Aplicație | `KasaMonitor__AutoDiscoveryEnabled` | `true` | |
| Discovery interval (minutes) | Aplicație | `KasaMonitor__AutoDiscoveryIntervalMinutes` | `5` | 1–1440 |
| Broadcast networks | TP-Link | `KasaMonitor__BroadcastNetworks` | empty → `255.255.255.255` | comma-separated IPv4, e.g. `192.168.1.255` |
| Auto-add discovered plugs | Aplicație | `KasaMonitor__AutoAddDiscoveredPlugs` | `true` | |
| TP-Link user | TP-Link | `KasaMonitor__TapoUsername` | — | Tapo app account e-mail |
| TP-Link password | TP-Link | `KasaMonitor__TapoPassword` | — | stored encrypted (ASP.NET DataProtection), shown masked; used for KLAP and AES |
| Tariff per kWh | Aplicație | `KasaMonitor__TariffRonPerKwh` | `1.30` | web UI cost; read at startup |
| Grafana (link) | Aplicație | `KasaMonitor__GrafanaUrl` | — | optional; address of a Grafana dashboard linked from the footer of the home page (`http`/`https`); empty = no link |
| Poll interval (s) | Aplicație (read-only) | `KasaMonitor__PollIntervalSeconds` | `10` | read at startup |
| Shelly discovery | Shelly | `KasaMonitor__ShellyDiscoveryEnabled` | `true` | mDNS `_shelly._tcp.local` + the probe hosts below |
| Shelly password | Shelly | `KasaMonitor__ShellyPassword` | — | optional; admin password of Shelly devices with authentication on (digest auth, user `admin`); stored encrypted like the TP-Link password |
| Shelly probe hosts | Shelly | `KasaMonitor__ShellyProbeHosts` | — | optional; comma-separated IPs, ranges (`192.168.1.10-20`) or CIDR (`/22`–`/32`, max. 1024 addresses), each probed with `Shelly.GetDeviceInfo` (1 s) on every discovery |
| Live stream | Aplicație | `KasaMonitor__LiveStreamEnabled` | `true` | stream of the channels ticked „flux 1 s” on `/dispozitive` |
| Live stream base interval (s) | Aplicație | `KasaMonitor__LiveStreamIntervalSeconds` | `1` | minimum 1; the main meter's interval, the plugs' fastest (they slow down to 10 s when quiet) |
| Keep raw live samples (days) | Aplicație | `KasaMonitor__LiveRawRetentionDays` | `30` | `0` = unlimited; the minute aggregates are never deleted; a smaller value asks for confirmation |
| Allow relay control | Shelly | `KasaMonitor__AllowRelayControl` | `true` | shows the Shelly relay switch (`Switch.Set`); off = hidden and refused. No authentication in KasaMonitor: LAN only |
| Burst trigger (W) | Aplicație | `KasaMonitor__LiveBurstTriggerW` | `50` | jump of the main meter between two readings that sends every plug back to 1 s for 60 s |
| Remote write | Export | `KasaMonitor__RemoteWriteEnabled` | `false` | Prometheus remote write of the live samples (`kasa_live_*`) |
| Remote write URL | Export | `KasaMonitor__RemoteWriteUrl` | — | e.g. `http://192.168.1.10:9090/api/v1/write` |
| Remote write user / password | Export | `KasaMonitor__RemoteWriteUser`, `KasaMonitor__RemoteWritePassword` | — | optional basic auth; stored encrypted |
| Appliance detection | Aplicație | `KasaMonitor__NilmEnabled` | `true` | events, signatures and runs from the main meter (needs a main meter with the live stream) |
| Event threshold (W) | Aplicație | `KasaMonitor__MinEventDeltaW` | `30` | 5–10000; smallest steady-state step of the house power recorded as an event |
| Re-clustering window (days) | Aplicație | `KasaMonitor__ClusterWindowDays` | `30` | 1–365; events grouped again every night at 03:30 |
| ML model | Aplicație | `KasaMonitor__MlEnabled` | `true` | nightly ML.NET classifier over the labelled events; saved as `<data dir>/nilm-model.zip` |
| Keep raw readings (months) | Aplicație | `KasaMonitor__ReadingsRetentionMonths` | `0` = unlimited | raw 10-second readings only; daily history and the minute and hour aggregates are never deleted (the „Putere” chart of /history needs only these: 3 months is a sensible value since 0.14.0). Saving a value that would delete existing readings asks for confirmation with the exact row count |
| Gas boiler | Viessmann | `KasaMonitor__HeatingEnabled` | `true` | reads the Viessmann boiler once the ViCare account is connected (`/centrala`) |
| Viessmann Client ID | Viessmann | `KasaMonitor__ViessmannClientId` | — | client of the Viessmann developer portal; the OAuth tokens are never configured, they come from „Conectează ViCare” and are stored encrypted |
| Boiler poll interval (s) | Viessmann | `KasaMonitor__ViessmannPollIntervalSeconds` | `120` | minimum 60; one API call per poll (120 s = 720 of the 1450 daily calls) |
| Gas price (RON / kWh) | Aplicație | `KasaMonitor__GasPricePerKwh` | `0.31` | cost = m³ × kWh/m³ × price |
| Gas calorific factor (kWh / m³) | Aplicație | `KasaMonitor__GasKwhPerM3` | `10.55` | converts the boiler's m³ to kWh |
| Gas unit fallback | Aplicație | `KasaMonitor__GasUnit` | `m3` | `m3` or `kWh`; only used when the API sends a gas value without `unit` |
| Keep boiler readings (days) | Viessmann | `KasaMonitor__HeatingReadingsRetentionDays` | `0` = unlimited | `HeatingReadings` older than this are deleted once a day; daily gas history is never deleted |
| Miele Client ID | Miele | `KasaMonitor__MieleClientId` | — | client of developer.miele.com; the OAuth tokens come from „Conectează Miele” (`/aparate`) and are stored encrypted |
| — (Miele client secret file) | Miele (state only) | `KasaMonitor__MieleClientSecretFile` | `/run/secrets/miele-client.secret` | file with the client secret, read once at startup; never stored in the database, shown or logged ([Miele appliances](#miele-appliances)) |
| — (Miele client secret) | Miele (state only) | `KasaMonitor__MieleClientSecret` | — | used when the file is missing or empty (0.12.0: the file wins); e.g. a variable of the Portainer stack |
| Outdoor latitude / longitude | Open-Meteo | `KasaMonitor__ClimateLatitude`, `KasaMonitor__ClimateLongitude` | — (off) | location for the outdoor temperature of Open-Meteo (`/settings/openmeteo`); both empty = no request |
| — (Govee API key file) | Govee (state only) | `GOVEE_API_KEY_FILE` or `KasaMonitor__GoveeApiKeyFile` | `/run/secrets/govee-api.key` | file with the Govee Developer API key, read once at startup; never stored in the database, shown or logged ([Climate](#climate)) |
| — (Govee API key) | Govee (state only) | `KasaMonitor__GoveeApiKey` | — | used when the file is missing or empty (0.12.0: the file wins); e.g. a variable of the Portainer stack |
| Heating degree days base (°C) | Aplicație | `KasaMonitor__HeatingDegreeDaysBaseC` | `18` | 10–25; /history „Climă”: degree days = base − outdoor daily mean on days with a mean ≤ base − 3 °C (Eurostat 18 / 15; SR 4839 uses another method, 20 / 12). Saved in /settings/aplicatie (the database wins once saved) |
| Boiler time zone | Viessmann | `KasaMonitor__BoilerTimeZone` | `Europe/Berlin` | IANA zone of the boiler's day: its „today” counters reset at midnight there (Viessmann: Central European Time, 00:00 CET/CEST = 01:00 Romanian time); every daily attribution of the boiler's gas, electricity and heat uses it |
| — (database) | — | `ConnectionStrings__Kasa` | — (**required**) | SQL Server connection string, read at startup; the login needs `db_owner` on its database (`deploy/sql/create-database.sql`); the password never reaches the UI or the log |
| — (data directory) | — | `KasaMonitor__DataDir` | `/data` (image), `data` (development) | DataProtection keys (`dp-keys/`) and `nilm-model.zip`; replaces `KasaMonitor__DbPath` (0.11 and earlier) |
| MQTT broker / port / user | MQTT | `KasaMonitor__MqttHost`, `KasaMonitor__MqttPort`, `KasaMonitor__MqttUser` | — / `1883` / — | 0.11; empty broker = the integration is „Neconfigurat” |
| MQTT TLS, discovery prefix, auto-add, on/off | MQTT | — | off, `homeassistant`, off, on | |
| Keep raw generic readings (days) | MQTT | — | `90` | `0` = never deleted; the hourly rollups are kept forever |
| Generic series limit on /metrics | MQTT | — | `500` | 10–10,000; above it the rest is skipped with a warning (log, MQTT tab) |
| — (MQTT password file) | MQTT (state only) | `KasaMonitor__MqttPasswordFile` | `/run/secrets/mqtt-kasamonitor.password` | read once at start-up; never stored in the database, shown or logged |
| — (MQTT password) | MQTT (state only) | `KasaMonitor__MqttPassword` | — | used when the file is missing or empty (0.12.0: the file wins); e.g. a variable of the Portainer stack |
| — | — | `KasaMonitor__NilmApiToken` | — | optional; when set, every `/api/nilm/*` route needs the header `X-Api-Key` with this value. Set it before exposing KasaMonitor outside the LAN ([HTTP API (LAN)](#http-api-lan)); read per request |

The DataProtection keys are kept in the data directory, in `<data dir>/dp-keys` (`/data/dp-keys` in the container,
on the persistent volume). If that directory is lost, the stored password cannot be decrypted and must be entered
again in `/settings` (the log shows `Stored secret could not be decrypted`). On Linux the keys are stored
unencrypted on disk (ASP.NET logs "No XML encryptor configured" at startup); protect the volume.

Do not commit credentials; prefer `/settings`.

## Monitoring with Prometheus & Grafana

**Start with the `/integration` page** of the web UI (Setări → Export → „Deschide pagina Integrare”, `http://<host>:8910/integration`).
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
(`plug` | `main` | `sub`, the role set on `/dispozitive`). For the channels ≥ 1 of a multi-channel Shelly meter `mac` is
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

**Single-appliance plugs** (since 0.9.1, see [Single-appliance plugs](#single-appliance-plugs)): the `kasa_*` series
above keep their labels (`alias`, …), so no series and no history changes when a plug is ticked „un singur aparat pe
priză”. The appliance's name is published in a family of its own, only for those plugs:
`kasa_appliance_plug_power_w{appliance, alias}` (gauge, W; the same value as `kasa_power_load` of that plug; `appliance`
normalised like `kasa_appliance_*`: no diacritics, at most 40 characters). Renaming the appliance or clearing the flag
removes its series. Example: `kasa_appliance_plug_power_w{appliance="Frigider"}`.

The gas boiler has its own `heating_*` family, see [Gas boiler (Viessmann ViCare)](#gas-boiler-viessmann-vicare); the
temperature and humidity sensors the `climate_*` family, see [Climate](#climate).

Home page (see [Home page](#home-page-acasă)), computed once per minute; a value that is not known has no series:

| Metric | Type | Unit | Labels | Meaning |
|---|---|---|---|---|
| `kasa_house_standby_w` | gauge | W | — | standby / always-on floor: median of (main meter − plugs) between 01:00 and 05:00 over the last 7 nights; 0 when it is 5 W or less (not measurable) |
| `kasa_bill_estimate_ron` | gauge | RON | `kind` = `electricity`, `gas`, `total` | bill estimate of the current month: used so far + mean per elapsed day × days left |

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
or no longer exported are removed. These counters restart at 0 with the application: use `increase()`. The labels of
`kasa_appliance_energy_wh_total` are the name and the category: when a signature is renamed, takes another category or
absorbs another one in a merge (since 0.8.2 it can take the absorbed one's category, or the plug rule can rename it),
its old series disappears and a new one appears under the new labels **with the cumulative value** — in Grafana a new
series, not a continuation (sum by `appliance` over time, or use `increase()` per series).

### Live samples via remote write (`kasa_live_*`)

Two metric families reach Prometheus:

- `kasa_*` (above): scraped from `/metrics` (typically every 15 s), the last value of every plug at scrape time.
- `kasa_live_*`: pushed by KasaMonitor with **Prometheus remote write 1.0** (protobuf `WriteRequest` + snappy,
  `Content-Encoding: snappy`, `X-Prometheus-Remote-Write-Version: 0.1.0`), with each sample's own timestamp, so the
  second-by-second fluctuations of the live stream reach Grafana without scraping every second:
  `kasa_live_power_w` and, when reported, `kasa_live_voltage_v`, `kasa_live_current_a`, `kasa_live_power_factor`.
  Same labels as `kasa_*` (`alias`, `mac`, `model`, `protocol`, `role`). Only the samples stored as raw rows are sent
  (power changed by more than max(1 W, 1 %), or every 60 s), so a quiet plug costs one sample a minute.

Enable it in `/settings/export` → *Prometheus remote write* (URL, optional basic auth; or the
`KasaMonitor__RemoteWrite*` variables). Prometheus must run with `--web.enable-remote-write-receiver` (already in
`deploy/docker-compose.monitoring.yml`). Samples are queued in memory and sent every second (or at 500 samples);
a failed request is retried with backoff 1 → 60 s, an HTTP 4xx other than 429 drops the batch (e.g. out-of-order
samples), and samples older than 5 minutes in the queue are dropped. State in `/settings/export` and on `/metrics`:
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
  `Shelly.GetConfig`. Add by IP on `/dispozitive` tries Shelly after legacy and Tapo. Components are found from the keys of
  `Shelly.GetStatus`, never from the model.
- **Channels** — one plug per measured channel, identity (MAC, channel). Default alias: the channel name
  (`em1:N.name`), otherwise the device name / id plus ` ch0`, ` ch1`; rename on `/dispozitive` like any plug.
- **Password** — devices with authentication on (`auth_en: true`) need the *Shelly password* in `/settings/shelly`
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
- **Reversed clamp** — the „Prize” page card shows „inversat?” when a Shelly channel reads below −10 W (simple rule on
  the current reading, accepted instead of "negative for a while"); fix the clamp direction (`reverse`) in the Shelly
  app if it is not real export.
- **Writes** — reads use only `Shelly.Get*`, `EM1.GetStatus`/`PM1.GetStatus`/`Switch.GetStatus`/`EM.GetStatus`,
  `EM1Data.Get*`. The single write is `Switch.Set` (relay switch, below); KasaMonitor never changes configuration
  (`*.SetConfig`), never reboots, resets or updates the device.

See `docs/device-api-capabilities.md` for what is shown, what exists and is not shown, and what would need writes.

## Roles, live stream, Contor page, relay

**Roles** (`Plugs.Role`, `/dispozitive` → the channel's row, *Rol*, icons plug / house / meter): *plug* (`0`, default for Kasa, Tapo
and Shelly `switch`/`pm1`), *main meter* (`1`, the channel that measures the house supply; at most one, also enforced
by a partial unique index; setting a new one turns the previous one into a sub-meter; button „Setează ca contor
general”) and *sub-meter* (`2`, default for Shelly `em1`/`em` channels). Only plugs count in totals: „Prize”
header, Istoric „Toate” (stacked bars; the house is a separate „Casă” line, never added to the plugs), Grafana totals
(`role="plug"`). Upgrading from 0.3.0 marks the existing Shelly meter channels (`S3EM…`, `SPEM…`, apps `EMG3`,
`ProEM`, `Pro3EM`) as sub-meters; other `em1`/`em` devices already in the database (another model code) stay plugs:
set their role on `/dispozitive`. Choose the main meter once on `/dispozitive`. Roles cannot be changed on withdrawn rows.

**Live stream** (`/dispozitive` → checkbox „flux 1 s” per channel, `Plugs.StreamEnabled`; default on for the main meter
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
  re-authenticated by the transport when it expires (needs the TP-Link account in `/settings/tplink`).
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
  for confirmation in `/settings/aplicatie`); aggregates are kept forever. The 10 s polling (`Samples`, `/metrics`) is unchanged
  and the live stream is **not** exported to `/metrics`.
- **Volume** (≈ 146 bytes per raw row including the index; measured on 2026-11-01 with the EM Gen3 and five KP115):
  a quiet plug writes ≈ 1 row/min (the witness) ≈ 1,440 rows ≈ 0.2 MB/day; a plug with a cycling load (UPS,
  network rack with fans/compressor) wrote 30–42 % of its readings at 1 s ≈ 26,000–36,000 rows ≈ 3.8–5.3 MB/day; the
  main meter at 1 s wrote 94 % ≈ 81,000 rows ≈ 11.8 MB/day (a whole house rarely holds within 1 %). With the default
  30 days: ≈ 350 MB for the main meter + 6–160 MB per plug. Minute aggregates: 1,440 rows per channel per day.
- To stop it: untick „flux 1 s” on a channel, or *Settings → Flux live* off (applies within 10 s).

**Contor page** (`/contor`, menu „Contor”): alias of the main meter, power now (1 s; falls back to the 10 s poll when
the stream is older than 5 s), rest of the house (see below), voltage (red outside 207–253 V), current, power
factor, frequency, relay; live chart of the last 10 minutes (the main meter and any number of plugs, toggled in „Linii
afișate” and read from the same stream, each with a colour no other active line has; the meter can be switched off to compare plugs on their own scale). All the lines share a grid of whole UTC seconds, so the tooltip
shows them together: a sample only updates its line's latest value, which is held until the next reading (a plug read
every few seconds draws in steps) and for at most 30 s, then the line has a gap. Once a second the new points of all the
lines are pushed to ApexCharts, the whole 10-minute window once a minute; today per minute
(min/avg/max from the aggregates); energy per day for 30 days (from `DailyEnergy`) with
cost; the sub-meters; the last 20 relay commands. Without a main meter it links to `/dispozitive`. The „Prize” page shows a
„Casă” card (house power, rest of the house, relay) above the plugs.

**Rest of the house** („Prize” page „Casă” card, `/contor`) is computed from averages aligned on the **last whole
minute**, not from the latest instantaneous values (the meter arrives every second, the plugs every 1–10 s, so their
latest values are from different moments): the main meter's mean over that minute minus Σ the plugs' means over the
same minute (from their live samples; an online plug without a live sample in that minute counts with its latest
polled reading). Without a live sample of the main meter in that minute (stream off, channel not streamed, WebSocket
down) the same minute is computed from the means of the 10 s polled samples (`Samples`), labelled „din citirile la
10 s”. The value is computed once per minute for all pages (`HouseRestService`). With |main − plugs| ≤ max(15 W, 3 % of the main meter) it shows „≈ 0 W” (*în marja de eroare a
măsurătorilor*); with the plugs above the meter beyond that margin it shows „0 W” and the warning *prizele depășesc
contorul: verifică poziția clemei*. Energy (the „Casă” report on `/history`) uses the same rule with a margin of
max(15 W × the interval's hours, 3 %): rest = max(0, house − plugs), marked „≈” within the margin; when the plugs
exceed the meter on an interval whose live coverage of the main meter is under 90 % of its (elapsed) minutes, it is
marked „date parțiale” instead of the clamp warning.

**Relay**: on channel 0 of a Shelly meter with `switch:N` (EM Gen3: `switch:0`, a contactor) and on Shelly
`switch` plugs (Plus Plug S, …) a Pornit/Oprit switch appears on `/contor`, in the „Casă” card and in the *Detalii*
row of `/dispozitive`. Every command needs a confirmation („Releul comandă un contactor; sigur?”) and sends
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
2. `/settings/viessmann`: paste the Client ID, save.
3. `/settings/viessmann` → **Conectează ViCare** (the connection has one owner since 0.10.0; `/centrala` shows the state and links there): KasaMonitor redirects to Viessmann (OAuth 2.0 authorization code with PKCE
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
by KasaMonitor**: every poll writes the boiler's "today" value on the **boiler's day of KasaMonitor's reading** (the
feature's timestamp is not used, the gateway re-sends features in bulk) into `HeatingEnergyDaily`, which keeps per
purpose the day's highest value (`GasHeatingMaxM3`, `GasDhwMaxM3`) and the last value seen (`…LastM3`). A closed day
counts with its highest value, the current day with its last value. Boilers with a `day[]` series (day[0] = today) fill
every day of the series.

**The boiler's day (since 0.8.1).** The Vitodens resets its „today” counters at **00:00 in its own time zone**, Central
European Time (measured on 2 Nov 2026: the reset arrives at 23:00 UTC = 01:00 Romanian time; in summer 00:00 CEST =
01:00 EEST, the difference stays one hour). Setting `BoilerTimeZone` (default `Europe/Berlin`, `/settings/viessmann`)
is that zone, and **every daily attribution of the boiler's gas, electricity and heat uses it**, not the container's
`TZ`: the daily rows, the „Rapoarte” day/week/month/year, the hourly profile (labelled in local time: a boiler day runs
01:00–01:00 Romanian time; the reports say „zi = după ceasul centralei (CET), se încheie la 01:00 ora României”), the
30-day chart, the comparisons and `heating_gas_consumption_m3_total`. The electricity reports of the plugs and the meter
stay on the local day. In the first 30 minutes after the boiler's midnight (the end of the reset window) a „today”
value that is still ≥ 90 % of the previous day's highest (or not lower than its last value) is **yesterday's total read
late**: it goes to the previous day (it may raise it) and the new day starts with the first lower value
(`BoilerClock.IsStale`; gas, heat and electricity). After a day of about zero (under 0.05 m³ of gas, 0.5 kWh of heat,
0.05 kWh of electricity) nothing is stale, so a new small value is never counted twice. A gas value lower than the
day's stored highest is the boiler's **reset** only when the boiler's time is between 23:45 and 00:30: the day then
gets highest = last = the new value (a reset seen in 23:45–24:00 belongs to the next day; one seen in 00:00–00:30
carries the stored highest, yesterday's total read after midnight, back to the previous day). Any other drop, a drop
under 20 % during the day included, **leaves the day as it is** (highest and last) until the boiler's value passes it
again, so the day and `_total` never go down; it is logged at Warning once per purpose and boiler day (the hourly
profile still counts a drop under 20 % with no reset in the 20 hours before as a reset). At startup and once a day
(30 minutes after the boiler's midnight) KasaMonitor **repairs** the current boiler day when its highest still holds
the previous day's total: highest > last and either last ≤ 50 % of highest or the highest equals the previous day's
highest within max(0.05, 1 %). The previous day gets max(its highest, that value), the current day highest = last;
the heat produced and the electricity of that day equal to the previous day's (same tolerance) go to the previous day
and are cleared on the current day (the next reading writes them again). Every repair is logged at Information. This
fixes the day written by 0.8.0 on 2 Nov 2026 (highest 1.4 m³ from the readings 00:00–01:00, last 0.7–0.8 m³, and
yesterday's heat and electricity).

The period values are stored as they come (`HeatingEnergyPeriods`). The page shows hot water
vs heating for today / 7 days / month / last month / year with the cost, a stacked bar chart of the last 30 days and
the apparent efficiency of last month (heat produced / gas × kWh/m³). **The consumption is estimated by the boiler's
controller, not measured by a gas meter**: compare it with the gas bill before relying on it.

**Burner timeline (since 0.8.4).** The first chart of `/centrala`, „Cronologie arzător”, shows when the burner burns and
for what — **heating** (orange), **hot water** (blue), **burning, purpose unknown** (dark grey); off = no bar — over
6 h / 24 h / 3 days / 7 days (◀ ▶ move by one window), with the supply, hot water (target dashed) and room temperatures
and the modulation and pump % on the same time axis (they replace the former „Ultimele 24 de ore” charts). The tooltip of
a bar gives start, end, duration, mean modulation and the gas the boiler's counters attributed to it. The Viessmann API
has no explicit „heating hot water” signal (no `heating.dhw.charging` on a Vitodens 050-W), so the purpose is inferred
(`BurnerTimeline`, Core) on each contiguous run of `BurnerActive = true` (a missing reading of up to 5 minutes beyond the
usual cadence does not break a run; a bar runs from the first reading with the burner on to the next reading):
(a) the boiler's „today” gas counters, which the boiler updates late (on 2 Nov 2026 the heating counter jumped by 0.6 m³
20 minutes after a run had ended), from the reading before the run to at most 60 minutes after its end, but never past the
reading before the next run (each increase belongs to one run): only hot water grew → hot water, only heating → heating,
both → the run is split in proportion to the increases when longer than 10 minutes (heating first), otherwise unknown;
(b) only when no counter grew (0.1 m³ steps), tuned for a combi boiler whose hot water sensor follows the supply while the
burner runs (rises = highest value in the run − the reading before it): hot water when the hot water rose ≥ 2 °C more
than the supply, or the supply stayed flat (< 1 °C) and the hot water rose ≥ 2 °C; heating when the supply rose ≥ 2 °C
and the hot water did not rise more; otherwise unknown. The same rule, applied at every poll to the readings of the last
12 hours, gives `heating_burner_purpose_info`: a run still going may be „unknown” until a counter moves. The page
re-evaluates closed runs when the late counter arrives; samples already in Prometheus keep the value they had.

**Storage.** `HeatingDevices`, `HeatingReadings` (one row per poll: about 720 rows a day at 120 s; kept forever unless
`HeatingReadingsRetentionDays` > 0),
`HeatingEnergyDaily`, `HeatingEnergyPeriods` and `HeatingRawFeatures` (only the latest raw answer, overwritten).

**Metrics** (`/metrics`, labels `device` = e.g. `Vitodens 050`, `model` = e.g. `E3_Vitodens_050_0822`; a value the
boiler does not report has no series):

| Metric | Type | Unit | Source |
|---|---|---|---|
| `heating_burner_active` | gauge | 0/1 | `heating.burners.0.active` |
| `heating_burner_purpose_info{purpose}` | gauge | 1 | built by KasaMonitor at every poll: `heating` / `dhw` / `unknown` / `off`, one series = 1 (rule above, „Burner timeline”) |
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
| `heating_gas_consumption_m3_total{purpose}` | counter | m³ | built by KasaMonitor: Σ highest value of the closed boiler days + last value of the current boiler day (`BoilerTimeZone`), recomputed from the database at every poll; a lower sum (a correction of today's value, a repair) is logged at Warning once per day, and the exported counter never goes down (it waits until the sum passes it again) |
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

## Miele appliances

KasaMonitor reads Miele appliances (washing machine, tumble dryer, washer-dryer; any appliance of the account is
listed) through the **Miele 3rd Party API** (developer.miele.com). Read-only (since 0.8.5): no program is started or
stopped (scope `openid mcs_thirdparty_read`; writing needs `mcs_thirdparty_write`, a later version).

**One-time setup**

1. Register at <https://developer.miele.com/get-involved> (application name + e-mail): you get a Client ID and a
   Client Secret. One credential pair per e-mail address; registering again invalidates the old pair. Personal,
   non-commercial use only.
2. Put the Client Secret alone in a file and mount it read-only into the container at
   `/run/secrets/miele-client.secret` (another path: `KasaMonitor__MieleClientSecretFile`; or, less safe,
   `KasaMonitor__MieleClientSecret`). It is read once at startup, never stored in the database, shown or logged. The
   compose lines are in `deploy/README.md` (*Miele client secret*).
3. `/settings/miele`: paste the Client ID, save. The tab shows whether the secret was loaded and from where.
4. Open KasaMonitor at the address you use (the redirect URI is built from it: `{scheme}://{host}/miele/callback`,
   e.g. `http://192.168.254.2:8910/miele/callback`) → `/settings/miele` → **Conectează Miele** (`/aparate` shows the state and links there). KasaMonitor redirects to the
   new Miele OAuth provider (`auth.domestic.miele-iot.com`, authorization code with the client secret, no PKCE, random
   `state` valid 10 minutes, single use, tied to the browser by the HttpOnly cookie `miele_oauth`). Log in with the
   Miele account (country: the Miele subsidiary of the account, e.g. Romania) and **tick the appliances** on the consent
   page: only those are visible. The developer portal has no redirect-URI field; Miele only requires the exchange to
   use the same URI as the authorization.

**Tokens.** Access and refresh token are stored encrypted (DataProtection, like the TP-Link password). The access
token is refreshed when 10 % of its lifetime (`expires_in` of the answer; the spec's example is 30 days) is left;
every refresh returns a new refresh token, which replaces the old one. Miele limits the tokens per client and
customer (too many can deactivate the client), so KasaMonitor never re-authorizes on its own: a refused refresh
(`invalid_grant` …) marks the account disconnected („Reconectează”, with the reason) and is retried at most once an
hour; a temporary failure (5xx, 503 limit, network) keeps using the access token while it is valid.
**Deconectează** calls `POST https://api.mcs3.miele.com/thirdparty/logout` and clears the tokens. Changing the Client
ID clears them too.

**Reading.** One SSE subscription for all appliances, `GET https://api.mcs3.miele.com/v1/devices/all/events`
(`Accept: text/event-stream`), preceded at every (re)connection by one full `GET /v1/devices`. The event names are not
documented consistently (docs: PING / IDENT / ACTION; the wire, as read by pymiele: `ping` / `devices` / `actions`):
both are accepted, any case, and a nameless event is classified by its data. No line for 60 s (PING comes after
20 s of silence) = dead connection. Reconnection waits 5 s, 10 s, 20 s … at most 5 minutes; after 3 failed streams
in a row KasaMonitor polls `GET /v1/devices` every 60 s until a stream works again. The stream is reopened when the
token is due for refresh. An API 401 triggers one forced refresh and one retry. The only other periodic call is the
program history below. The labels `value_localized` come in English (Romanian is not among the API languages).

**Program history (since 0.8.6).** Miele keeps the last **five** finished runs per appliance server-side
(`GET /v1/devices/{id}/lastUsedPrograms`, washing machines, tumble dryers, dishwashers, washer-dryers): program,
`startAt`/`endAt` (UTC), calculated water (l) and energy (kWh) — `null` without EcoFeedback; washer-dryers return
program and times only — and, with `calculateEnergyMonitoring=true`, a coarse **estimated** energy profile in fixed
15-minute slots (not a measured load profile). KasaMonitor reads it once after the first read of the appliances after
start or after connecting the account, then **every 6 hours** and **2 minutes after an observed cycle ended** (one
call per appliance not archived; no other calls). An appliance that answers 400/404 to `calculateEnergyMonitoring` is
logged once and read without it from then on; 429/503 is logged and nothing is called before `Retry-After` (else
1 hour); 403 means the *appliance statistics* consent is missing in the Miele app (shown on `/aparate`). The runs are
united with `MieleCycles`: a run whose `startAt` is within 10 minutes of an observed cycle's start, or that overlaps it
by at least 50 % of the shorter one, is the same run — the cycle keeps its phases, its ecoFeedback values and the
plug's energy and takes the API's start/end (`ApiStartTs`/`ApiEndTs`), energy and water (`ApiEnergyKwh`/`ApiWaterL`),
the energy profile (`EnergyProfileJson`, `[{ts, kwh}]`) and the program if it had none (`Source = merged`). A run
without such a cycle becomes a cycle of its own (`Source = api`, no phases, no ecoFeedback values, the linked plug's
energy over the run if the plug has data then). A run matching a cycle still open waits for the next read. `(DeviceId, ApiStartTs)` is unique, so reading the
same five runs again never duplicates them. So the API shows the last five runs from the first day, and from the
connection on KasaMonitor keeps every run.

**What is stored**

- `MieleDevices`: one row per appliance (Id = Miele deviceId, type, technical type/model, the app's name, an alias you
  can edit, the plug that feeds it, archived, last seen).
- `MieleStateSnapshots`: the state (status, program type and program, phase raw + text, remaining / elapsed / start
  time, target temperature, spin, drying step, door, failure/info signals, ecoFeedback energy/water and forecasts,
  remote-control flags) on every change, and at most every 5 minutes otherwise.
- `MieleCycles`: one row per program run: it starts when the status turns *Running* (5) and ends at *End programmed*
  (7), *Idle* (10) or *Off* (1), or 30 minutes after the last *Running* (pause, failure, interrupted, not connected).
  It keeps the program, the phases as `[{phaseRaw, phaseText, fromTs, toTs}]`, the highest `ecoFeedback` energy and
  water seen during the run (the appliance's estimate; the counters reset when the program ends), the energy the linked
  plug measured over the run (live minute aggregates, else the 10 s polls) and the end reason. An open cycle survives a
  restart. Since 0.8.6 also the source (`observed` | `api` | `merged`), the run's start/end, energy and water in the
  API history and Miele's 15-minute energy profile (see *Program history*). Three separate consumption pairs, never
  overwritten by each other: `EnergyKwh`/`WaterL` = ecoFeedback seen live, `ApiEnergyKwh`/`ApiWaterL` = the API
  history, `PlugEnergyWh` = the plug.

**Texts.** Status and appliance type follow the official enums. **Program phases are not documented by Miele**: the
numbers → names come from the community (Home Assistant's `miele` integration, e.g. washing machine 260/11004 main
wash, 261/11005 rinse, 265 drain, 266/11010 spin; tumble dryer 514/11018 drying …) and differ between appliance
generations (the spec's own example shows phase 1795 „Main wash”, in no list). An unknown phase is shown with the
API's `value_localized`, else „faza <raw>”. Program names are the API's `value_localized`.

**Page `/aparate`** (menu „Aparate”): the connection (stream / polling, last event, last error; connect and
disconnect in `/settings/miele`); per appliance a card with alias, model, status (coloured), program, phase (with the load it usually
switches on: heating element in pre-wash / main wash, motor in the spin, pump in the drain — informative), remaining
time with a progress bar, target temperature / spin, door, the appliance's energy and water so far and the forecasts
(relative 0–100 %), the current cycle as a coloured phase band, and the plug that feeds it with its power now (alias and plug
are set on `/dispozitive`, the appliance's row, since 0.10.0); then „Istoric cicluri”: one table for all appliances or one (filter), 50 cycles per page, newest first: start,
duration, program, the source (discreet badge: observat / din API / ambele), phases band, energy (the API history's,
else the plug's, else ecoFeedback) and water (the API history's, else ecoFeedback), the tooltip naming the source, the
plug's measured energy, cost (the plug's energy when linked, else Miele's, at the tariff of `/settings/aplicatie`) and end
reason. A row opens its detail: where it comes from, its start/end in the Miele history, the three values side by
side (API history, ecoFeedback, plug), and a
small chart of Miele's estimated profile (average W per 15-minute slot) next to the linked plug's power on the same
interval (live minute averages, else the 10 s polls), with both totals over the slots. Under the table: „Miele oferă
ultimele 5 rulări; de la conectare încolo KasaMonitor păstrează tot.”

**Ground truth for the appliance detection.** For an appliance with a linked plug, a main-meter event that the plug
already confirmed (the plug's level changed by the same ΔP, `LabelPlugId`) while the appliance was *Running* is
labelled `„<alias> · <phase>”` (source „din priză”, not the user); its signature, if still anonymous
(„Necunoscut #n”), takes that name (source *plug*) and keeps it when the plug rule later links it. Never for an event
without the plug's confirmation: an appliance running does not prove that a step of the house meter is it.

**Metrics** (`/metrics`, labels `device` = alias, `type` = `washing_machine` | `tumble_dryer` | `washer_dryer` | …;
the serial is never a label):

| Metric | Type | Notes |
|---|---|---|
| `miele_online` | gauge | 1 = connected to Miele (status ≠ 255) and read recently |
| `miele_status_info{status}` | gauge | 1 for the current status (`running`, `end_programmed`, `off`, …) |
| `miele_program_phase_info{phase}` | gauge | 1 for the current phase (text) |
| `miele_remaining_seconds`, `miele_elapsed_seconds` | gauge | program times |
| `miele_target_temperature_c`, `miele_spin_rpm` | gauge | program settings |
| `miele_eco_energy_kwh`, `miele_eco_water_l` | gauge | the running program so far (ecoFeedback; reset at the end) |
| `miele_eco_energy_forecast_ratio`, `miele_eco_water_forecast_ratio` | gauge | forecast of the selected program, relative 0..1 (not kWh / litres) |
| `miele_cycles_total` | counter | completed cycles, including the runs known only from the API history (each run once) |
| `miele_last_cycle_energy_kwh`, `miele_last_cycle_water_l` | gauge | last completed cycle: the API history's values when it has them, else ecoFeedback |
| `miele_door_open` | gauge | 0/1 |
| `miele_connected` | gauge | no labels; 1 = SSE stream open |

The two forecast metrics are **fractions from 0 to 1** (`ecoFeedback.energyForecast` / `waterForecast`: the selected
program's expected use relative to the appliance's range, as the spec defines them), not kWh or litres; `/aparate`
shows them as percentages. The consumption so far (`miele_eco_energy_kwh`, `miele_eco_water_l`) is in kWh and litres.

```promql
miele_status_info{status="running"} == 1          # what runs now
increase(miele_cycles_total[7d])                   # cycles per week
```

## Climate

Since 0.8.7 the page **`/clima`** (menu „Climă”) shows temperature and humidity per room from four sources, in one
generic model: a **sensor** (`ClimateSensors`: source, external id, editable name, room, „afară” = outdoor, archived,
last seen, battery) and its **readings** (`ClimateReadings`: time, temperature °C, humidity % RH, battery).

| Source (`source`) | What | How it is read | Interval |
|---|---|---|---|
| `viessmann` | the boiler's room thermostat only („Termostat (cameră)”, the Zigbee room sensor) | a virtual sensor fed from the stored boiler readings (`HeatingReadings`); **no extra Viessmann API call**. On the first run the readings of the last 30 days are imported. The supply and hot-water temperatures are water, not air: they stay on `/centrala` and are not climate sensors (since 0.9.3; the „Tur centrală” / „Apă caldă” sensors of earlier versions are removed at startup with their readings and daily rows — the same values remain in `HeatingReadings`) | every minute (new readings only) |
| `govee` | Govee Wi-Fi thermometers (e.g. H5179) of the account | Govee Developer API v1: `GET https://openapi.api.govee.com/router/api/v1/user/devices` and `POST …/device/state` (header `Govee-API-Key`); the temperature comes in **°F** and is converted to °C; an offline device answers `online:false` with `""` values (nothing is stored, the card shows „offline la sursă”) | 10 minutes per ticked thermometer (they upload about every 30 minutes); the device list every 6 hours |
| `shelly` | Shelly H&T Gen3, Shelly Plus Add-on (DS18B20 / DHT22) | local RPC: `Shelly.GetStatus` components `temperature:N` (`tC`) and `humidity:N` (`rh`, paired by id), battery from `devicepower:0`; found by mDNS (`_shelly._tcp`, only when Shelly discovery is on) | every minute; an H&T on batteries sleeps and is read only when it is awake |
| `openmeteo` | the outdoor temperature and humidity („Afară”) | `https://api.open-meteo.com/v1/forecast?latitude=…&longitude=…&current=temperature_2m,relative_humidity_2m&timezone=auto` (free, no key) | 15 minutes, only when latitude and longitude are set |

**Setup.** Govee: mount the API key as a file (`/run/secrets/govee-api.key`; another path: `GOVEE_API_KEY_FILE` or
`KasaMonitor__GoveeApiKeyFile`; or, less safe, `KasaMonitor__GoveeApiKey`; compose lines in `deploy/README.md`,
*Govee API key*). It is read once at startup, never stored in the database, shown or logged. `/settings/govee`
shows its state; `/dispozitive` → Adaugă → Govee lists the devices of the account; only those ticked **„acasă”** are read and stored (all are unticked at first, so
thermometers of other places in the same account are never requested); unticking hides the sensor (its history
stays). Requests per day ≈ 144 per ticked thermometer + 4 for the list (Govee allows 10,000). Open-Meteo:
`/settings/openmeteo`, latitude and longitude (both empty = off). Shelly: nothing to set;
a sensor appears at its first value. A Shelly H&T without a meter is not a plug: the plug discovery skips it (0.10.0).
Name, room and „afară” of every sensor are set on `/dispozitive` (the sensor's row; the ✎ on a `/clima` card links it).

**Storage.** A reading is stored when the temperature changed by **≥ 0.1 °C** or the humidity by **≥ 1 % RH** since the
last stored reading, and at least every **30 minutes** (heartbeat); a value read in between only updates the card and
`/metrics`. Raw readings are kept **without limit** since 0.9.2 (the climate history of `/history`; before: one year). A sensor is created at its first value with the
source's name; name, room and „afară” are edited on its card in `/clima` (the boiler's thermostat has no room at first).

**Page.** One card per sensor: name, room, temperature, humidity, battery, source and the age of the last value
coloured against the source's interval (Viessmann: its poll interval; Govee 10 min; Shelly 1 min, 2 h on batteries —
an assumption; Open-Meteo 15 min): green up to 2 intervals, yellow up to 6, red beyond. A chart of all sensors over
**24 h / 7 days / 30 days** (temperature, and humidity as a second chart; 5-min / 30-min / 2-h averages; click a
name in the legend to hide it) and a table **min / mean / max** per sensor for the period. The mean is weighted by how
long each value held (a value holds until the next reading, at most 60 minutes; a longer silence is a gap), so a
sensor written often while it changed is not over-counted. `/centrala` shows an **„Afară”** tile next to its
temperatures when an outdoor sensor has a value (a physical one before Open-Meteo). The in-memory service
`ClimateState` (sensors, values, freshness, `Outdoor()`) is what other pages use.

**Metrics** (labels `sensor` = name, `room`, `source`; one series per sensor, a renamed sensor replaces its series, an
archived one disappears; ids and MACs are never labels):

| Metric | Type | Notes |
|---|---|---|
| `climate_temperature_celsius` | gauge | last temperature read |
| `climate_humidity_percent` | gauge | last humidity (no series for sensors without humidity) |
| `climate_battery_percent` | gauge | Govee / Shelly battery, when reported |
| `climate_online` | gauge | 0/1: the source reports it online and the last value is within 6 intervals of its source |
| `climate_last_reading_timestamp_seconds` | gauge | unix time of the last value read |
| `climate_api_requests_today{source}` | gauge | HTTP requests to Govee / Open-Meteo today (local day; in memory, restarts at 0) |

**Generic devices** (0.11, labels `device`, `entity`, `room`, `integration`; never an id or a MAC). One family per class
with a fixed unit — a Prometheus metric refers to a single unit, so there is no family mixing units:

| Metric | Type | Notes |
|---|---|---|
| `entity_temperature_celsius`, `entity_humidity_percent`, `entity_moisture_percent` | gauge | the temperature of a sensor routed to Climă is only in `climate_*` |
| `entity_power_watts`, `entity_apparent_power_va`, `entity_reactive_power_var` | gauge | never part of the house totals |
| `entity_energy_wh_total` | counter | a `total` / `total_increasing` energy (Wh); a device reset is skipped |
| `entity_voltage_volts`, `entity_current_amperes`, `entity_frequency_hertz` | gauge | |
| `entity_battery_percent`, `entity_signal_strength_dbm` | gauge | |
| `entity_co2_ppm`, `entity_co_ppm`, `entity_pm1_ugm3`, `entity_pm25_ugm3`, `entity_pm10_ugm3`, `entity_voc_ugm3` | gauge | |
| `entity_pressure_hpa`, `entity_illuminance_lux`, `entity_duration_seconds` | gauge | |
| `entity_state` | gauge | 0/1 of a binary sensor or a switch |
| `entity_value{unit}` | gauge | a number whose class has no family (link quality, rpm …), with its unit as a label |
| `entity_online{device,room,integration}` | gauge | 0/1 per device |

Only the entities ticked „Metrici” in the device's row (by default the main measured classes, not diagnostics, texts or
enums); at most 500 series (setting).

```promql
avg by (room) (climate_temperature_celsius{source!="viessmann"})   # rooms
climate_online == 0                                                  # sensors without data
```

Not yet verified on hardware: the Shelly H&T Gen3 and the Plus Add-on (implemented from the Gen2+ API docs, tested on
synthetic answers); the Govee battery capability (H5179 does not report it in the captured answers).

## Home page („Acasă”)

The home page (`/`, menu „Acasă”) follows Home Assistant's Energy dashboard with the „Now” view of Sense. One period
selector (Azi / Săptămână / Lună / An, ◀ ▶) drives the period cards (Energie, Pe ore, Aparate, Unde s-a dus energia) and
is kept in the address (`/?p=zi|saptamana|luna|an&d=yyyy-MM-dd`: bookmarks, back after another page, shared links);
„Acum în casă” and row 3 (today vs yesterday, the current month, the boiler, Miele, the temperatures) stay on the
present. „Compară cu …” (for today „Compară cu ieri (la aceeași oră)”) shows the previous period (pale bars, Δ) on Energie
and Pe ore. The former Dashboard (one card per plug) is now „Prize” at `/prize`. Menu (Romanian, routes unchanged): Acasă,
Prize, Contor, Centrală, Aparate, Climă, Consumatori, Istoric (`/history`), Dispozitive (`/dispozitive`), Setări (`/settings`)
(10 entries; „Integrare” is reached from Setări → Export since 0.10.0).

Numbers: W whole, kWh with 2 decimals below 10 and 1 above, m³ and lei with 2 decimals, a Δ with the decimals of its
value (the same rules as /history). A **cost total** (curent + gaz) exists only when every component that should exist
does (electricity, and gas when a boiler is connected): a missing part is never counted as 0, so there is no Δ of cost
between a total and gas alone.

- **Acum în casă** (live, 1 s, from the stream of the main meter and the plugs): the main meter as a big number and bubbles
  whose **area ∝ W**: every plug with all its power (blue; grey = off / offline), the appliances detected from the main
  meter that have **no plug** (aqua; plug-confirmed signatures are already in the plugs), **„Standby / mereu pornit”**
  and **„Nedetectat”** (dashed). The parts always add up to max(meter, Σ plugs): standby is at most the rest now,
  the detected appliances are scaled down together when they would exceed what is left, and the undetected rest is
  what remains (≥ 0). The bubble positions are recomputed only when the set or the order changes (with a 10 % + 2 W
  hysteresis) or a value outgrows its slot, so they do not jump every second. A plug drawing ≥ 20 W, a running Miele
  appliance's plug and a running detected appliance pulse. A name that does not fit goes on two lines; a bubble under
  22 px gets its label outside. The list below gives W and % of the meter (the 8 largest; the others in the tooltip).
  Text on a coloured fill keeps a contrast of at least 4.5:1 (white on a darker blue in the light theme, dark text on blue,
  orange and aqua otherwise).
- **Standby / mereu pornit** = the night floor of the **rest** of the house: the median of (main − Σ plugs) per minute
  between 01:00 and 05:00 local time over the last 7 nights (fewer while the history is shorter), from the live minute
  aggregates. Every plug that counts in the totals is subtracted: a streamed plug by its minutes (a minute counts only when
  every streamed plug has it, a night only when each covers at least half of it), a plug without live stream by its mean
  polled power of that night. A plug's own standby stays in the plug (no double counting). Recomputed hourly;
  `kasa_house_standby_w`. A floor of **5 W or less** is not measurable (at night the sum of the plugs exceeds the
  meter): no bubble, no row, the note „Standby nemăsurabil: noaptea, suma prizelor depășește contorul.”, and the metric
  is 0.
- **Energie** of the period: electricity (kWh, lei at the tariff of `/settings/aplicatie`), gas (m³, ≈ kWh with the calorific
  factor, lei at the gas price per kWh) and the total, a bar split by source in kWh. For **today** the comparison is with
  **yesterday up to the same time**, both as Σ of the main meter's live minutes (same source); when either window is
  covered under 90 % by the stream it says „date parțiale” instead of a Δ. A week / month / year **in progress** is
  compared with the **same share of the previous one** (up to the same day and time); a past period with the whole
  previous one; a missing previous value says „fără date pentru …”. Gas follows the **boiler's day** (Central European
  time, ends at 01:00 Romanian time), as on `/centrala`. When a week / month / year started before the main meter's first
  day, the cards say „⏳ date parțiale: contor din <data>; nedetectat calculat doar pe zilele acoperite”, and the plugs
  and „Nedetectat” are counted only on the days the meter covers (the same rule as /history).
- **Pe ore / Pe zile / Pe luni**: the main meter's energy per bucket, the previous period pale behind it, the **typical
  day** (mean kWh per local hour over the last 30 days, hours with at least 54 live minutes; day periods) and „acum”.
- **Aparate**: energy and cost of every plug, every detected appliance without a plug and „Nedetectat”
  (= meter − plugs − detected, ≥ 0), largest first, with a short note when it fits; a click opens the plug's history or
  `/consumatori`. Each card title has a „→” to its source page.
- **Unde s-a dus energia**: a Sankey from the meter to the top 5 plugs + „Alte N prize”, the top 3 detected appliances
  + „Alți N detectați” and „Nedetectat” (band width ∝ energy, the flows add up to the meter); gas on its own scale to
  heating / hot water. Detected appliances under 0.01 kWh are counted in „Nedetectat”; the members of „Alte N prize” are
  written next to it when they fit. When the plugs exceed the meter the note says why, as /history does: within the
  margin, partial data, or the clamp. Under 900 px the electricity flows are a vertical list with bars; under 1500 px the
  gas flow goes under the electricity one.
- **Azi vs ieri** (always today, at the same time), **Luna în curs vs luna trecută** (the previous month up to the same
  point, and whole) with the **ESTIMARE** of the bill: used so far + (used so far / elapsed days) × days left, electricity
  and gas, marked „încredere scăzută” under 7 days; `kasa_bill_estimate_ron{kind="electricity|gas|total"}`.
- **Centrala** (burner with its purpose, supply, hot water, modulation, today's gas per purpose), **Aparate Miele**
  (the running program with phase, time left and progress, else the last cycle) and **Climă** (temperatures per room
  and outside, from the [climate service](#climate): „Afară” = the sensors ticked „afară” (Open-Meteo by
  default), the rooms = the other sensors except the boiler's supply and hot water; a stale value is shown grey). The
  boiler's values older than 2 poll intervals (`HeatingState.StaleAfterPolls`) or with the boiler offline, and a Miele
  appliance with the stream down and no read in the last 3 minutes (`MieleState.IsOnline`), are shown grey as „ultima
  stare cunoscută”, never as present — each card is left out when its source is missing. Before the first 6 hours of a
  month the estimate says „Estimarea facturii apare după primele 6 ore ale lunii.”
- Footer: the age of the main meter's latest value, links to the pages and to Grafana (setting „Grafana”).

Performance: every period is computed on the thread pool (never on a browser's circuit; a rule from the SQLite days, when
the "async" queries ran synchronously, kept with SQL Server) and shared by all browsers — concurrent requests wait for the same computation. A value is fresh for the
current day 1 minute, a week / month / year in progress 10 minutes, a past period until the day changes; an expired
value is still served while one recomputation runs (stale-while-revalidate); at most 32 periods are kept. The current day
and month are kept warm in the background; each computation is logged at Debug with its duration (category
`KasaMonitor.Workers.HomeService`, e.g. `Logging__LogLevel__KasaMonitor.Workers.HomeService=Debug`); one house report per
period gives both its bars and the previous period's; periods come from the hourly live sums, the daily rows and the
boiler's daily history, never from the raw `LiveSamples`. Only „Acum în casă” and the footer refresh every second.

**Theme.** Light or dark for the whole application: the button in the menu bar switches it (it says which theme is on,
`aria-pressed`) and the choice is kept in the browser (`localStorage`); without a choice the system setting
(`prefers-color-scheme`) decides, followed live. The rule has one owner, the inline script in `App.razor` (it sets
`data-theme` before the first paint); the colours are CSS variables on `:root`, with one dark block
(`[data-theme="dark"]`): electricity / plugs blue, gas orange, detected appliances aqua, standby and „off” grey.
Bootstrap follows through `data-bs-theme`; its `table-light`, `text-bg-light` and `bg-light` and the ApexCharts texts,
grid and tooltips follow the theme too.

## History (`/history` „Istoric”)

One long page for the past of everything KasaMonitor monitors, on the home page's look (cards, palette, light/dark).

**One period for the whole page**: **Zi** (bars per hour), **Săptămână** (Monday–Sunday, per day), **Lună** (per day),
**An** (per month) or **Interval** (de la – până la, whole local days, per day, at most 732 days); ◀ ▶ move by one period
(an interval by its own length), never past today. The period, the comparison and a plug filter live in the
address, so every link opens exactly that view: `/history?p=luna&d=2026-11`, `?p=an&d=2026`, `?p=saptamana&d=2026-10-26`,
`?p=zi&d=2026-11-02`, `?p=interval&de=2026-10-01&pana=2026-10-20`, plus `&cmp=0` (no comparison) or `&cmp=an` (the same
interval last year, 0.14.0), `&plug=<id>`, `&pw=<id>,<id>` (the series of „Putere”; `pw=-` none; absent: the main meter)
and a section anchor (`#curent`, `#putere`, `#gaz`, `#aparate`, `#consumatori`, `#clima`, `#costuri`, `#evenimente`). Without `p` the page
opens on the current month; the old `?plugId=` still works. The home page's „→” links, the plug links of /prize,
/dispozitive, the sub-meters card and the Sankey, and /centrala's „Istoric →” carry their period. Days are local days
(`TZ`) everywhere; gas follows the **boiler's day** (`BoilerTimeZone`, as on /centrala since 0.8.1).

**Comparison**: one control for the page (since 0.14.0 a list instead of the switch): „Fără comparație”, the previous
period („Compară cu luna precedentă”, today: „Compară cu ieri (la aceeași oră)”) or „Același interval anul trecut”. The
last one is drawn only by „Putere”; the other sections keep comparing with the previous period. As on the home page, a
period in progress is compared with the **same share** of the previous one
(`SameFraction`: whole bars plus the elapsed part of the current one; today = yesterday up to the same time, from the
live stream); a past period with the whole previous one. Since 0.9.2 the same rule is in `HouseReportBuilder` and
`HeatingReportBuilder`, so /centrala „Rapoarte” shows „față de aceeași parte din …” too (before: about −90 % on the 2nd
of a month, against the whole previous month).

**Summary row** (anchors to the sections): electricity kWh / lei, gas m³ / lei, total cost, burner hours and starts,
Miele cycles, mean temperature indoors / outdoors, each with its Δ.

**Sections** — each has its chart, key figures with Δ, a table (the same rows as its **CSV**; rows stop at now) and
drill-down (a bar or a table row of a week / month / interval opens that day, of a year that month):
- **Curent**: the main meter split into the counted plugs (stacked, one colour per plug, the 7 largest + „Alte N prize”)
  and „Restul casei”, the previous period pale behind; a plug filter keeps one plug next to the meter line; top
  appliances (plugs, detected without a plug, „Nedetectat”, as on the home page; „Nedetectat” only on the days the meter
  covers). Data: `HomeService` (shared with the home page, cached).
- **Putere** (0.14.0): the power over time of the main meter and of any plugs or sub-meters, toggled like the lines of
  `/contor` (same palette; main meter on and plugs off by default; kept in the address as `pw=`). One point per
  **minute** for periods up to 2 days, per **hour** up to 45 days, per **local day** beyond (`PowerHistory.ResolutionFor`);
  each point is the average (the line) and the maximum (a light band up to it); a bucket without data is a gap, never a
  zero. Read only from the aggregates — `OneMinuteAggregates` for minute points, `OneHourAggregates` for the others
  (days are computed from the hours) — never from `Samples` or `LiveSamples`, so the chart of a past period stays when
  the raw readings are trimmed. The comparison is one dashed line, of the main meter (or of the first series shown when
  the meter is off), aligned on the period's offset (hour of the day, day of the period, by wall clock: on the 23 h and
  25 h DST days 04:00 meets 04:00; a day without a counterpart, the 31st against a 30-day month, stays empty). „Același
  interval anul trecut”: a day → the same date (29 February → 28 February), a week → the week 52 weeks earlier (the same
  weekdays: 2–8 November 2026 → 3–9 November 2025), a month → the same month, a year → the year before, an interval →
  the same dates. Key figures for that series: average W and maximum W (with its minute, or its hour from the hour
  rows), the comparison's maximum, and the Δ of both (a period in progress against the same part of the comparison).
  The aggregates of today are completed every 15 minutes.
- **Gaz și centrală**: heating vs hot water m³ per bar, ≈ kWh, lei, apparent efficiency (heat / gas kWh), burner hours
  and starts per bar (the cumulative counters of `HeatingReadings`, grouped per hour in SQL), the mean gas per hour of
  the day and the **burner timeline per day** (runs on a 24 h track coloured by purpose, `BurnerTimeline`) for periods
  up to 31 days. /centrala „Rapoarte” stays and links here.
- **Aparate (Miele)**: the cycles of the period (start, appliance, program, duration, Miele's energy vs the plug's, water,
  lei — the plug's energy when it measured, else Miele's) and per program: cycles, mean duration / energy / water, totals
  and the spread (one dot per cycle).
- **Consumatori**: where the meter's energy went by how well it is known: every **identified** consumer on its own row
  (a detected signature named by the user, a single-appliance plug under its appliance's name, or a plug confirmed by a
  signature), the other plugs, all the anonymous
  signatures grouped as „Neidentificați (n semnături)” (opens into the list, links to /consumatori) and „Nedetectat”
  (dotted); stacked per bar; „identificat N %” = identified / **meter** (Σ of its bars), the progress of the learning. On a
  bar where Σ plugs exceeds the meter, the plugs are reduced to the meter and the difference is shown apart as „Peste
  contor (toleranța clemei / date parțiale)”, never in the share. The detected appliances are scaled bar by bar when they
  exceed the meter minus the plugs — one rule (`EnergyFlowRule.ScalePerBucket`) used by the home page as well, so its
  „Aparate” and Sankey give the same numbers. Below: energy, runs, mean duration and the
  start hours (hour × appliance) of the detected appliances. The same summary for the last 7 and 30 days is at the top
  of `/consumatori`.
- **Climă**: per sensor min / mean / max temperature and humidity per bar (hours of a day from the raw readings, days
  from `IClimateDailyStats`, months of a year from the days), outdoor dashed vs rooms; **heating degree days** per bar and
  in total (outdoor = the sensor ticked „afară”; `HeatingDegreeDaysBaseC`, default 18 °C, Eurostat method with the
  threshold base − 3 °C; finished days only, today's value so far is shown as „parțial” and not added) and the **energy
  signature**: heating gas kWh/day (boiler days, whole days) against x = max(0, base − Tmean) **without** the Eurostat
  threshold (between the threshold and the base the boiler still heats), with the least-squares line, kWh per degree
  and R², shown from **7 days** with both values (otherwise „Insuficiente date, de la <prima zi>”).
  A period shorter than 7 days uses the 30 days ending with it.
- **Costuri**: electricity and gas lei per bar and in total (the total only when both parts exist), the prices used
  („la tarifele actuale”: a price change recalculates the past) and, for the current month, the bill estimate of the
  home page.
- **Evenimente**: relay commands, boiler errors (`ActiveError`) and gaps of more than 15 minutes in its readings, Miele
  appliances disconnected (status 255 or signal failure), newest first, filtered per source (at most 500 per source).
  Application restarts are not stored (container log only); alerts stay in Grafana.

**CSV**: `GET /history/export.csv?section=<s>&p=…&d=…` with `s` = `curent`, `putere` (with `cmp=` and `pw=`: one row per
bucket and series — average, maximum and its time, minutes with data, and the compared series' aligned values), `gaz`,
`aparate`, `aparate-programe`, `consumatori`, `consumatori-detectati`, `clima`, `clima-grade-zile`, `costuri`, `evenimente`: `;` between fields,
decimal comma, UTF-8 with BOM (Excel / LibreOffice in Romanian), the same rows as the page's table. The time column is
ISO (`yyyy-MM-dd HH:00`, `yyyy-MM-dd`, `yyyy-MM`), so a spreadsheet sorts it as a date; markers (rest within the margin,
partial data, the bar in progress) are in a separate „Notă” column, so every number stays a number.

**Performance**: only aggregates and bounded raw reads — never `LiveSamples`. Electricity from `HomeService`; the other
sections through `HistoryService`, with the same cache as the home page (`PeriodCache`: one computation shared by every
circuit and the CSV, a period in progress 10 minutes, a past one until the day changes, stale-while-revalidate). Each
computation is logged at Debug („History {Section} of {Kind} {Start} computed in {Ms} ms”). Climate days come from the
table **`ClimateDaily`** (per sensor and local day: min / time-weighted mean / max temperature and humidity, number of
readings; migration `AddClimateDaily`), filled by `ClimateDailyWorker` at startup for every missing finished day (from the
sensor's first reading, in chunks of 31 days) and again 10 minutes after each local midnight (at least hourly); every fill
also recomputes the last 3 finished days, so readings stored late (the boiler's source catching up) reach their day; today and any day not stored yet
are computed from the raw readings (`ClimateDailyStore` behind `IClimateDailyStats`). The degree days and the energy
signature use these days. Measured on a PC: a year of raw readings (3 sensors, one every 10 min, 136 000 rows) took
≈ 290 ms; from `ClimateDaily` a few milliseconds.

„Putere” reads one series of one period on the key (PlugId, BucketStartUtc) of its aggregate table and caches it
(`PowerHistoryService`, its own `PeriodCache`); toggling a series reads only that series. A year of hour rows for three
plugs (26,280 rows) is read in 36 ms on SQLite and 80 ms on SQL Server 2022 in Docker (the repository test, on a PC).

**Retention** (0.9.2): `ClimateReadings`, `MeterEvents` and `ApplianceRuns` are kept without limit; `LiveSamples` keep
`LiveRawRetentionDays` (default 30). The minute and hour power aggregates are kept without limit and never rebuilt from
less data (0.14.0): with `ReadingsRetentionMonths = 3` the raw readings go after three months, the chart of every past
period stays.

### Report sources (house and boiler)

The house report (`HouseReportBuilder`) per day is the larger of `DailyEnergy` (Shelly meters: `EM1Data` from the
device's memory, read every 6 h for up to 60 days back; Kasa/Tapo: their own day counters) and the Σ of KasaMonitor's
live minute aggregates of that day; hours („Zi”) only from the live stream (`LiveMinuteAggregates`, grouped per channel
and UTC hour in SQL); a year per month the larger of the device month total (`MonthlyEnergy`) and Σ of the stored days.
„Restul casei” = main meter − plugs, never below 0 (see *Rest of the house* above).

**Centrală „Rapoarte”** (below the current state): tabs **Gaz** (m³, heating orange-red vs hot water blue),
**Curent centrală** (kWh, `heating.power.consumption.summary.*`) and **Căldură produsă** (kWh). Under each: total, Δ %
against the previous period, cost (gas: m³ × kWh/m³ × RON/kWh from the boiler settings; electricity: the plug tariff)
and the apparent efficiency of the period (heat / (gas × kWh/m³)). The gas price is per kWh only (no per-m³ price
setting). The Δ of the period in progress compares it with the same share of the previous one („față de aceeași parte din …”,
0.9.2), and the card links to `/history` for the same period („Istoric →”). Sources (also in the „ⓘ” tooltip):
- days: `HeatingEnergyDaily`, built by KasaMonitor from the boiler's „currentDay” (it starts with the first reading;
  earlier days are empty, with the note „fără date înainte de …”);
- hours („Zi”): the increases of the boiler's „today” counters between consecutive readings (~2 min). Since 0.8.0 every
  row of `HeatingReadings` stores them (`GasHeatingTodayM3`, `GasDhwTodayM3`, `PowerTodayKwh`, `HeatTodayKwh`; migration
  `AddHeatingReadingCounters`). A drop is a reset by `BoilerClock.IsReset` (boiler time 23:45–00:30, or under 20 % of
  the previous value with no reset in the 20 hours before; the new value is then the use since the reset); any other
  drop is ignored (the higher value stays the reference) and logged at Warning once per counter and boiler day. A rise
  in the first 30 minutes of a boiler day before its reset was seen is yesterday's total read late and adds nothing. A
  boiler day's hours run 01:00–01:00 Romanian time and are labelled in local time.
  Electricity and heat per hour are totals (heating + hot water);
- month: the stored days, with the boiler's own `currentMonth` as a check (both shown when they differ by more than 5 %);
  the previous month falls back to the boiler's `lastMonth` total when its days are missing;
- year: the months from stored days, plus one bar „restul anului” = the boiler's `currentYear` minus those months;
- the boiler's relative values are mapped to calendar periods by the boiler day they were read on (`last7days` is a
  rolling window and is never used as a week).
The „Centrală” card of the „Prize” page shows today's gas with the change against yesterday **up to the same time** (from the
stored „today” counters); without counters for yesterday it shows only yesterday's total.

## Appliance detection (NILM)

KasaMonitor recognises appliances in the power of the whole house (non-intrusive load monitoring) with a
deterministic, explainable pipeline, and uses the plugs it already measures as automatic labels. It needs a **main
meter** (`/dispozitive`) with the live stream on (1 s). Page `/consumatori`, menu „Consumatori”.

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
   restart) and is not stored again. **Unknown transient** (since 0.8.2): the transient is known when the step reached
   its new level within one sample (rise 0 s) or the ratio is above 1.5, and unknown only when the step took 1 s or
   more **and** the ratio is ≤ 1.5 (see „How it learns”); the stored ratio stays as measured, but an unknown one is never
   compared (clustering, matching, merging) and never enters a signature's mean transient.
2. **Backfill**: at start, the stored `LiveSamples` of the main meter after the marker `NilmLastProcessedTs` (Settings
   table; the first time: all of them, i.e. `LiveRawRetentionDays`) go through the detector in 6-hour batches (the
   plugs' readings of a batch are loaded once); a backfill of more than 6 hours is re-clustered right away. The page
   stays usable meanwhile (it reads its own snapshot and shows „Procesare în curs”).
3. **Plug labels**: 45 s after an event, the plugs' 10-second readings around it (kept in memory for the last
   3 minutes; loaded per batch during a backfill) are compared on **stabilised levels** (since 0.8.2). A plug's readings
   are those of its live stream (≈ 3 s, `LiveSamples`) **held on a 1-second grid** — at every second the value of the
   last row, at most 60 s old; the stream stores a row only on a change, so a stable level counts every second and a
   ramp only for the seconds it lasted, the same live and in a backfill — when that covers both windows, otherwise its
   10-second polls. The plug's level before = the median of its readings in [t − 20 s, t − 3 s], after = the median in
   [t + 3 s, t + 20 s] (at least one reading in each; the last one before and the first one after at most 25 s apart);
   both windows stop 3 s before the previous / next event of the main meter that is large enough to move the plug beyond
   the tolerance (|ΔP| ≥ max(10 W, 15 % of this event's |ΔP|): a washing machine's switch-off 16 s after its switch-on;
   a UPS's 30 W does not shorten the windows of a 2 kW step). Readings within ±3 s of the event
   are ignored, so a reading taken in the middle of a ramp or an inrush (a washing machine read at 171 W or 827 W on its
   way to 2 kW) no longer hides the step. A plug labels the event (source `auto-plug`) when its step is ΔP ± max(10 W,
   15 %), it is the **only** plug whose level moved in the same direction by ≥ 60 % of |ΔP| (counting every plug, also
   those excluded from learning: a UPS that moved at the same time makes the event ambiguous), and that plug step has
   not labelled another event. Events detected late (after a long backfill or nightly job) read the plugs'
   readings from `Samples`. A plug that matches **more than 20 events in an hour** (a UPS, a PC: a variable load) is
   excluded from learning automatically (`Plugs.NilmIgnored`; badge on `/dispozitive` and `/consumatori`, with a button to
   undo it — an undone plug is never excluded automatically again; a plug can also be excluded by hand). Excluding
   only stops new labels and training: the plug's labels no longer count for linking new signatures or for the
   classifier, but signatures already linked to it stay „measured by the plug”.
4. **Signatures** (`ApplianceSignatures`): DBSCAN (own implementation) over |ΔP| (relative, i.e. log scale), ΔQ seen
   from the switch-on side and the transient ratio of switch-ons; eps(|ΔP|) = 8 % of |ΔP| + 3 W, eps(ΔQ) = 8 % of |ΔP|
   + 15 VAR, minPts 3; a cluster wider than (1 + 2 × 8 %) × its smallest |ΔP| + 6 W is cut into narrower bands (a dense
   band of small loads would otherwise chain into one cluster). Each point enters the expansion queue once (memory
   O(n)). Switch-ons and switch-offs of the same magnitude join the same signature. A new event goes first to the
   nearest **anchor** (a signature named by you or linked to a plug, not hidden) within **1.5 × eps** (≈ 12 % of |ΔP|)
   — an event labelled by a plug first to an anchor linked to that plug, never to one linked to another plug —,
   otherwise to the nearest other signature within eps, otherwise to a new „Necunoscut #n”. Every night at 03:30 (and at startup
   when the last run is older than 24 hours or never ran, and on „Regrupează acum”) the **newest 20,000 events** of the
   last `ClusterWindowDays` (30) are clustered again on a low-priority thread, outside the processing lock: events you
   labelled yourself, and events labelled by the plug their signature is linked to, never move; every other event goes
   through the **anchors** again (it stays in a labelled signature only while within 1.5 × eps of it, so a signature
   cannot ratchet away by keeping everything it once caught);
   DBSCAN groups the rest (clusters keep the anonymous signature most of their events had; model labels are
   re-evaluated; empty automatic signatures are deleted); then signatures that are the same component are **merged
   automatically** (see „How it learns” below). The time of the last run is the setting `NilmLastReclusterTs`. Counts
   and means are SQL aggregates over the window.
5. **Measured by a plug**: when at least 3 events and ≥ 60 % of a signature's events were labelled by the same (not
   excluded) plug, the signature is linked to that plug (below 3 it stays „Necunoscut #n”, marked „posibil <plug>” on
   the page; `ApplianceSignatures.PlugId`; it takes the plug's name unless you named it).
   The link is by `PlugId`, not by name: renaming does not change it. These signatures are listed separately on
   `/consumatori` („Măsurate de prize”): the exact consumption is the plug's own (its card, `kasa_*`), so they are not
   in „Acum pornite”, not in the detected energy, not in `kasa_appliance_*`, and never subtracted twice from the rest
   of the house.
6. **Your labels** (`/consumatori`): rename a signature, choose its category (fridge, boiler, hob/oven, washing
   machine, AC/heat pump, lighting, electronics, other), hide it, merge one into another (one selector above the
   table), split it (re-clustered alone with half the eps), open its **„fișa”** (the card button: ΔP mean ± standard
   deviation, ΔQ, the step's power factor, the transient or „necunoscut, n din m”, switch-ons/offs, the typical run
   length — median and p10–p90 —, a 24-bar histogram of the hours of the day, the plug it matches and in what share,
   first/last seen, energy over 7 days; the same data points drive the automatic merge; and, for information only, over
   the last 30 days „Durată tipică”, „Se repetă la” — between switch-ons, pauses over 6 h left out — and „Într-o sesiune”
   — runs at most 120 s apart: pulses and energy per session), the duration band (see **Duration bands** below), and from the „fișa” „Unește
   cu…” (the survivor is chosen as in the automatic merge; a confirmation says which one stays and which one goes) or
   „Adaugă la aparatul…” (to a signature you named or one linked to a plug, not hidden; with a confirmation): when the
   two centres are within 1.5 × eps (the same component) the events move into the chosen one; otherwise (a washing
   machine's motor added to its heater) the signature only takes the chosen one's name and category and stays a separate
   row — a signature is one component, mixing two would match neither. „Regrupează acum” (with a confirmation) runs the
   nightly job on request and reports how many signatures were merged. You can also label any event of the last 24 h („a fost boilerul”;
   Enter or the button): the event moves to the signature with that name if there is one; otherwise its own signature
   takes the name when it is an automatic one, else a new signature is created. Names are trimmed to 64 characters.
   Signatures you named or merged are kept as they are by the nightly re-clustering. A hidden signature is left out of
   the cards and the metrics (its energy stays „unidentified”). Lists are paged by 50. The list of detected signatures
   shows by default only those with **at least 3 events, a name of their own or a „posibil <plug>” badge**, the newest
   last event („Ultima”) first; „arată și semnăturile rare (n)” shows all of them. Every column header sorts the whole
   list before paging (a second click reverses it, ▲/▼ on the active column; rows without a last event or a known
   transient stay last; the choice is kept in the address, `?sort=last&dir=desc`, and a new order starts again from
   page 1); „Măsurate de prize” is ordered newest first too. Mean ΔP and the
   number of events are the first two columns. „Curăță semnăturile rare” (with a confirmation) deletes the signatures
   with at most 2 events whose last event is older than 7 days (never one with a name, a „posibil <plug>” badge or
   measured by a plug); their
   runs go with them, their events stay, without a signature, and the nightly re-clustering may group them again.
7. **ML model** (`MlEnabled`): with ≥ 20 events labelled by plugs or by you in ≥ 2 classes, a multiclass ML.NET
   classifier (`SdcaMaximumEntropy`; features ΔP, log |ΔP|, ΔQ, transient, rise, hour of day as sin/cos, day of week)
   is trained every night on the newest 20,000 labelled events (low-priority thread), also at startup when no model is
   saved yet, after every re-clustering („Regrupează acum” included: the labels may have moved), and on „Antrenează
   acum” on `/consumatori` (the result shows the classes, the cross-validated accuracy and the duration; while the
   condition is met but no model exists the page says so); its accuracy is measured by
   5-fold cross-validation and shown on the page with a confusion matrix — **measured only on the appliances that have
   a plug or that you labelled**, not on the whole house. A new event without a plug label gets the prediction only
   when the probability is ≥ 0.7 **and** the event is within 2 × eps of that class's centre (an open-set check: an
   appliance no plug has seen is otherwise forced into the nearest known class); it then joins the signature with that
   name (never one of a single-appliance plug), otherwise it stays in its DBSCAN signature. Saved as `<data dir>/nilm-model.zip`. ML.NET adds ≈ 5.5 MB to the
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
   „Prize” header; Shelly plugs have none and are not subtracted) is scaled to the same covered share of the day.
9. **Cards** „Acum pornite” (detected appliances running now, with W) and „Azi” (top 5 + unidentified) on the
   „Prize” and `/contor`; the rare signatures (those the list hides by default) are not named one by one there but
   summed into one row „Semnături rare (n)” (W, Wh; a link to `/consumatori`), so every name on the cards is in the
   default list, and a run open longer than 6 hours is never shown as running; metrics `kasa_appliance_*`,
   `kasa_nilm_*` (see Metrics; per signature, unchanged).
10. **Retention**: none since 0.9.2 — events and runs are kept without limit (the history of `/history`; before: 365 days).

**Duration bands** (since 0.13.0). Two appliances with the same step (an espresso machine's pulses of ≈ 1.43 kW for
10–20 s, a kettle's ≈ 1.45 kW for 1–7 minutes) differ only by how long a run lasts. A signature can have a band, minimum
and maximum seconds, inclusive (the „fișa” or `POST /api/nilm/signatures/{id}/duration-band`); it is set only by you,
never inferred. When a run closes with its own switch-off and its duration is out of the band of its signature, it moves
to the **only** other signature, not hidden, whose band holds the duration, with no plug or the same plug, and whose
centre is within 1.5 × eps of the switch-on's |ΔP| and ΔQ (or the first signature's centre, when one was split from
the other: the two can have the same centre). Run, both events and energy per day move; the events take the source
„după durată” and the re-clustering no longer moves them (the centres include them). None or several candidates: it stays. An
event you labelled holds its whole run in place; a run closed by the house level or by the 6-hour limit is never moved. Two
signatures with disjoint bands are never merged (touching at the limit counts as disjoint; uniting the two sides of a split
by hand undoes it, and the result has no band), and the moved events do not train the model. `POST /api/nilm/signatures/{id}/split-by-duration` separates what already stands in a signature: a dry
run unless told otherwise, it shows both groups of durations and the energy that would move, then moves the paired runs up
to the limit, of every stored day, to the signature with that name (a compatible one, or a new one), and sets both bands.
A side split off a signature measured by a plug stays measured by that plug, so its energy is never subtracted twice.
New events near the two sides of a split wait in the signature that was split, so a pulse still running shows under that name
(„Acum pornite”, `kasa_appliance_power_w`) until it closes and moves. For two appliances with the same centre use the split,
not two bands set by hand: without it a switch-on and its switch-off can land in different signatures and the run never closes as paired.
Overlapping runs (kettle and espresso machine at once) pair first in, first out, so their durations are not reliable.

**How it learns.** A signature is a **component**, not necessarily a whole appliance: a washing machine is a heater
(≈ 2 kW), a drum motor (≈ 250–300 W pulses) and a pump; a kettle is one heater. Your labels come first: a signature
you named or that a plug confirms is an **anchor**, tried before the anonymous groups with a wider tolerance (1.5 ×
eps), so a kettle running at −6 % (lower voltage while the washer heats) still lands on „Fierbător” instead of a nearer
anonymous group, and the nightly re-clustering never takes a name away. The transient ratio (peak in the first 2 s /
ΔP) is used only when it was really measured: **known when the step took no time (rise 0 s) or the ratio is above 1.5,
unknown only when the step took ≥ 1 s and the ratio is ≤ 1.5**. A step that falls between two samples, or a transition
started by a small wobble before the real step, can only make the measured peak smaller (≈ 0–1); a peak above 1.5 can
only be real (a motor's inrush, whose settling also makes the rise last 2 s or more). Several signatures that turn out to be the
same component are **merged automatically** (nightly, at startup after a missed night, „Regrupează acum”). Never
merged: two linked to different plugs (checked first), two with disjoint duration bands, two you named differently, hidden ones. Otherwise when (c) you
gave them the same name and their centres are within 1.5 × eps (the same name on distant components stays two rows),
(a) their centres are within eps (|ΔP|, ΔQ, and the transient only when both know it), (b) both are linked to the same
plug, or clearly „posibil” it (≥ 2 events and ≥ 30 % of them from that plug), and their |ΔP| differ by less than 15 %,
or (d) they are the same **inductive** component with a variable load (a drum motor's pulses): neither is an anchor,
both have |ΔQ/ΔP| ≥ 0.5 and |ΔP| ≥ 150 W, their ΔQ/ΔP differ by at most 0.15 with the same sign, their |ΔP| by at most
25 %, they are not „posibil” two different plugs, and at least 60 % of the smaller one's events have an event of the
other within ±10 minutes, at least twice as often as with those times shifted by ±1 hour (a dense small load is
„together” with everything by chance). A merge with a signature linked to a plug is made only when that plug would
still have labelled ≥ 60 % of the merged events (the merge never unlinks it). The survivor is the one you named,
else the one linked to a plug, else the one with more events (then the older id); the absorbed signature's events and
runs move to it (also the runs older than the window), it takes the absorbed one's plug and category when it has none,
the absorbed signature is deleted (its `kasa_appliance_*` series disappears, the survivor's continues). Each merge is
logged at Information. Measured on the real house of 2 Nov 2026: the drum motor's pulses (≈ 210–310 W, cut by DBSCAN
into 215 / 267 / 304 W) end in 2 signatures (284 W with 56 of the 60 events, and 215 W); the small loads (59–117 W)
stay separate.

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
such as a washing machine appear as several signatures). The plug labels need the plug's level to change around the
house step (medians of the 20 s before and after, ±3 s ignored, cut at large neighbouring events), so a plug offline,
two plugs moving together, or two large steps less than ~7 s apart give no label. Measured on the washing machine of
2 Nov 2026: 12 of its 13 heater steps labelled with the plug's stream, 10 with the 10-second polls only (7 in 0.8.1).

### Single-appliance plugs

Since 0.9.1 a plug can be marked „un singur aparat pe priză” on `/dispozitive` (the plug's row, for a plug with
the role *plug*), with the appliance's name (empty = the plug's alias, e.g. „Frigider”) and category (`fridge`,
`hvac`, …), or with `POST /api/nilm/plugs/{id}/appliance` ([HTTP API (LAN)](#http-api-lan)). Nothing is set
automatically on upgrade (columns `Plugs.SingleAppliance`, `ApplianceName`, `ApplianceCategory`, migration
`AddPlugAppliance`, default off). For such a plug:

- **Labels at once.** A main-meter event confirmed by the plug (the plug's level moved by the same ΔP) gets the
  appliance's name as its label (source *din priză*) and goes to the signature linked to that plug within the anchor
  radius (1.5 × eps), or to a new one linked to the plug right away with the appliance's name and category (*confirmat
  din priză*): no 60 % / 3 events threshold. A signature linked to it stays linked while any of its events in the window
  comes from the plug; the signatures already linked take the name and category as soon as the flag is set (and the
  alias again when it is cleared). Its events are pinned at the nightly re-clustering like every plug-confirmed one.
- **Only what the plug confirmed** (since 0.13.1). Such a signature holds only events that plug confirmed and events you put
  there by hand. An event near its centre that the plug did not confirm goes to another signature or to „Necunoscut”, and so
  does a prediction of the model; the automatic merge joins it only with another signature of the same plug. The next
  regrouping removes what got in before: centre and count are rebuilt from what stays, and an emptied signature stays as
  the plug's until its next confirmation. A merge by hand is still possible (the events you bring in take your label with the
  appliance's name). Pulses under ≈ 15 s, which a plug cannot label, leave the signature, and the number of events in
  „Necunoscut” goes up: both are expected.
- **Runs from the plug** (since 0.13.2). The runs of such a plug come from its own power, never from events: it is on from 20 W, a
  change must still hold after 5 s, the power of a run is its median level and its energy the integral of the plug. They stand on one
  signature of the plug (most confirmations, then the lowest id; a new one when it has none), so a merge doubles nothing. If the data stop for
  more than 70 s, a running run closes as uncertain with the energy measured so far. A split by duration of such a signature finds nothing to
  move and a band does not act on it. On `/consumatori` its row in „Măsurate de prize” shows the switch-ons, the mean level, the duration and the
  repetition measured by the plug, over the same window (a fridge with 84 transitions a day shows about 42 switch-ons a day); its sheet shows
  the runs and its list of events what was confirmed. `GET /api/nilm/signatures` keeps `count` as the events it holds.
- **Shadows** (since 0.13.2). A main-meter event without a confirmation that is the only candidate of a transition of the plug (the same
  sign, |ΔP − the plug's step| ≤ 15 W, at most 5 s before it) is the plug's step seen again: it gets the source *umbră a prizei* (`PlugShadow`
  in `GET /api/nilm/events`), no label and no signature, so it forms no run and is not subtracted from the rest of the house. It is no
  confirmation and never trains the model. The regrouping does not move it and calculates the shadows again where the plug has data. You
  can label it like any event. Limit: a small foreign step that happens to be the only candidate leaves „detected” with it (about 15 %
  of the shadows, estimated). A step that is neither confirmed nor a shadow still counts as detected and is subtracted as well.
- **Out of the ML model.** Its events are explained completely by the plug, so they are neither examples nor a class
  of the classifier (the training set leaves out the labels of the excluded plugs and of the single-appliance ones).
  The plug is **not** excluded from learning (`NilmIgnored` is untouched): it still labels and links its events, so they
  never become „Necunoscut”; it is also never excluded automatically for matching too many events per hour. Since 0.13.1 it
  labels and links even when it was excluded from learning by hand: that state speaks only of the training (the value stays
  stored, `GET /api/nilm/plugs` shows it next to `labelsEvents`). Changing the flag retrains the model on the next pass when ML is on.
- **Where the name appears.** The plug keeps its alias (`/dispozitive`, history, `kasa_*` metrics); the appliance's name is
  shown where the plug stands for an appliance: the bubble and the row of the home page, the Sankey, `/consumatori`
  (its signatures, in „Măsurate de prize” with the badge *priză cu un singur aparat*, never among the unknown ones),
  and `kasa_appliance_plug_power_w`.
- **Limit.** An inverter appliance (an AC) has no steady steps: few of its changes become events, and those that do may
  give several signatures linked to the plug (one per component); the plug's own measurement (`kasa_*`, history) stays
  the reference for its energy.

## HTTP API (LAN)

Since 0.8.3 the appliance detection can be operated without the UI, with `curl` from the LAN: `/api/nilm/*`, JSON.
Every route calls the same method as the matching button of `/consumatori` (same merge routine, same survivor rule,
same „Adaugă la aparatul…” semantics); nothing in the API decides on its own.

**Security.** Like the relay switch, the API has no password and is meant for the LAN only: the `POST` routes
(changes) are accepted only from private addresses (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, loopback),
judged on the address of the TCP connection (never on `X-Forwarded-For`); any other address gets `403`. The `GET`
routes are read-only. Every `POST` is logged at Information with what was done and the caller's address (category
`KasaMonitor.Web.Api.NilmApi`); a refused one at Warning. **If KasaMonitor is ever reachable from outside the LAN**
(port forward, reverse proxy, VPN without filtering), set a token: `KasaMonitor__NilmApiToken=<long random value>`.
With a token every `/api/nilm/*` route (also `GET`) needs the header `X-Api-Key: <token>` (otherwise `401`), and the
`POST` routes still need a private address (behind a reverse proxy that address is the proxy's, so the token is
what protects them).

Answers: `200` with JSON; `400` for a bad request (`{"error": "…"}` in Romanian), `404` when a signature (or plug) id does not
exist, `403` from outside the LAN, `401` without the token (when one is set), `503` when the detection is busy (a
backfill, the nightly job or another regroup; retry in a few seconds).

| Route | What it does (button on `/consumatori`) |
|---|---|
| `GET /api/nilm/signatures[?all=true]` | the signatures: id, name, category, labelSource, plugAlias (the plug that measures it, or the „posibil” plug), plugLinked, meanDeltaP, meanDeltaQ, meanTransient, count, hidden, lastSeen, minRunSeconds, maxRunSeconds, splitFromId. By default the page's filter (plug-measured ones, and the detected ones with ≥ 3 events, a name of their own or a „posibil” plug, not hidden); `all=true` adds the rare and hidden ones. Most events first |
| `GET /api/nilm/signatures/{id}` | the sheet („fișa”: profile over the window, with `repetition` over the last 30 days: `durations`, `intervals`, `sessions`, `pulsesPerSessionP50`, `energyPerSessionWhP50`), the duration band in `signature`, and the newest 50 events (ts, deltaP, deltaQ, label, source, …) |
| `POST /api/nilm/signatures/{id}/label` | body `{"name": "…", "category": "washer"}`, both optional but not both missing: rename (a user label) and/or set the category (`fridge`, `boiler`, `cooking`, `washer`, `hvac`, `lighting`, `electronics`, `other`) |
| `POST /api/nilm/signatures/{id}/merge-into/{targetId}` | „Unește cu…”: the survivor is chosen by the rule of the automatic merge (your name, then a plug, then more events); the answer gives `survivorId` and `absorbedId` |
| `POST /api/nilm/signatures/{id}/add-to/{targetId}` | „Adaugă la aparatul…”: `outcome` = `merged` (close centres: the events move to the target) or `named` (distant: the signature takes the target's name and category, two rows stay) |
| `POST /api/nilm/signatures/{id}/duration-band` | the duration band ([Duration bands](#appliance-detection-nilm)): body `{"minSeconds": 45, "maxSeconds": null}`, integers ≥ 0, min < max; both `null` clear it (`400` otherwise). Only runs that close from then on, and the next rebuild of the runs, feel it. The answer is the signature, with `minRunSeconds`, `maxRunSeconds`, `splitFromId` |
| `POST /api/nilm/signatures/{id}/split-by-duration` | body `{"maxSeconds": 40, "name": "Espressor", "category": "cooking", "dryRun": true}`: the paired runs up to `maxSeconds`, of every stored day, and their events go to the signature with that name and a compatible ΔP (a band of its own must start at zero and end at `maxSeconds` or below), or a new one labelled by you (`category` absent: the one of the signature split). Its band ends at `maxSeconds`; the band of `{id}` starts there unless it has one. An event you labelled holds its run. **`dryRun` defaults to `true`** and then nothing is written. The answer: `dryRun`, `applied` (`false` when nothing changed), `targetId` (`null` for a new one), `targetName`, `runs`, `events`, `skippedUserRuns`, `energyWh`, `moved` and `remaining` (`count`, `p10`, `p50`, `p90` seconds), `signature`, `target` |
| `POST /api/nilm/signatures/{id}/hide`, `…/unhide` | hide / show again |
| `POST /api/nilm/mark` | „acum am pornit X” (ground truth, no button): body `{"name": "Fierbător", "category": "cooking", "at": "2026-11-02T12:15:00+02:00"}`, `category` and `at` optional (default: now). The stored main-meter event with \|ΔP\| ≥ 100 W nearest to `at` within ±90 s is labelled by you, like an event label on `/consumatori` (it moves to the signature of that name if one exists; otherwise its automatic signature, or a new one, takes the name and the category): `outcome` = `matched`, with the event and its signature. Without such an event yet (the appliance starts in a few seconds, or the event still waits the 45 s for the plugs' readings) the mark waits in memory up to 10 minutes (`pendingUntil`; lost at a restart) and the detector gives it the first event ≥ 100 W it stores from `at` − 90 s on: `outcome` = `pending`. A time more than 45 s + 90 s in the past without such an event: `404` |
| `POST /api/nilm/regroup` | „Regrupează acum”: events, anchored, clusters, signatures, moved, merged, the merges with their reason, and the training that follows |
| `POST /api/nilm/train` | „Antrenează acum”: trained, labelled events and classes, the classes, cross-validated accuracy, folds, duration |
| `GET /api/nilm/plugs` | the plugs that can label events (polled, role *plug*): id, alias, single, applianceName (the name in effect when `single`: the alias when none was given), category, nilmState (`Auto`, `IgnoredAuto`, `IgnoredManual`, `AlwaysUse`: the state of learning), labelsEvents (whether it labels events: a plug with a single appliance always does) |
| `POST /api/nilm/plugs/{id}/appliance` | „un singur aparat pe priză” (the checkbox on `/dispozitive`): body `{"name": "Frigider", "category": "fridge", "single": true}`; `single` defaults to `true` (`false` clears the flag, name and category are kept), an empty `name` = the alias, no `category` = unchanged. The plug's linked signatures take the name and category at once. `404` for an unknown id or a meter ([Single-appliance plugs](#single-appliance-plugs)) |
| `GET /api/nilm/events?from=…&to=…&minAbsDeltaP=…` | the raw stored events (diagnostics), oldest first; `from`/`to` ISO 8601 (default: the last 24 h, at most 31 days), `minAbsDeltaP` in W (default 0) |

Examples (`K=http://<host>:8910`; add `-H "X-Api-Key: $TOKEN"` when a token is set):

```bash
curl -s "$K/api/nilm/signatures" | jq '.[] | {id, name, count, meanDeltaP, plugAlias}'
curl -s "$K/api/nilm/signatures?all=true"
curl -s "$K/api/nilm/signatures/53"
curl -s -X POST "$K/api/nilm/signatures/53/label" -H "Content-Type: application/json" \
     -d '{"name": "Mașina de spălat", "category": "washer"}'
curl -s -X POST "$K/api/nilm/signatures/55/merge-into/53"
curl -s -X POST "$K/api/nilm/signatures/61/add-to/53"
curl -s -X POST "$K/api/nilm/signatures/28/duration-band" -H "Content-Type: application/json" -d '{"minSeconds": 45, "maxSeconds": null}'
curl -s -X POST "$K/api/nilm/signatures/28/split-by-duration" -H "Content-Type: application/json" -d '{"maxSeconds": 40, "name": "Espressor", "category": "cooking"}'
curl -s -X POST "$K/api/nilm/signatures/28/split-by-duration" -H "Content-Type: application/json" -d '{"maxSeconds": 40, "name": "Espressor", "category": "cooking", "dryRun": false}'
curl -s -X POST "$K/api/nilm/signatures/70/hide"
curl -s -X POST "$K/api/nilm/signatures/70/unhide"
curl -s -X POST "$K/api/nilm/mark" -H "Content-Type: application/json" -d '{"name": "Fierbător", "category": "cooking"}'
curl -s -X POST "$K/api/nilm/mark" -H "Content-Type: application/json" -d '{"name": "Fierbător", "at": "2026-11-02T12:15:00+02:00"}'
curl -s -X POST "$K/api/nilm/regroup"
curl -s -X POST "$K/api/nilm/train"
curl -s "$K/api/nilm/events?from=2026-11-02T08:00:00Z&to=2026-11-02T09:00:00Z&minAbsDeltaP=100"
curl -s "$K/api/nilm/plugs" | jq '.[] | {id, alias, single, applianceName}'
curl -s -X POST "$K/api/nilm/plugs/<plug id>/appliance" -H "Content-Type: application/json" \
     -d '{"name": "Frigider", "category": "fridge", "single": true}'
```

## Data retention

| Data | Table | Written | Deleted |
|---|---|---|---|
| Raw readings (every poll, default 10 s) | `Samples` | ≈ 128 bytes per reading ≈ 1.1 MB per plug per day ≈ 400 MB per plug per year | never by default; only if `ReadingsRetentionMonths` > 0 (daily purge, in batches) |
| Raw live samples (on change or every 60 s) | `LiveSamples` | ≈ 146 bytes per row: quiet plug ≈ 0.2 MB/day, cycling load ≈ 4–5 MB/day, main meter ≈ 12 MB/day | after `LiveRawRetentionDays` (default 30; `0` = never), daily |
| Live minute aggregates | `LiveMinuteAggregates` | 1,440 rows per streamed channel per day | never |
| Relay commands | `RelayCommands` | one row per command | never |
| Appliance events, signatures, runs | `MeterEvents`, `ApplianceSignatures`, `ApplianceRuns` | one row per event (≥ 30 W step of the house) / appliance / run | never (since 0.9.2; before: 365 days) |
| Miele appliances, state snapshots, cycles | `MieleDevices`, `MieleStateSnapshots`, `MieleCycles` | a snapshot on every change of the state (≈ one per minute while a program runs, as the remaining time changes) and at most every 5 minutes otherwise; one row per program cycle | never |
| Climate sensors and readings | `ClimateSensors`, `ClimateReadings` | a reading on a change of ≥ 0.1 °C / ≥ 1 % RH and at least every 30 minutes per sensor (≈ 40 bytes; ≤ 48 rows per quiet sensor per day) | never (since 0.9.2; before: 365 days) |
| Climate per day | `ClimateDaily` | one row per sensor and day (≈ 100 bytes) | never |
| Minute aggregates | `OneMinuteAggregates` | ≈ 120 bytes per plug-minute ≈ 65 MB per plug per year (every 15 minutes since 0.14.0; daily before) | never |
| Hour aggregates (0.14.0) | `OneHourAggregates` | one row per plug and hour (≈ 9,000 per plug per year), from the minute rows | never |
| Daily / monthly energy | `DailyEnergy`, `MonthlyEnergy` | one row per plug per day / month | never |
| Per-entity preferences (0.11) | `EntityPrefs` | one row per entity whose display the user changed | never (a row back to every default is deleted) |
| Generic devices and entities (0.11) | `GenericDevices`, `GenericEntities` | one row per added device / entity (with its discovery payload) | never (a removed entity is marked) |
| Generic raw readings (0.11) | `GenericReadings` | ≈ 20 bytes per row (integer key; clustered on (EntityId, Ts) on SQL Server, WITHOUT ROWID on SQLite before 0.12.0): on a change above the threshold, at least every 30 min, at most one per entity in 10 s | after 90 days (MQTT tab; `0` = never), daily |
| Generic hourly rollups (0.11) | `GenericHourly` | one row per entity and hour | never |

Aggregates are never deleted and never replaced by ones built from fewer readings (minutes) or fewer minutes (hours):
rolling a day again after the retention deleted its raw readings leaves its minutes and hours as they were. So
`ReadingsRetentionMonths = 3` with `LiveRawRetentionDays = 30` keeps every chart of `/history` („Putere” reads only the
aggregates) and the daily energy, while the raw data stay bounded.

The sizes above were measured on SQLite (0.11 and earlier); on SQL Server a row takes about as much (fixed-width
`bigint`/`float` columns, page overhead), plus the indexes. Up to and including 0.2.1, raw readings older than 30 days
were deleted automatically; since then nothing is deleted unless you set a retention. Deletions run in batches of
5,000 rows, each its own short transaction; SQL Server reuses the freed pages but does not shrink the data file (no
need to). The database is created with recovery model SIMPLE (`deploy/sql/create-database.sql`): the transaction log
stays small and the restore point is the last full backup.

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
- `NILM API: signature <id> labelled (name <name>, category <category>) from <ip>` (and `… merged, <id> stays`, `… added
  to <id> (Merged|Named)`, `… hidden|unhidden`, `regroup from <ip>: …`, `train from <ip>: …`) — Information, every
  `POST /api/nilm/*` (`NILM API: mark <name> (category <c>) at <time> from <ip>: Matched|Pending|NotFound…`); `NILM: pending
  mark <name> labelled the event <id> …` and `NILM: mark <name> of <time> expired without an event …` — Information;
  `NILM API: POST <path> refused from <ip> (not a LAN address)` — Warning.
- `Miele account connected …`, `Miele access token refreshed (… refresh token rotated)`, `Miele: SSE stream open`,
  `Miele <alias>: cycle started (<program>)` / `cycle ended (<reason>, <min> min, API <kWh> kWh, plug <Wh> Wh)` —
  Information; `Miele: SSE failed 3 times in a row …; polling GET /devices every 00:01:00` and `Miele refresh refused,
  account marked disconnected` — Warning. Tokens, codes and the client secret are never logged.

## Build and test

```bash
dotnet build KasaMonitor.sln
dotnet test KasaMonitor.sln
```

Most tests run the data layer on SQLite in memory (same EF model, `EnsureCreated`). `tests/KasaMonitor.SqlServer.Tests`
runs on a real SQL Server in Docker (Testcontainers, `mcr.microsoft.com/mssql/server:2022-latest`): the
migrations, the collation, the repository tests again, the app start-up and the data migration tool. It is skipped when
Docker is not available (or with `KASA_SQLSERVER_TESTS=off`).

To run the app locally, give it a development database (never a production one) through the user secrets of the web
project: `dotnet user-secrets --project src/KasaMonitor.Web set ConnectionStrings:Kasa "Server=localhost,1433;Database=kasamonitor_dev;…"`.

## Docker image

From the repository root, with Docker running and `docker login` done once:

```powershell
.\deploy\build-and-push.ps1 -Version 0.12.0         # build :0.12.0 + :latest and push both
.\deploy\build-and-push.ps1 -Version 0.12.0 -NoPush  # local build only
```

or manually:

```bash
docker build -t duobit/kasamonitor:0.12.0 -t duobit/kasamonitor:latest -f deploy/Dockerfile .
docker push duobit/kasamonitor:0.12.0
docker push duobit/kasamonitor:latest
```

To publish under your own Docker Hub account, change the image name (`-Repo` parameter of the script and
`image:` in `deploy/docker-compose.yml`).

## Updating

1. Back up the database (`BACKUP DATABASE [kasamonitor] TO DISK = …`, or the last nightly backup) and the `/data`
   volume (`dp-keys/`).
2. `docker compose -f deploy/docker-compose.yml --env-file kasamonitor.env pull && docker compose -f deploy/docker-compose.yml --env-file kasamonitor.env up -d`
   (Portainer: *Update the stack* with *Re-pull image and redeploy*). New migrations are applied at start-up.
3. Check `http://<host>:8910/health`, `/dispozitive` (Adaugă → Scanează acum) and `curl -s http://<host>:8910/metrics | grep kasa_online`.

0.14.0 has no migration (`OneHourAggregates` existed, unwritten; its `SampleCount` now holds the minutes of the hour).
`RollupWorker` runs every 15 minutes instead of once a day (the readings since its previous cycle; once a day also the
whole of yesterday, then the retention, as before); the first cycle after the start rolls the last 7 days again and fills
the hour rows of every past day that has minutes but no hours — once, plug by plug and day by day, logged as
`Hourly power aggregates backfilled: N hours on M plug-days in T ms`. It is idempotent: a restart finds nothing left to do. After this
release `ReadingsRetentionMonths` can be lowered (e.g. to 3): the „Putere” chart and its comparisons need only the
aggregates. The comparison of `/history` is now a list; old addresses (`cmp=0`, no `cmp`) open as before.

0.13.2 has no migration. The runs of a single-appliance plug are taken over at the first pass after the start: the runs of every signature of the plug are replaced
from the later of the plug's first stored reading and the start of the clustering window (older ones stay), and new ones come from the plug. Shadows of the last 6 hours are marked at once, the
others at the first regrouping (03:30, at a start when the last one is older than 24 h, or „Regrupează acum”); until then the detected
energy still holds them. `GET /api/nilm/events` can return the new source `PlugShadow`. `tools/KasaMonitor.NilmReplay` has `--plug-shadows <plug-stream.csv> <events.csv>`, which
prints these rules' results on your own data.

0.13.1 has no migration. The signature of a single-appliance plug is cleaned at the first regrouping after the upgrade (03:30, at
a start when the last one is older than 24 h, or „Regrupează acum” on `/consumatori`); until then its count stays as it was, and
new events already keep out. A single-appliance plug that was excluded from learning starts labelling new events; stored events
are not labelled again. A hidden signature no longer takes new events, except the confirmations of its plug. `GET /api/nilm/plugs`
adds `labelsEvents`.

0.13.0 adds one migration (`DurationBands`: three nullable columns on `ApplianceSignatures`), applied at start-up over the existing
data; nothing moves until a band is set. `tools/KasaMonitor.NilmReplay` (`dotnet run --project tools/KasaMonitor.NilmReplay -- <meter.csv>`) replays
a CSV of the main meter's seconds through the event detector, with and without a median; it is not part of the image.

### From SQLite (0.11 and earlier) to 0.12.0

0.12.0 replaces the SQLite file with SQL Server; the schema is one new migration (`InitialCreate`), and the data are
copied once with `tools/KasaMonitor.DataMigration` (`kasa-migrate`):

1. Update to the last SQLite release (0.11.x) first and let it start once (its migrations bring `kasa.db` to the last
   SQLite schema). Then stop it and take a consistent copy of `kasa.db` (`sqlite3 kasa.db ".backup kasa-copy.db"`).
2. Create the database and the login (`deploy/sql/create-database.sql`); do not start 0.12.0 yet.
3. `ConnectionStrings__Kasa='…' dotnet run --project tools/KasaMonitor.DataMigration -c Release -- --source kasa-copy.db --report migrate-report.md`:
   applies the migration, copies every table (batches of 5,000 rows, `SqlBulkCopy`, ids kept) and verifies each one
   (`COUNT(*)` and the sums of the numeric columns); a non-empty database is refused (`--truncate`, `--resume`).
4. Copy `dp-keys/` (and `nilm-model.zip`) of the old `/data` into the new data directory; without the keys the
   passwords and tokens saved in `/settings` must be entered again.
5. Deploy the 0.12.0 stack (`ConnectionStrings__Kasa`, `KasaMonitor__DataDir`; `KasaMonitor__DbPath` is no longer
   read) and check `/health`.

Details, timings and the backup with SQL Agent: `deploy/README.md`. The notes of the upgrades between SQLite versions
(0.1 → 0.11) are in the README of 0.11.

See also `deploy/README.md` (troubleshooting).
