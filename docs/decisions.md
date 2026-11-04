# Design decisions

A short log of the design decisions that shaped KasaMonitor, in the order they were taken (by the release that
introduced them). Each entry is context → decision → consequences. Behaviour is documented in the
[README](../README.md); structure in [architecture.md](architecture.md); the detection pipeline in [nilm.md](nilm.md).
Entries marked **planned** are intentions, not code.

Numbers quoted in the entries are measurements from the author's own installation unless stated otherwise.

---

## 0.8.1 – Local day vs the boiler's day

**Context.** The gas boiler (Viessmann Vitodens, read through the ViCare cloud) exposes „today” counters for gas, heat
and electricity. It resets them at 00:00 in its own time zone, Central European Time (measured: the reset arrives at
23:00 UTC; in Romania that is 01:00 local time, in summer too, because both zones change together). Attributing a
reading to the container's local day misfiled the first hour of every day, and the cloud sometimes re-sends
yesterday's total after the reset.

**Decision.** A setting `BoilerTimeZone` (default `Europe/Berlin`) defines the boiler's day. Every daily attribution of
the boiler's gas, electricity and heat uses it: daily rows, the day/week/month/year reports, the hourly profile, the
comparisons and the total counter. The electricity of plugs and the meter stays on the local day. In the first 30
minutes after the boiler's midnight, a „today” value that is still ≥ 90 % of the previous day's highest is treated
as yesterday's total read late and goes to the previous day. A value lower than the day's stored highest is a reset
only when the boiler's clock is between 23:45 and 00:30; any other drop is ignored. After a near-zero day nothing is
considered stale, so a small new value is never counted twice.

**Consequences.** Reports say „zi = după ceasul centralei, se încheie la 01:00 ora României”. Cost: two day
definitions coexist and every page that mixes gas and electricity must say which it uses (the history and home
pages do). Days written before the change are not rewritten; the first start after the upgrade repairs only the
current boiler day.

## 0.8.2 – Consumer detection: anchors, unknown transients, automatic merge

**Context.** After a few weeks of data the clusterer produced several signatures for one physical component (a washing
machine's heater split in two, a drum motor in three), and a kettle's switch-on was sometimes attracted to a nearer
anonymous cluster instead of the signature the user had named. Plug labels were also missed because the matcher
looked at single readings taken in the middle of a ramp.

**Decision.** Four rules, detailed in [nilm.md](nilm.md):
1. The transient ratio is used only when it was really measured. A step that took ≥ 1 s and shows a ratio ≤ 1.5 has
   an *unknown* transient: never compared, never part of a mean. A peak above 1.5 is always real (a motor's inrush).
2. User labels have priority. A signature named by the user or linked to a plug is an **anchor**; new events and the
   nightly re-clustering try anchors first with a wider tolerance (1.5 × eps), so a name is never taken away.
3. Signatures that are the same component are merged automatically (same name, centres within eps, same plug, or the
   inductive variable-load rule) with guards: never two different plugs, never two different user names, never hidden
   ones. The survivor is the user-named one, else the plug-linked one, else the one with more events.
4. Plug matching compares medians of the plug's level before and after the step on a 1-second grid, ignoring ±3 s
   around the event, instead of single readings.

**Consequences.** On the author's data, the washing machine's heater ended in one signature, three kettle switch-ons
went to the named signature, and the drum motor ended in two signatures instead of three. The rest of the
tolerances are deliberately fixed numbers rather than learned values, so a merge can always be explained. A
signature is a **component** (heater, motor, pump), not an appliance; a multi-stage appliance stays several rows.
Absorbed signatures are deleted (not hidden): their events and all their runs move to the survivor and their
`kasa_appliance_*` series disappear.

## 0.8.3 – Operating the detection without the UI

**Context.** Labelling and merging signatures needs many small actions; doing them in a browser is slow and cannot be
scripted.

**Decision.** `/api/nilm/*` exposes the same actions as `/consumatori` (list, sheet, label, merge, add to, hide,
regroup, train, raw events, „now I switched on X” marks). Every route calls the same service method as the button,
and nothing in the API decides on its own. LAN-only; every write is logged with the caller's address.

**Consequences.** One code path per action, so the API cannot drift from the UI. No authentication: it relies on
the LAN restriction.

## 0.8.4 – Burner timeline and the „purpose” rule

**Context.** The ViCare API has no explicit „burner is heating the hot water” signal on this boiler model. The page
needed to show for what the burner burns, and the gas split between heating and hot water depends on it.

**Decision.** The purpose of each contiguous burner run is *inferred*, in order of reliability: (a) which of the
boiler's gas „today” counters grew during and shortly after the run (the counters update late, so the look-ahead is
up to 60 minutes but never past the next run; each increase belongs to one run; both counters growing splits the run
in proportion when it is longer than 10 minutes); (b) only if no counter grew (0.1 m³ steps), the temperature
trends, tuned for a combi boiler whose hot-water sensor follows the supply temperature while the burner runs.
Otherwise *unknown*. Gaps of up to 5 minutes beyond the usual cadence do not break a run.

**Consequences.** The rule is one pure function (`BurnerTimeline`) used by the page, the history page and the
`heating_burner_purpose_info` metric, so they always agree. The first version of rule (b) mislabelled a heating run
as hot water on real data, because the late counter had not arrived yet; the look-ahead window and the „rises
relative to the supply” condition were added for that reason. A run still going may show *unknown* until a counter
moves, and the page re-evaluates closed runs when the late counter arrives, but samples already in Prometheus keep
the value they had.

## 0.8.5 / 0.8.6 – Miele cycles from three sources

**Context.** A Miele appliance reports energy and water for the current program through its live state („eco
feedback”), keeps its own history of used programs on the cloud, and may be plugged into a metered plug. None of the
three is complete: the live feed misses cycles when the connection drops, the cloud history is consent-gated and
coarse, and the plug sees the whole appliance but knows nothing about programs.

**Decision.** A cycle (`MieleCycles`) keeps the three measurements side by side and never overwrites one with another:
the live-state values (maximum seen before the counter resets at the end), the program-history values from the
API, and the linked plug's energy. A cycle is opened by the state stream and closed by the end status, or after 30
minutes without the running status. Program history is fetched after connecting, every 6 hours and 2 minutes after
a cycle ends, and merged into an observed cycle when the start times are within 10 minutes or the overlap is ≥ 50 %
of the shorter one; an unmatched API run becomes a cycle of source `api` without phases. The table shows the API
value, else the plug, else the live value, each with a „source” tooltip. The stream is SSE with a polling fallback
after three failed connections; authentication is OAuth (authorization code) with the client secret read from a
file or the environment, never stored.

**Consequences.** Merging is idempotent (unique index on device and API start). Because the plug's energy is
independent, the cycle detail can show Miele's estimate against the plug's measurement. The cycle starts also serve
as ground truth for the appliance detection (a running washing machine explains a step of the house).

## 0.8.7 – Climate: one generic sensor model, many sources

**Context.** Temperature and humidity come from four unrelated places: the boiler's room thermostat, cloud
thermometers (Govee), local Shelly H&T devices and a weather service for the outdoor temperature.

**Decision.** A single model: a **sensor** (`source`, external id, editable name, room, outdoor flag, archived) and
its **readings**. A reading is stored when the temperature changed by ≥ 0.1 °C or the humidity by ≥ 1 % RH since the
last *stored* reading, or at least every 30 minutes; a value read in between only updates the card and the metrics.
Each source has its own schedule, and a failing source never stops the others. Govee devices of an account are
requested only when ticked as „home” (none at first), because the account may contain thermometers elsewhere and the
API has a daily quota. The Govee key is read from a file or the environment at start-up, never stored. „Outdoor” is
a flag on the sensor, not a property of the source: a physical outdoor sensor takes precedence over the weather
service. Freshness is judged against each source's own interval (green ≤ 2 intervals, yellow ≤ 6, red beyond).
Averages are time-weighted: a value holds until the next reading, at most 60 minutes.

**Consequences.** Adding a source does not need a migration. Readings are kept without limit (since 0.9.2), because
the history page compares years and heating degree-days need the whole record. The Shelly H&T and the Add-on were
implemented from the vendor's documentation and tested on synthetic answers; the Govee battery capability is not
reported by the model in use.

## 0.9.0 – Home page: scaling rule and cache

**Context.** The home page combines three different measurements of the same electricity: the main meter, the plugs,
and the appliances inferred by the detection. They never add up exactly, and the page must still draw bubbles, bars
and a Sankey whose parts sum to the meter.

**Decision.**
- **Breakdown rule.** The parts always sum to max(meter, Σ plugs): plugs keep all their power; *standby* (the night
  floor of the rest of the house) takes at most what is left; detected appliances are scaled down together in what
  remains; the undetected rest is the difference (≥ 0). Standby is the median of (meter − Σ plugs) per minute between
  01:00 and 05:00 over the last 7 nights; a night is skipped if a streamed plug covers less than half of it, and a
  floor of 5 W or less is reported as not measurable rather than drawn. Detected energy below 0.01 kWh in a period is
  counted as undetected.
- **One scaling rule for flows.** When detected energy exceeds the rest, it is scaled bar by bar by one function
  shared by the home page and the history page, so both give the same detected/undetected split.
- **Comparisons.** „Today” is compared with yesterday *up to the same time*, both as sums of the main meter's live
  minutes (same source). If either window is covered less than 90 % by the stream, the page says „date parțiale”
  instead of showing a difference. A cost total exists only when every component that should exist does; a missing
  part is never counted as 0.
- **Cache.** One service computes each period at most once per minute for all circuits (TTL: 1 minute for today,
  10 minutes for a period in progress, until the day changes for the past; at most 32 past entries). The cache entry
  is created *before* the computation and computed on the thread pool; concurrent callers await the same task, and an
  expired value is served while a single recomputation runs (stale-while-revalidate).
- **Stale sources.** If the boiler is offline or older than two poll intervals, or a Miele appliance is disconnected,
  its card is greyed with „last known state, N min ago” rather than hidden or shown as current.

**Consequences.** The page never blocks a circuit on a computation and the numbers on it are mutually consistent. The
price is that a few cards intentionally show less (partial data, no estimate in the first 6 hours of a month,
„low confidence” below 7 days of data).

## 0.9.1 – Plugs with a single appliance

**Context.** For a plug that only ever carries one appliance (a fridge, an air conditioner), the plug *is* the
ground truth; waiting for 3 events and a 60 % share before linking a signature is needless, and mixing its events
into the classifier teaches the model nothing.

**Decision.** A plug can be marked „single appliance” with a name and category. An event confirmed by that plug is
labelled with the appliance's name at once and goes to the signature linked to the plug (within the anchor radius),
or to a new signature linked right away. The plug is excluded from the classifier's training set but not from
labelling and linking. The name is shown where the plug stands for the appliance (home bubbles and Sankey,
`/consumatori`, the new metric `kasa_appliance_plug_power_w`), while the plug keeps its alias in `kasa_*` and
history. Nothing is set automatically on upgrade.

**Consequences.** The new signature is the *plug's* signature, not a rename of whichever anonymous cluster the event
would have fallen in (that cluster may hold events of a PC or UPS). Appliances with several components (an inverter
air conditioner) can therefore have several signatures linked to one plug; the plug's own measurement stays the
reference for energy. `kasa_*` metrics are unchanged.

## 0.9.2 – One History page, one period selector, unlimited retention

**Context.** Reports were spread over several pages with different period controls, and some data was deleted after a
year, so comparisons across years could not work.

**Decision.** A single `/history` page with a single period selector (day, week, month, year, free interval) whose
state is entirely in the address. Sections: electricity, gas and boiler, Miele, detected consumers, climate (with heating degree-days and
the energy signature), costs, events. Differences are computed **at the same fraction of the previous period**: a
period in progress is compared with the same share of the previous one, not with the whole of it. Days are local days;
gas follows the boiler's day. Every table has a CSV export built from the same table object as the page (separator `;`,
decimal comma, UTF-8 with BOM, ISO keys in the first column). Degree-days use the Eurostat rule (base 18 °C, threshold
base − 3) for totals and bars, while the energy-signature regression uses max(0, base − T) without a threshold.
Events, runs and climate readings are no longer deleted.

**Consequences.** Links from every other page carry their period into history. Because climate over a year of raw
readings measured 288 ms against a 300 ms budget on a development machine (136,296 rows), a daily summary table
(`ClimateDaily`) was added behind an interface before a year of data accumulated; it recomputes the last 3 closed days
so a late reading lands in its day. Boiler events are computed in SQL with window functions (265 ms → 168 ms for a
year of 227,130 readings).

## 0.9.3 – Water temperatures are not climate

**Context.** The boiler's supply and hot-water temperatures had been exposed as virtual climate sensors beside the
room thermostat. They are water temperatures, not ambient ones, and they polluted room lists, `climate_*` metrics,
min/mean/max and the history page.

**Decision.** Only the room thermostat is a climate sensor of the boiler. The supply, hot-water and hot-water-target
temperatures stay on the boiler page and in `heating_*`. A one-time clean-up at start removes the former virtual
sensors with their readings and daily rows (idempotent). On the boiler page, each temperature line now has an
explicit colour so the chart matches its legend.

**Consequences.** No data of the boiler is lost: the same temperatures are in `HeatingReadings`. The clean-up lives in
the recorder's load path so that removed sensors can never reach the UI or the metrics, whoever loads first.

## 0.9.4 – Rare signatures and diacritic-insensitive sorting

**Context.** The home cards listed individual anonymous signatures (dozens of rows of 0.00 kWh) that the main table
hid by default, and long-open runs of a missed switch-off were shown as „running”.

**Decision.** The default list shows signatures with at least 3 events, a name or a „possible plug” badge. On the
cards, everything else is summed into one row „Semnături rare (n)” (linking to the list), so every name on a card
exists in the default list; metrics per signature are unchanged. A run open longer than 6 hours is never shown as
running. Every column of the table sorts the whole list before paging, the order is kept in the address, rows without
a value stay last in both directions, and text sorts ignore diacritics (Unicode decomposition without non-spacing
marks plus an explicit map for ă â î ș ş ț ţ), so a name starting with „Ș” sorts with S even in the invariant-globalization
container.

**Consequences.** Cards and table agree; the sort is the same in any culture setting.

## 0.10.0 – Integration registry, settings on tabs, one devices page

**Context.** A device's configuration lived in five places (plugs page, Miele page, climate page, settings cards,
details); „add” had four different flows; „withdraw” existed in the UI only for plugs; „online” had four rules and
no common projection; settings of one vendor were mixed with cross-cutting ones (tariffs, thresholds).

**Decision.**
- The axis of the UI is the **integration** (the vendor connection), not the device category. A registry
  (`Integrations`) is the single list behind the settings tabs, the vendor filter, the state chips and the „+ Adaugă”
  menu; adding a vendor is one entry plus its tab, its add panel and its catalog rows.
- `/settings/{tab}` has one tab per integration plus „Aplicație” (cross-cutting: tariffs, discovery, live stream,
  retention, detection, degree-days) and „Export”. Each tab saves on its own with partial (nullable) updates; an
  unsaved-changes guard covers tab links and reload. Validation (e.g. gas settings) happens before any write.
- `/dispozitive` is one table for every device of every integration with category, vendor, state and „show withdrawn”
  filters; an expanded row is the only place a device is configured. Withdraw/restore is uniform, with an inline
  confirmation for the main meter and the boiler.
- **No new table.** The list is a projection (`DeviceCatalog`) of the existing tables, and the state of an
  integration is a pure function of states that already exist.
- Climate-only Shelly devices are skipped by plug discovery (they used to appear as „unsupported plug”); old rows
  are archived once. The old `/plugs` redirects permanently.

**Consequences.** One place per concern, no migration, and nothing new to keep in sync. The „online” rules stay
different per integration on purpose (10 s polling, cloud, SSE, battery sensors); only their presentation is shared.

## 0.10.1 – Static files revalidated on every load

**Context.** After a release the browser kept an old stylesheet through heuristic caching (the response had an
`ETag` but no `Cache-Control`), so new markup rendered unstyled until a hard reload.

**Decision.** Static files are sent with `Cache-Control: no-cache`: the browser revalidates with the `ETag` on every
load and still gets a `304` when nothing changed.

**Consequences.** A release's CSS and scripts are visible immediately; the cost is one conditional request per file.

## 0.12.0 – From SQLite to SQL Server

**Context.** Until 0.11 everything was in one SQLite file next to the app. The installation it was written for already
runs a SQL Server with a nightly SQL Agent backup job for its other databases, and a Portainer stack per application.
The SQLite file needed its own backup script (copy, `integrity_check`, mirroring), its writers were serialised by
`busy_timeout` and WAL, and the settings of the container were split between the compose file and mounted files.

**Decision.**
- **SQL Server only**, no second provider: `UseSqlServer` with `EnableRetryOnFailure`; the 14 SQLite migrations are
  replaced by **one `InitialCreate`** generated from the model. Two providers would mean two migration folders and two
  schemas to keep equal at every model change.
- The database is created outside the app (`deploy/sql/create-database.sql`): collation `Latin1_General_100_CS_AS_SC`
  so identifiers compare exactly as in SQLite whatever the server's collation, recovery SIMPLE, a login that owns
  only its database. The app applies its migrations, never creates the database.
- Explicit lengths on every string of a key or an index (SQL Server cannot index `nvarchar(max)`, and an unbounded
  key would be `nvarchar(450)`, too wide for the four-string key of `HeatingEnergyPeriods`).
- Start-up waits for the server (120 s, the reason in the log) and tries the migrations 3 times without EF's retrying
  strategy (≈ 3 min at most with the server missing); `/health` reports `degraded` instead of failing the process; the
  live stream keeps its buffer meanwhile. EF generates SQL for compatibility level 150 (SQL Server 2019), and the SQL
  Server suite runs on 2019 CU22 as well as on 2022 (`KASA_SQLSERVER_IMAGE`).
- **The live batch is stored once.** A retried commit whose answer was lost, or a batch put back in the queue, must not
  duplicate samples nor count a minute twice: `LiveSamples` has a unique key `(PlugId, Ts)` (one row per plug and
  second; none of the 164,599 rows of the real copy collided), `SaveBatchAsync` skips the keys already stored, and the
  batch runs in `ExecuteInTransactionAsync` whose `verifySucceeded` checks the batch's last sample (or its first minute
  with the merged values) before a retry. `IGNORE_DUP_KEY = ON` was tried and rejected: EF counts the rows of its
  inserts, an ignored row raises `DbUpdateConcurrencyException` and the whole batch, new rows included, is lost
  (measured on SQL Server 2022).
- **What SQL Server refuses is handled before it is sent** (`KasaDbContext.PrepareForStore`): NaN/±Infinity in a
  nullable `double` becomes NULL (SQLite stored them; one would fail a whole batch at every flush), a text longer than
  its `HasMaxLength` is cut, an identifier too long is refused with its column. Retention deletes run in batches of
  5,000 on every large table (one DELETE of more rows escalates to a table lock).
- **0.11 on SQL Server.** The generic tables keep their integer keys; `GenericReadings` and `GenericHourly` are clustered
  on `(EntityId, Ts|HourTs)` (the SQLite `WITHOUT ROWID` layout); `EntityPrefs.Key` is 400 characters (800 bytes, under
  the 900-byte clustered key). Prefix searches use `StartsWith` (`LIKE 'prefix%'`), never a range up to `prefix + U+FFFF`:
  a linguistic collation ignores that character and the range comes back empty.
- **Every setting from the environment** (the Portainer stack); a secret comes from a mounted file when the file is
  present, otherwise from a variable of the stack. The data directory (`KasaMonitor__DataDir`) no longer derives from a
  database path.
- The data move once, with a tool (`tools/KasaMonitor.DataMigration`): bulk copy with ids kept, per-table counts and
  sums, refusal of a non-empty target, resume after a failure.
- Tests: the model is provider-agnostic, so the fast suites stay on SQLite in memory (`EnsureCreated`); a separate
  suite runs the migration, the repositories and the start-up on SQL Server in Docker.

**Consequences.** One backup routine for all databases (SQL Agent), real concurrency (no `busy_timeout`, no WAL, no
`VACUUM`), the data reachable from SSMS or Grafana, one place for the settings (the stack). Lost: the single file that
could be copied anywhere and the zero-dependency start — KasaMonitor now needs a SQL Server 2019+ reachable from its
host, and does not start without it. Queries go over the network (≈ 0.3–1 ms per round trip instead of µs); the
period cache of the home page and of `/history` hides most of it. EF's SQL Server provider compares string keys
case-insensitively in its change tracker while the database is case-sensitive: harmless for the keys used (setting
names are constants, Miele ids are digits).

---

## 0.11.0 – Generic devices: a common entity model, a generated page, MQTT Discovery

**Context.** Every new device family needed code: a client, a recorder, a page, metrics. Devices that describe
themselves (Home Assistant's MQTT Discovery: Zigbee2MQTT, ESPHome) can be read without that, but only for what they
describe: what links several values (totals, cost, cycles, NILM) cannot be self-described.

**Decision.**
- **Demarcation rule.** The generic layer shows and keeps values per entity (current value, history, per-entity
  statistics). Anything that links several entities or devices (house balance, totals, cost, NILM, cycles, degree
  days) stays in the rich integrations; a generic device enters such a calculation only through an explicit adapter.
  0.11 has one: a generic climate sensor is recorded through `ClimateRecorder` (source `mqtt` = the integration key).
  Generic power and energy never enter the totals, the cost or the detection.
- **A common entity model** (`Core/Devices/Entities`): kind, data type, the app's own device class mapped from HA's
  `device_class`, a canonical unit (values converted on the way in), state class, category, precision, writable.
  The page `/dispozitive/{key}` is drawn from `Kind × StateClass × DeviceClass × EntityCategory` (pure rules), not from
  the data type alone.
- **Projection, not migration.** The existing integrations get read-only `IEntitySource` projections (plug, climate
  sensor, boiler, Miele); their tables are untouched (the database has no off-host copy and feeds NILM, history and
  the alert rules). Reflection tests keep the projections complete. Per-entity preferences live apart from the
  descriptions (`EntityPrefs`), since a discovery message rewrites the description.
- **MQTT Discovery, read only.** Both discovery formats, the abbreviations and the `~` base; a small template subset
  (anything else marks the entity, the raw payload is never used instead); availability and `expire_after`; MQTTnet 5
  behind a thin `IMqttTransport` (own reconnection and re-subscription, the CONNACK result checked). No command is
  published (KasaMonitor has no authentication). New additive tables with integer keys (`GenericReadings` and
  `GenericHourly` are `WITHOUT ROWID`), a write rule with a 10-second cap, hourly rollups kept forever, raw rows 90 days.
- **Metrics per class**, one unit per family (`entity_temperature_celsius`, `entity_energy_wh_total` …), `entity_value`
  with a `unit` label only for unknown classes; no ids in labels; a series limit.
- **Keys decide owners**: the expanded row, „Retrage” and the page dispatch on the key prefix (`plug:`, `miele:`,
  `heating:`, `climate:`, `dev:`), never on the category; `PlugProtocols.Integration` maps protocols explicitly.

- **Untrusted input.** Discovery payloads come from any publisher on the broker: templates are bounded (512
  characters, 32 levels, 128 nodes) and never throw; payloads over 64 KB, wildcard state topics, unique ids over 128
  characters and more than 500 devices are refused and listed as problems; texts are cut to their column lengths and
  only finite numbers are stored.
- **A retained state is not a reading.** What the broker kept is shown (without a time) but never stored, and the last
  reading's time stays; the same holds after every reconnection.
- **Freshness without availability.** An entity with neither availability nor `expire_after` is fresh up to 2 expected
  intervals, „Întârziat” up to 6, then not available; the interval is the observed cadence (median gap, 30 s – 12 h),
  else 1 h for a number and 12 h for an on/off or a text.
- **Counters as Home Assistant counts them.** A `total_increasing` value under 90 % of the highest seen is a reset; a
  smaller dip is ignored. What a counter counted while KasaMonitor was not listening goes to the hour of the first
  reading after the gap.
- **Deduced state class.** A device that sends no `state_class` gets `measurement` only for the classes that cannot be
  anything else (temperature, humidity, pressure, illuminance, CO₂/CO, PM, VOC, voltage, current, power, signal
  strength, battery) with a recognised unit and not diagnostic — never energy (total or total_increasing cannot be
  told) and never a number without a class. The entity is marked `StateClassInferred` („dedus” in its row).

**Consequences.** A Zigbee2MQTT or ESPHome device needs no code; the rich pages stay what they were. OpenMetrics
scraping, commands and Shelly over MQTT are not in 0.11 (no real target yet / no authentication / RPC stays better).

---

## 0.13.0 – Duration bands set by hand; the median filter measured and left out

**Context.** An espresso machine (pulses of ≈ 1.43 kW, 10–20 s) and a kettle (≈ 1.4–1.5 kW continuous, 1–7 min) on one
plug fell into one signature named by the user. ΔP and ΔQ cannot separate them, and neither can the ML.NET classifier,
which has the same features. The duration of a run can.

**Decision: an explicit band, not an inferred one.** A signature has `MinRunSeconds`/`MaxRunSeconds`, set only by the user
(the „fișa”, the API). The durations of a signature are a mixture exactly when the band would be needed: inferring it from
the runs means separating the two groups first, with nothing to check the result against, and an inferred band
that drifts moves energy silently from one day to another. A wrong hand-set band is visible and can be corrected; a dry
run of the split shows the two groups of durations before anything moves. Inference stays an open idea.

**Decision: the rule acts when a run closes, not on the match.** A switch-on's duration is unknown until its switch-off,
so an anchor cannot use it; the run is moved afterwards, with both events (source `DurationRule`), only if exactly one
other signature fits. An event labelled by the user holds the whole run: moving only its partner would break the pair at
the next rebuild. The two sides of a split can have the same centre, so new events near them all wait in the side
that was split from (otherwise a switch-on and its switch-off could land apart and never pair). To know that two
signatures are such a pair the table also keeps `SplitFromId`, without a foreign key: a merge moves it to the survivor, a
deletion ends it. The moved events do not train the classifier: it is told nothing about the duration, so they would
only add two classes with the same features.

**Decision: the median filter stays out.** Replayed with `tools/KasaMonitor.NilmReplay` on the real main meter
(172,892 stored seconds, ≈ 63 hours, minimum step 30 W), with a 3-second centred median setting the levels and the transient
taken from the raw seconds. It was to be integrated only if it reduced the false events without losing or moving real
steps:

| | actual | median |
|---|---:|---:|
| events | 7,109 | 8,245 |
| of which \|ΔP\| ≥ 100 W | 1,055 | 1,143 |
| a step and its opposite within 5 s (likely false) | 447 | 657 |
| \|ΔP\| ≥ 100 W without a lasting change in the raw levels | 244 | 253 |
| \|ΔP\| ≥ 100 W confirmed by the raw levels, absent from the other mode | 38 | 134 |

Of the 5,007 events common within ±2 s, 1,404 moved by a second or more and 2,186 changed their rise time; of those
with \|ΔP\| ≥ 100 W, 139 changed by more than eps. So it does not reduce the likely false events, it adds to them (probably because the
filtered series settles more easily, so more small transitions count as steps), and it loses or moves real ones. It did find
134 lasting steps the detector misses, and in a measured espresso cycle it started one pulse on its own second where
a wobble had opened the transition 7 s early. That is a reason to look again with a different gate, not to change
every event's timing and transient now. The detector is unchanged.

**Consequences.** One migration after `InitialCreate` (three nullable columns). A band only applies from then on (and
at the next rebuild of the runs); what already stands in a signature moves with the split. A split-off signature of a
plug-measured one stays linked to that plug, though the plug cannot label pulses under ≈ 15 s, or its energy would be
subtracted twice from the rest of the house. The Prometheus counter of a signature keeps what it counted before a split, like
after a rebuild of the runs.
