# LiveKit deployment (iPop calls and meeting recordings)

Copy this folder to the LiveKit server (Windows + Docker), then run in an **Administrator** PowerShell:

```powershell
.\setup.ps1 -NodeIp <server-ip-reachable-by-users> -PublicDomain <npm-domain>
```

Example: `.\setup.ps1 -NodeIp 10.10.6.13 -PublicDomain ipop.santos.co.id -SkipFirewall`

## Containers and generated configuration

Compose project `ipop-livekit` contains three containers:

| Service / container | Purpose |
| --- | --- |
| `redis` / `ipop-livekit-redis` | Dedicated ephemeral Redis shared by LiveKit and Egress; persistence disabled and no published ports. Separate from the SignalR Redis. |
| `livekit` / `ipop-livekit` | Meeting media and server APIs; TCP 7880, 7881, UDP 3478 and 50000–50050. |
| `egress` / `ipop-livekit-egress` | Room-composite MP4 recording; uses `SYS_ADMIN` and mounts the recordings spool at `/out`. |

The script:
1. Creates `.env` with a random API secret, reusing the existing key and secret on subsequent runs (quoted and unquoted `LIVEKIT_KEYS` are supported).
2. Creates the host spool folder, default `F:\ipop-data\recordings-spool`; override with `-SpoolDir`. Writes `RECORDINGS_SPOOL` to `.env` with forward slashes for the Docker bind mount.
3. Writes `livekit.yaml` with `node_ip`, UDP range 50000–50050 and shared Redis address `redis:6379`.
4. Writes `egress.yaml` from `egress.yaml.template`, replacing the key/secret placeholders. Egress connects internally to `ws://livekit:7880` and `redis:6379`.
5. Opens Windows Firewall: TCP 7880, 7881 and UDP 3478, 50000–50050 (`-SkipFirewall` to skip).
6. Starts all three containers and prints the API configuration values.

Generated `.env`, `livekit.yaml` and `egress.yaml` are git-ignored. Both `.env` and `egress.yaml` contain credentials: do not commit or share them. Generated configuration is written as UTF-8 **without BOM**, including on PowerShell 5.1.

## API configuration

**The API must run on the same host as Egress**, because its recording worker reads MP4 files directly from the host spool and uploads them to NAS. The API account must have read/write access to the spool and recording cache and the configured NAS service account must be able to upload recordings.

| Key | Value / default |
| --- | --- |
| `Calls:LiveKit:Url` | Browser-reachable WebSocket URL, e.g. `ws://10.10.6.13:7880` on a LAN or the externally routed `wss://` URL. Remains the URL returned to browsers joining rooms. |
| `Calls:LiveKit:ServerUrl` | Optional server-to-server RoomService/Egress Twirp URL. On this host use `http://127.0.0.1:7880`: Docker ports cannot be reached through the host's own LAN IP (hairpin). When unset or blank, falls back to `Calls:LiveKit:Url`; WebSocket schemes are converted to HTTP(S) for Twirp. Does not change browser URLs or token grants. |
| `Calls:LiveKit:ApiKey` | Key generated/reused by setup. |
| `Calls:LiveKit:ApiSecret` | Secret generated/reused by setup. |
| `MeetingRooms:DeploymentMode` | `Production` or `Isolated` (default `Isolated`). Independent of `ASPNETCORE_ENVIRONMENT`: the production API uses `Development`, so it must explicitly set `Production`. |
| `MeetingRooms:MediaNamespace` | Default `hall`; v2 names media `{namespace}-{roomKey}-{meetingId:N}`. Isolated deployments must explicitly use a distinct value, e.g. `qa-hall`. Legacy `hall-{roomKey}` is not managed by v2. |
| `MeetingRooms:BookingWorkerEnabled` | Default true; Isolated deployments must explicitly choose true/false. False disables booking enforcement/metadata/chat cleanup, not recording finish/purge. |
| `MeetingRooms:SpoolDir` | Host spool path, default `F:\ipop-data\recordings-spool`. Must match `-SpoolDir` / `RECORDINGS_SPOOL`. |
| `MeetingRooms:EgressOutDir` | Container-side output directory, default `/out`; must match the bind mount target. |
| `MeetingRooms:NasFolder` | NAS upload root, default `/sja/SJA SPJ/ICT/recordings`. |
| `MeetingRooms:CacheDir` | Local playback cache. Defaults to a sibling `recordings-cache` folder beside the spool, normally `F:\ipop-data\recordings-cache`. |

Set these in `appsettings.Local.json` or as environment variables (replace `:` with `__`, e.g. `Calls__LiveKit__ServerUrl` and `MeetingRooms__SpoolDir`), then restart the API.

## v2 release / isolation checklist

- Review and apply the ownership-ledger migration only during an explicitly authorized release, after DB backup; startup then backfills eligible legacy booking passwords. Do not drop the ledger while v2 serves traffic.
- Set `MeetingRooms:DeploymentMode=Production` explicitly on the real production API, regardless of ASP.NET environment. This permits the production namespace and filesystem defaults; no deployment is authorized by this document.
- Every QA/staging API must use `DeploymentMode=Isolated` (also the default), a non-`hall` namespace, explicit `BookingWorkerEnabled`, and **explicit distinct NAS, spool and cache roots**. Missing local roots, default production roots, equal/nested local roots and roots overlapping the known production spool/cache are rejected before serving. Configure any additional production/custom roots operationally so QA cannot access them; prefer separate least-privilege credentials.
- Example isolated roots: NAS `/sja/SJA SPJ/ICT/recordings-qa`, spool `F:/ipop-qa/recordings-spool`, cache `F:/ipop-qa/recordings-cache`. The Egress mount must match that spool; changing the mount/shared stack requires separate approval.
- Copied NAS paths outside the configured NAS root are quarantined: no delete, download, cached playback, list or new ticket. Rejected deletion keeps NasPath for operator investigation; no public special error discloses another environment's path.
- Drain legacy physical-room sessions/recordings before authorized v2 cutover. Future sja-hall must use v2 booking-specific admission/token names.
- Egress reconciliation requires authoritative `ListEgress(room_name)` items with `egress_id`, `room_name`, status. After a lost start response, grace expires then room jobs are stopped and re-listed; unknown responses/timeouts remain fail-closed and retry. Ordinary user Stop still marks Processing after acceptance; successor admission independently demands terminal confirmation.
- Polling still cannot detect empty/rejoin entirely between samples. Full lifecycle closure requires authenticated ordered/idempotent LiveKit webhooks and separately approved delivery configuration.

## Network and troubleshooting

- **NPM**: add Custom Location `/rtc` → `http://<NodeIp>:7880` and enable Websockets Support; ensure the browser URL corresponds to the route exposed by the proxy.
- **Network/router**: users must reach `<NodeIp>` directly on TCP 7881, UDP 3478 and UDP 50000–50050. These media ports cannot go through NPM.
- Logs: `docker logs ipop-livekit`, `docker logs ipop-livekit-egress`, `docker logs ipop-livekit-redis`.
- Call connects but has no audio/video: `node_ip` is wrong or UDP ports are blocked.
- Server room/recording requests time out: configure `Calls:LiveKit:ServerUrl=http://127.0.0.1:7880` on this host, without changing the browser URL.
- Recording never reaches NAS: check Egress logs, spool mount/path parity, API filesystem permissions and NAS service-account access.

Linux server: create `.env` (`LIVEKIT_KEYS` and `RECORDINGS_SPOOL`), `livekit.yaml` and `egress.yaml` manually with the same configuration, then run `docker compose up -d` and open the media ports with the host firewall. Set `MeetingRooms:SpoolDir` to that host's spool path.

## Notes from the first Egress rollout (2026-10-12)

- `rtc.advertise_internal_ip: true` is required: without it Egress (inside the Docker network) only gets the LAN `node_ip` candidate and every server recording aborts.
- `.env`, `egress.yaml` and the API's `Calls:LiveKit:ApiSecret` must hold the **same** secret. A mismatch shows up as HTTP 401 from `/twirp/...` in the API log.
- On the Docker host itself, published ports answer on `127.0.0.1` but not on the LAN IP; set `Calls:LiveKit:ServerUrl=http://127.0.0.1:7880` for an API running on that host.
