# What the plugs' local APIs offer

Reference for deciding what KasaMonitor can show or do next. Three local protocols are covered:

- **Kasa legacy** (e.g. KP115): JSON on TCP/UDP port 9999, XOR-obfuscated. No authentication.
- **Tapo SMART** (e.g. P110/P115): JSON `{"method": …, "params": …}` over KLAP or AES "securePassthrough" on HTTP port 80. Needs the TP-Link account.
- **Shelly Gen2/Gen3** (e.g. Shelly EM Gen3): JSON-RPC 2.0 over HTTP, `GET http://<ip>/rpc/<Method>?<params>`. Optional digest authentication (see the Shelly section).

Method names come from the python-kasa project, which exercises these APIs (`kasa/iot/…` for legacy, `kasa/smart/modules/…` for SMART), and from the answers of our own plugs. The SMART part was checked against a P115(EU) fw 1.2.3 capture. Write commands marked "names unverified" do not appear in python-kasa and must be confirmed before use. A device lists the SMART features it has in `component_nego` (e.g. `auto_off`, `power_protection`, `led`, `countdown`, `schedule`, `antitheft`, `default_states`, `energy_monitoring`, `firmware`, `cloud_connect`). A request for a missing feature answers with an error code (e.g. -1).

Legend for the "KasaMonitor" column:

- **used**: read today.
- **shown**: read only when the details card on the Dashboard is opened (cached 60 s).
- **easy**: read-only, can be added like the details card.
- **action**: a write command; not implemented, available on request.

## Kasa legacy (KP115)

| Area | Request (module.method) | What it returns / does | KasaMonitor |
|---|---|---|---|
| State and identity | `system.get_sysinfo` | alias, model, hw/sw version, MAC, `relay_state`, `on_time` (s), `led_off`, `rssi`, `next_action`, `updating` | **used** (discovery, alias, model); **shown** (relay, uptime, LED, RSSI, firmware) |
| Relay | `system.set_relay_state {state: 0/1}` | turn on/off | **action** |
| LED | `system.set_led_off {off: 0/1}` | LED on/off | **action** |
| Alias | `system.set_dev_alias {alias}` | rename on the device | **action** (KasaMonitor renames only locally) |
| Reboot / reset | `system.reboot {delay}`, `system.reset {delay}` | restart / factory reset | **action** (reset: deliberately not) |
| Live energy | `emeter.get_realtime` | `power_mw`, `voltage_mv`, `current_ma`, `total_wh` | **used** (every 10 s) |
| Energy history | `emeter.get_daystat {year, month}`, `emeter.get_monthstat {year}` | Wh per day / month | **used** (history sync) |
| Time | `time.get_time`, `time.get_timezone` | device clock (y/m/d h:m:s), time zone index | **shown** |
| Time (write) | `time.set_time`, `time.set_timezone` | set clock / zone | **action** |
| Schedules | `schedule.get_rules`, `schedule.get_next_action` | on/off rules (days, time, sunrise/sunset), next action | **shown** (count, enabled, next action) |
| Schedules (write) | `schedule.delete_rule` / `delete_all_rules` / `set_overall_enable` (add/edit: names unverified) | manage rules | **action** |
| Usage statistics | `schedule.get_daystat` / `get_monthstat` | minutes on per day / month (runtime) | **easy** |
| Away mode | `anti_theft.get_rules` (+ add/delete) | random on/off while away | **shown** if the device has it (KP115 answers "module not support") |
| Cloud | `cnCloud.get_info`, `set_server_url`, `bind`/`unbind` | cloud connection, bound account | **shown** (connected / bound) |
| Firmware | `cnCloud.get_intl_fw_list` (+ download/upgrade commands, names unverified) | firmware list / update | **easy** (list), **action** (update) |
| Wi-Fi | `netif.get_scaninfo`, `netif.set_stainfo` | scan networks / join a network | **easy** (scan), **action** (join) |
| Countdown | `count_down.get_rules` / `add_rule` | one-shot timer | not on KP115 in python-kasa (only bulbs/strips); untested |

## Tapo SMART (P110 / P115)

| Area | Method | What it returns / does | KasaMonitor |
|---|---|---|---|
| Identity and state | `get_device_info` | `device_on`, `on_time`, `nickname` (base64), model, `fw_ver`/`hw_ver`, `rssi`/`signal_level`, `ssid` (base64), IP, `region`/`time_diff`, `default_states`, `overheated`, `power_protection_status`, `overcurrent_status`, `auto_off_status`/`auto_off_remain_time` | **used** (discovery, alias); **shown** (all of these) |
| Relay, alias | `set_device_info {device_on}` / `{nickname}` | on/off, rename on the device | **action** |
| Default state after power loss | `set_device_info {default_states: {type: "last_states" \| "custom", state: {on}}}` | on / off / last state | **shown**; **action** to change |
| Live energy | `get_energy_usage`, `get_current_power`, `get_emeter_data` | `current_power` (mW), `today_energy`/`month_energy` (Wh), runtime minutes; `voltage_mv`, `current_ma`, `power_mw` | **used** (every 10 s) |
| Energy history | `get_energy_data {start_timestamp, end_timestamp, interval}` | Wh per hour (60) / day (1440) / month (43200) | **used** (daily, history sync). Not in python-kasa: semantics to be confirmed on the device |
| Usage summary | `get_device_usage` | power, time on and "saved" for today / 7 / 30 days | **easy** |
| Electricity price | `get_electricity_price_config` | price set in the Tapo app | **easy** |
| Auto-off | `get_auto_off_config`, `set_auto_off_config {enable, delay_min}` | turn off N minutes after being turned on | **shown**; **action** to change |
| Countdown | `get_countdown_rules`, add/edit commands (names unverified) | one-shot "turn on/off in N min" | **shown** (enabled, rule count); **action** |
| Schedules | `get_schedule_rules`, `get_next_event` (+ add/edit/remove commands, names unverified) | local schedules, kept on the plug (the P115 has a local `schedule` component and up to 32 rules) | **shown** (count); **action** |
| Away mode | `get_antitheft_rules` (+ add/edit) | random on/off | **easy** |
| Power protection | `get_protection_power`, `get_max_power`, `set_protection_power {enabled, protection_power}` | cut power above N W (max ≈ 3.6–3.9 kW) | **shown**; **action** to change |
| LED | `get_led_info`, `set_led_info {led_rule: always \| never \| auto}` | LED mode, night mode (sunrise/sunset) | **shown**; **action** |
| Time | `get_device_time`, `set_device_time` | timestamp, `time_diff` (min), region | **shown** |
| Cloud | `get_connect_cloud_state` | 0 = connected | **shown** |
| Firmware | `get_latest_fw`, `get_fw_download_state`, `get_auto_update_info`, `set_auto_update_info`, `fw_download` | update available, download progress, auto-update | **easy** (check), **action** (update) |
| Wi-Fi | `get_wireless_scan_info`, `set_qs_info` | scan / join | **easy** / **action** |
| Reboot / reset | `device_reboot`, `device_reset` | restart / factory reset | **action** (reset: deliberately not) |
| Matter / HomeKit | `get_matter_setup_info`, `get_homekit_info` | pairing codes (newer models) | not relevant for P115 |
| Batching | `multipleRequest {requests: […]}` | several methods in one round trip | possible optimisation (we send one method per request) |

## Shelly Gen2 / Gen3

Source: the official Gen2+ API documentation (components `Shelly`, `Sys`, `WiFi`, `Cloud`, `MQTT`, `Switch`, `EM`,
`EMData`, `EM1`, `EM1Data`, `PM1`) and the answers of a Shelly EM Gen3 (`S3EM-002CXCEU`, app `EMG3`, firmware
1.6.99, Gen3) read on 2026-11-01. Each component has `<Type>.GetStatus` / `<Type>.GetConfig` (`?id=N`);
`Shelly.GetStatus` / `Shelly.GetConfig` return all components at once, keyed `type:id` (e.g. `em1:0`).
`Shelly.GetDeviceInfo` never needs authentication. With `auth_en: true` every other call answers 401 with
`WWW-Authenticate: Digest qop="auth", realm="<device id>", nonce=…, algorithm=SHA-256`; the client answers with HTTP
digest (RFC 7616, user `admin`). KasaMonitor calls **only** `Shelly.GetDeviceInfo`, `Shelly.GetStatus`,
`Shelly.GetConfig`, `EM1Data.GetRecords`, `EM1Data.GetData`.

| Area | Request | What it returns / does | KasaMonitor |
|---|---|---|---|
| Identity | `Shelly.GetDeviceInfo` | `id`, `mac`, `model` (S3EM-002CXCEU), `gen`, `ver`, `app` (EMG3), `auth_en`, `name` | **used** (discovery, hourly refresh; model, firmware, gen); **shown** |
| Meter channel (EM Gen3, Pro EM) | `em1:N` in `Shelly.GetStatus` | `voltage` V, `current` A, `act_power` W (signed), `aprt_power` VA, `pf`, `freq` Hz, `calibration`, `errors` | **used** (W, V, A, pf, VA, Hz → metrics); **shown** (pf, VA, Hz, calibration) |
| Meter energy counters | `em1data:N` | `total_act_energy`, `total_act_ret_energy` (Wh, cumulative) | **used** (`kasa_power_total`, `kasa_energy_returned_wh_total`); **shown** |
| Three-phase meter (Pro 3EM) | `em:0`, `emdata:0` | per phase `a_`/`b_`/`c_` voltage, current, act/aprt power, pf, freq; `total_current`, `total_act_power`, `total_aprt_power`; `total_act`, `total_act_ret` | **used** (totals only, one channel; untested). Per-phase values: **easy** |
| Power meter (PM Mini) | `pm1:N` | `voltage`, `current`, `apower`, `freq`, `pf`, `aenergy.total`, `ret_aenergy.total` | **used** (untested) |
| Metered relay (Plus Plug S, 1PM, Pro xPM) | `switch:N` | `output`, `apower`, `voltage`, `current`, `pf`, `freq`, `aenergy.total` (+ `by_minute`), `temperature.tC`, `source` | **used** (untested): W/V/A/pf/Hz/Wh, relay, temperature |
| Relay without metering (EM Gen3) | `switch:0` status / config | `output`, `temperature.tC`; config `initial_state`, `auto_on`, `auto_on_delay`, `auto_off`, `auto_off_delay`, `name` | **used** (`kasa_relay_state` on channel 0, temperature); **shown** (default state, auto on/off) |
| Meter config | `em1:N` in `Shelly.GetConfig` | `name`, `reverse` (CT direction), `ct_type` (e.g. `50AEMG3`) | **used** (alias from `name`); **shown** (CT type, reversed) |
| Per-minute energy history | `EM1Data.GetRecords?id=N` | `data_blocks[{ts, period, records}]` (what the device keeps) | **used** (start of the first sync) |
| | `EM1Data.GetData?id=N&ts=&end_ts=` | `keys[]` + `data[{ts, period, values[[…]]}]`, one row per period (60 s): energy of that minute (`total_act_energy`, `total_act_ret_energy`, reactive energies), min/max/avg power, voltage, current; `next_record_ts` when more pages exist | **used** (daily kWh, one request per local day). Min/max/avg per minute: **easy** |
| Energy history (Pro 3EM) | `EMData.GetRecords`, `EMData.GetData` | same shape, three-phase keys | **easy** (not read: daily kWh comes from our own readings) |
| CSV export | `/emdata/0/data.csv`, `/em1data/N/data.csv` (HTTP) | the same records as CSV | not used |
| System | `sys` in `Shelly.GetStatus` | `uptime`, `time`, `unixtime`, `utc_offset`, `available_updates.stable.version` (and `beta`), `restart_required`, `ram_*`, `fs_*`, `cfg_rev`, `reset_reason` | **shown** (uptime, update available, device time, restart required). RAM/FS: **easy** |
| Location / time zone | `sys.location` in `Shelly.GetConfig` | `tz`, `lat`, `lon` | **shown** (time zone) |
| Network | `wifi` status | `sta_ip`, `status`, `ssid`, `rssi` | **shown**; `rssi` also in `PlugInfo` |
| Cloud / MQTT / WebSocket | `cloud.connected`, `mqtt.connected`, `ws.connected` | connection state | **shown** (cloud, MQTT) |
| Bluetooth | `ble`, `bthome` | BLE gateway / BTHome state | not relevant |
| Relay control | `Switch.Set`, `Switch.Toggle` | switch the relay | **action** (deliberately not: KasaMonitor is read-only) |
| Configuration | `EM1.SetConfig` (`reverse`, `name`, `ct_type`), `Switch.SetConfig`, `Sys.SetConfig` (time zone, name), `WiFi.SetConfig`, `Cloud.SetConfig`, `MQTT.SetConfig` | change settings | **action** (not implemented; e.g. fixing a wrong `sys.location.tz` or a reversed CT is done in the Shelly app / web UI) |
| Counters / data | `EM1Data.ResetCounters`, `EM1Data.DeleteAllData`, `EMData.*` equivalents | reset energy totals / delete history | **action** (deliberately not) |
| Maintenance | `Shelly.Reboot`, `Shelly.Update`, `Shelly.FactoryReset`, `Shelly.SetAuth` | reboot, firmware update, reset, set password | **action** (deliberately not) |
| Scripts / webhooks / schedules | `Script.*`, `Webhook.*`, `Schedule.*` | on-device automation | not used |

Notes:

- A device with `auth_en: true` and no password in `/settings` is listed as `nesuportat: parolă Shelly lipsă`; a
  rejected password as `autentificare Shelly eșuată`. The digest challenge is cached per device (nonce count
  incremented), so a steady poll costs one request; a stale nonce costs one extra 401.
- Time stamps of `EM1Data` records are the device's own clock. A device whose clock jumps (e.g. right after a time
  zone change, or before the first SNTP sync) writes records at wrong times; KasaMonitor drops records dated after
  today and sums the rest by the application's local day.
- Discovery: Gen2+ devices announce `_shelly._tcp.local` with TXT `gen`, `app`, `ver`; `sys.device.discoverable =
  false` hides them from mDNS — use *Shelly probe hosts* or add by IP.

## Limitations

- **TPAP**: firmware from 2025 on can switch the plug to the TPAP protocol (SPAKE2+ handshake). It is not supported by KasaMonitor or by python-kasa mainline. Fix on the plug: Tapo app → Me → Third-Party Services → *Third-Party Compatibility* (account-level): turn it off, wait ~10 s, turn it on again; the plug switches to KLAP within ~30 s and is re-classified at the next scan. Restarting the plug alone does not help.
- **Kasa on KLAP**: Kasa devices with new firmware that speak KLAP v1 instead of port 9999 are listed as unsupported (the v1 hash is implemented, the transport is not wired).
- **Credentials**: every Tapo request needs the TP-Link account; a wrong password makes the plug refuse the handshake. Some firmware rate-limits repeated failed logins.
- **No push**: neither API sends events; everything is polled. Schedules and timers run on the plug itself and keep working without KasaMonitor or the cloud.
- **Tapo history**: `month_energy` resets monthly. The daily history comes from `get_energy_data`, which python-kasa does not use, so its exact window rules are to be confirmed.
- **Writes**: no write command is implemented yet. Each one in the "action" rows needs a confirmation in the UI and an audit line in the log before it is added.
