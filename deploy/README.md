# KasaMonitor — Deployment

Main documentation (settings, Tapo, metrics, Prometheus & Grafana): `../README.md`. The web UI page
`/integration` generates the Prometheus/Grafana configuration below with the real KasaMonitor address.

The monitoring files (`docker-compose.monitoring.yml`, `prometheus/`, `grafana/provisioning/`) are embedded in
`KasaMonitor.Web.dll` (`<EmbeddedResource>` in `src/KasaMonitor.Web/KasaMonitor.Web.csproj`; the `Dockerfile` copies
them into the build stage) and served by `/integration`. Edit them here; the next build picks them up.

## Files

| File | Purpose |
|---|---|
| `docker-compose.yml` | KasaMonitor (host networking, port 8910, volume `./data:/data`) |
| `docker-compose.monitoring.yml` | optional Prometheus + Grafana |
| `prometheus/prometheus.yml` | scrape job `kasa` |
| `grafana/provisioning/` | data source, dashboard `KasaMonitor`, alert rules |
| `Dockerfile`, `build-and-push.ps1` | image build (`duobit/kasamonitor`) |

## Run

```bash
docker compose -f deploy/docker-compose.yml up -d
docker compose -f deploy/docker-compose.monitoring.yml up -d   # optional
```

Wait ~30 s for the first discovery and polling cycle, then browse to `http://<host>:8910/`.

Portainer / NAS (QNAP Container Station, Synology, ...): create a stack from `docker-compose.yml`; replace the
relative volume `./data` with an absolute path on the NAS (e.g. `/share/Container/kasamonitor/data`) because the
stack has no repository directory. Keep `network_mode: host`.

## Local build & push to Docker Hub

```powershell
docker build -t duobit/kasamonitor:latest -t duobit/kasamonitor:0.3.0 -f deploy/Dockerfile .
docker login
docker push duobit/kasamonitor:latest
docker push duobit/kasamonitor:0.3.0
```

## Configuration

`KasaMonitor__*` environment variables are read on startup; the DB at `/data/kasa.db` persists across container
restarts. Since 0.2.0 the discovery settings and the TP-Link account are edited in `/settings` and stored in the DB;
the environment variables are only initial values (full list in `../README.md`).

| Var | Default | Notes |
|---|---|---|
| `KasaMonitor__BroadcastNetworks` | (empty → 255.255.255.255) | comma-separated IPv4 broadcast addresses, e.g. `192.168.1.255` |
| `KasaMonitor__TariffRonPerKwh` | `1.30` | price per kWh in the web UI |
| `KasaMonitor__PollIntervalSeconds` | `10` | plug poll interval |
| `KasaMonitor__DbPath` | `/data/kasa.db` | SQLite file location |
| `KasaMonitor__TapoUsername` / `KasaMonitor__TapoPassword` | — | TP-Link account for Tapo plugs (better: `/settings`) |
| `KasaMonitor__ShellyDiscoveryEnabled` | `true` | Shelly Gen2/Gen3 discovery (mDNS `_shelly._tcp`) |
| `KasaMonitor__ShellyPassword` | — | admin password of Shelly devices with authentication (better: `/settings`) |
| `KasaMonitor__ShellyProbeHosts` | — | IPs / ranges / CIDR probed with `Shelly.GetDeviceInfo` (mDNS fallback) |

## Troubleshooting

- **No plugs discovered** → confirm `network_mode: host` is set (some NAS UIs reset it). Without host networking the
  UDP broadcast cannot reach the LAN. Check that `BroadcastNetworks` is your LAN's broadcast address; for plugs in
  another subnet use "Add by IP" on `/plugs`.
- **Tapo plug "unsupported"** → see the reason on `/plugs`: missing/wrong TP-Link credentials, or a protocol such
  as TPAP (toggle *Third-Party Compatibility* in the Tapo app and restart the plug).
- **Prometheus target `kasa` DOWN** → see "Reaching KasaMonitor from Prometheus" in `../README.md`
  (`host.docker.internal` vs. the host's LAN IP).
- **Database locked errors** → another container instance may be running against the same volume. Stop all but one.
- **Health endpoint 503** → check container logs; usually means EF migration failed at startup.
- **Healthcheck failing** → the container's HEALTHCHECK runs `dotnet KasaMonitor.Web.dll --healthcheck` which probes
  `http://localhost:8910/health`. If you changed the port via `ASPNETCORE_URLS`, the healthcheck won't match.

## Build verification

The Dockerfile uses `mcr.microsoft.com/dotnet/sdk:10.0` for the build stage and `mcr.microsoft.com/dotnet/aspnet:10.0`
for the runtime stage. Final image size: ~120 MB. Built for `linux/amd64` only — multi-arch (arm64) is out of scope.
