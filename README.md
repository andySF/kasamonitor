# KasaMonitor

Monitorizare pentru prizele TP-Link cu contor de energie: Kasa (KP115, protocol legacy XOR pe 9999) și Tapo
(P110/P115, protocoalele KLAP și AES „securePassthrough”). Aplicație Blazor Server (.NET 10) cu SQLite, interfață web pe portul 8910 și
`/metrics` pentru Prometheus.

Versiune: **0.2.0** (`Directory.Build.props`).

## Ce face

- **Descoperire automată** la fiecare N minute: broadcast UDP 9999 (Kasa legacy) și UDP 20002 (sonda TDP
  folosită de Tapo / Kasa noi). Prizele noi se adaugă automat în baza de date (dacă „Adaugă automat” e pornit).
- **Scanează acum** (`/plugs`): rulează descoperirea imediat și arată fiecare priză găsită ca `nouă`,
  `nouă (adăugată)`, `cunoscută` sau `nesuportat: <model>, <motiv>`; cu adăugarea automată oprită, prizele noi
  se adaugă cu un click („Adaugă”).
- **Adaugă după IP** (`/plugs`): încearcă întâi legacy (TCP 9999), apoi sonda Tapo pe UDP 20002 + KLAP, apoi KLAP
  direct, apoi AES direct (dacă sunt setate credențialele TP-Link). Util pentru prize din alt subnet, unde broadcastul nu ajunge.
- **Retrage / Readu** (`/plugs`): o priză retrasă nu mai e interogată și dispare din `/metrics` și din
  Dashboard (fără repornire). Bifa „Arată retrase” o face din nou vizibilă în listă; „Readu” o reactivează.
  (Folosește coloana existentă `IsArchived`.)
- **Prize nesuportate** (răspund la descoperire, dar nu pot fi interogate) apar în listă cu IP/MAC și motivul:
  - `lipsesc credențialele TP-Link` – priză Tapo pe KLAP/AES, dar contul TP-Link nu e setat în `/settings`;
  - `autentificare eșuată (...)` – credențiale greșite;
  - `protocol TPAP` / ... – protocol pe care aplicația nu îl vorbește. Hint afișat în UI:
    „În aplicația Tapo: Setări dispozitiv → Third-Party Compatibility: oprit, apoi pornit, și repornește priza;
    de obicei revine pe KLAP.”
  - `KLAP pe Kasa (IOT), neimplementat` – priză Kasa cu firmware nou pe KLAP v1.

  Când motivul dispare (ex. ai pus credențialele), următoarea descoperire o trece automat pe „activă”.

## Setări

Setările de descoperire și contul TP-Link se editează în `/settings`, se salvează în tabela `Settings` din SQLite
și se aplică fără repornire. Variabilele de mediu sunt doar **valori inițiale**: se folosesc cât timp cheia nu a
fost salvată din `/settings`.

| Setare (`/settings`) | Variabilă de mediu (inițială) | Implicit | Note |
|---|---|---|---|
| Descoperire automată | `KasaMonitor__AutoDiscoveryEnabled` | `true` | |
| Interval descoperire (minute) | `KasaMonitor__AutoDiscoveryIntervalMinutes` | `5` | 1–1440 |
| Rețele broadcast | `KasaMonitor__BroadcastNetworks` | gol → `255.255.255.255` | IPv4 separate prin virgulă; valoare goală în DB = se folosește env |
| Adaugă automat prizele găsite | `KasaMonitor__AutoAddDiscoveredPlugs` | `true` | |
| Utilizator TP-Link | `KasaMonitor__TapoUsername` | — | e-mailul contului din aplicația Tapo |
| Parolă TP-Link | `KasaMonitor__TapoPassword` | — | în DB criptată cu ASP.NET DataProtection; afișată mascată; aceeași pentru KLAP și AES |
| Tarif (RON/kWh) | `KasaMonitor__TariffRonPerKwh` | `1.30` | citit la pornire din env |
| Interval interogare (s) | `KasaMonitor__PollIntervalSeconds` | `10` | citit la pornire din env |
| — | `KasaMonitor__DbPath` | `/data/kasa.db` (imagine) | |

Cheile DataProtection stau lângă baza de date, în `<dir DB>/dp-keys` (în container `/data/dp-keys`, deci pe
volumul persistent). Dacă directorul se pierde, parola salvată nu mai poate fi decriptată: se reintroduce în
`/settings` (în log apare `Stored secret could not be decrypted`). Pe Linux cheile sunt în clar pe disc
(ASP.NET loghează la pornire avertismentul „No XML encryptor configured”) — protecția e la nivelul volumului.

Nu pune credențiale în `docker-compose.yml` din repo; folosește `/settings` sau variabile setate doar în stack-ul
Portainer.

## Tapo / KLAP

Implementare proprie (fără pachet NuGet), după python-kasa `transports/klaptransport.py` (`KlapTransportV2`) și
`protocols/smartprotocol.py`:

1. Descoperire: datagramă TDP pe UDP 20002 (antet 16 octeți `>BBHHBBII`, CRC32; se trimit ambele variante, cea
   simplă de 16 octeți și cea cu cheie RSA). Răspunsul JSON dă `device_type`, `device_model`, `mac`, `ip`,
   `mgt_encrypt_schm.encrypt_type` (`KLAP`, `AES`, `TPAP`, ...), `http_port`, `lv`.
2. Handshake: `POST http://<ip>/app/handshake1` cu `local_seed` (16 octeți) → `remote_seed` (16) + hash (32) și
   cookie `TP_SESSIONID`; `auth_hash = sha256(sha1(user) + sha1(parolă))` (v2; v1 = `md5(md5(user)+md5(parolă))`
   e implementat dar folosit doar pentru Kasa IOT, nesuportat încă). Verificare `sha256(local + remote + auth)`,
   apoi `POST /app/handshake2` cu `sha256(remote + local + auth)`.
3. Cereri: `POST /app/request?seq=N`, AES-128-CBC cu cheie/IV/semnătură derivate din seed-uri
   (`lsk`/`iv`/`ldk`), corp = `sha256(sig + seq + cipher) + cipher`. Sesiunea se refolosește per priză și se
   renegociază la eroare.
4. Metode: `get_device_info` (alias = `nickname` base64, model, firmware), `get_energy_usage`
   (`current_power` mW, `month_energy` Wh) și `get_emeter_data` (`voltage_mv`, `current_ma`). Dacă priza nu
   răspunde la `get_emeter_data`, tensiunea și curentul lipsesc pentru ea din `/metrics`.

### AES („securePassthrough”, firmware Tapo mai vechi)

După python-kasa `transports/aestransport.py` (`AesTransport`): `POST http://<ip>/app`
`{"method":"handshake","params":{"key":"<cheie publică RSA-1024, PEM>"}}` → `result.key` (base64, criptat RSA
PKCS#1 v1.5) = cheie AES (16 B) + IV (16 B), cookie `TP_SESSIONID`. Apoi `login_device` prin
`{"method":"securePassthrough","params":{"request":"<base64 AES-CBC>"}}`: `username = base64(hex(sha1(email)))`;
login v2 `password2 = base64(hex(sha1(parolă)))`, v1 `password = base64(parolă)` → `result.token`. Cererile
următoare merg la `/app?token=<token>`. Versiunea de login nu se salvează per priză: se încearcă v2, apoi v1, și se
reține per priză cea care a mers. Aceleași metode SMART ca la KLAP.

Selecția transportului după `mgt_encrypt_schm.encrypt_type` din descoperire: `KLAP` → `klap`, `AES` → `aes`,
altceva (TPAP etc.) → nesuportat cu hint. Transportul e o interfață per protocol (`ISmartTransport`); TPAP (SPAKE2+)
se poate adăuga ca altă implementare. Istoricul zilnic/lunar (`HistorySyncWorker`) există doar pentru prizele legacy.

## Metrici (`/metrics`)

Toate seriile au etichetele `alias`, `mac`, `model` (ex. `KP115`, `P115`; fără sufixul de regiune) și
`protocol` (`legacy` | `klap` | `aes`).

| Metrică | Tip | Sursă legacy | Sursă Tapo (KLAP/AES) |
|---|---|---|---|
| `kasa_online` | gauge | 1/0 după ultimul poll | idem |
| `kasa_power_load` | gauge, W | `emeter.get_realtime.power_mw` | `get_energy_usage.current_power` |
| `kasa_voltage` | gauge, V | `voltage_mv` | `get_emeter_data.voltage_mv` (lipsește dacă nu e raportat) |
| `kasa_current` | gauge, A | `current_ma` | `get_emeter_data.current_ma` (lipsește dacă nu e raportat) |
| `kasa_relay_state` | gauge | 1 când poll-ul reușește | idem |
| `kasa_power_total` | counter, Wh | `total_wh` (cumulat pe priză) | `month_energy` (resetat lunar de priză; contorul Prometheus ignoră scăderea, deci rămâne monoton) |
| `kasa_rssi` | gauge | declarată, nesetată | idem |

Prizele retrase sau nesuportate nu au serii. Redenumirea unei prize șterge seriile cu aliasul vechi. Dashboard-ul
Grafana existent (`{{alias}}`) funcționează neschimbat; etichetele noi creează serii noi în Prometheus (cele vechi,
fără `model`/`protocol`, devin „stale” după update).

## Loguri

- `Discovery on <rețele>: found N, new N, unsupported N (auto-add on|off)` – Information, la fiecare scanare.
- `Discovered new plug <mac> (<alias>, <model>, <protocol>) at <ip> -> Id <id>` – Information.
- `Poll failed for plug <alias> (<ip>, <protocol>), N consecutive failures: <motiv>` – Warning, prima dată, apoi
  după încă 10 eșecuri consecutive sau după 10 minute (ce vine întâi), per priză.
- `Plug <alias> (<ip>) is responding again after N failed polls` – Information.
- `History sync failed for plug <alias> (<ip>, <id>)` – Warning.

## Build și teste

```powershell
dotnet build KasaMonitor.sln
dotnet test KasaMonitor.sln
```

## Imagine Docker (Docker Hub `duobit/kasamonitor`)

Din rădăcina repo-ului, cu Docker Desktop pornit și `docker login` făcut o dată:

```powershell
.\deploy\build-and-push.ps1 -Version 0.2.0          # build :0.2.0 + :latest și push pe ambele
.\deploy\build-and-push.ps1 -Version 0.2.0 -NoPush  # doar build local
```

Echivalent manual:

```powershell
docker build -t duobit/kasamonitor:0.2.0 -t duobit/kasamonitor:latest -f deploy/Dockerfile .
docker push duobit/kasamonitor:0.2.0
docker push duobit/kasamonitor:latest
```

## Update pe QNAP (Portainer, stack 28)

1. Backup la baza de date înainte de prima pornire cu 0.2.0 (migrarea `AddPlugProtocol` adaugă coloanele
   `Plugs.Protocol` și `Plugs.UnsupportedReason`; datele existente rămân, prizele existente devin `legacy`):
   copie a `/share/Container/kasamonitor/data/kasa.db` (cu containerul oprit, sau `sqlite3 .backup`).
2. Portainer → Stacks → stack-ul KasaMonitor (id 28) → Editor → „Update the stack” cu
   **Re-pull image and redeploy** bifat (sau `docker compose pull && docker compose up -d` în directorul
   stack-ului).
3. Verificare: `http://<qnap>:8910/health`, `/plugs` (Scanează acum), `curl -s http://<qnap>:8910/metrics | grep kasa_online`,
   `docker logs kasamonitor | grep -E "Discovery on|Poll failed"`.
4. Pentru Tapo: `/settings` → cont TP-Link (utilizator + parolă) → Salvează → `/plugs` → Scanează acum.

Vezi și `deploy/README.md` (compose, troubleshooting).
