# KasaMonitor — Deployment

## Local build & push to Docker Hub

```powershell
docker build -t duobit/kasamonitor:latest -t duobit/kasamonitor:0.1.0 -f deploy/Dockerfile .
docker login
docker push duobit/kasamonitor:latest
docker push duobit/kasamonitor:0.1.0
```

## QNAP / Portainer deployment

1. SSH to QNAP, ensure `/share/Container/kasamonitor/data` exists.
2. In Portainer → Stacks → Add stack → paste contents of `docker-compose.yml`.
3. Deploy. Wait ~30s for discovery + first polling cycle.
4. Browse to `http://<qnap-ip>:8910/`.

## Configuration overrides

All `KasaMonitor__*` env vars are read on startup. The DB at `/data/kasa.db` persists across container restarts.

| Var | Default | Notes |
|---|---|---|
| `KasaMonitor__BroadcastNetworks` | (empty → 255.255.255.255) | Comma-separated IPv4 broadcast addrs |
| `KasaMonitor__TariffRonPerKwh` | `1.30` | Cost calculation rate |
| `KasaMonitor__PollIntervalSeconds` | `10` | Plug poll interval |
| `KasaMonitor__DbPath` | `/data/kasa.db` | SQLite file location |

## Troubleshooting

- **No plugs discovered** → confirm `network_mode: host` is set; Container Station sometimes resets it. Without host networking, the UDP broadcast can't reach the LAN.
- **Database locked errors** → another container instance may be running against the same volume. Stop all but one.
- **Health endpoint 503** → check container logs; usually means EF migration failed at startup.
- **Healthcheck failing** → the container's HEALTHCHECK runs `dotnet KasaMonitor.Web.dll --healthcheck` which probes `http://localhost:8910/health`. If you changed the port via `ASPNETCORE_URLS`, the healthcheck won't match.

## Build verification

The Dockerfile uses `mcr.microsoft.com/dotnet/sdk:10.0` for the build stage and `mcr.microsoft.com/dotnet/aspnet:10.0` for the runtime stage. Final image size: ~120 MB. Built for `linux/amd64` only — multi-arch (arm64) is out of scope.
