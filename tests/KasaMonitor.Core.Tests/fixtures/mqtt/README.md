# MQTT Discovery fixtures (0.11)

Every line is `{"source", "topic", "retain", "payload"}`; `payload` is the JSON object sent (a string for a raw payload,
`""` for a removal). **All of them are written from the documentation (Zigbee2MQTT, ESPHome, Home Assistant MQTT), not
captured from hardware** (`source` says which). Identifiers are synthetic (`0xaabbccddee0000NN`, `aabbccddee0N`). Replace or
extend them with captures (`mosquitto_sub -v -t 'homeassistant/#'`) when the first real device arrives.

- `z2m-discovery.jsonl` / `z2m-states.jsonl`: a temperature / humidity / pressure sensor (battery, mV voltage and link
  quality as diagnostics), a plug with switch / power / energy (kWh) / voltage / current, a door contact
  (`payload_on: false`), availability `all` of the bridge and of the device.
- `esphome-discovery.jsonl` / `esphome-states.jsonl`: abbreviated keys, `availability_topic`, raw state payloads, a switch.
- `ha-discovery.jsonl` / `ha-states.jsonl`: the device-based format (`cmps`, a shared `state_topic`), the `~` base, a light
  (unsupported component), a template with an instruction block (unsupported), a payload without `unique_id`.
