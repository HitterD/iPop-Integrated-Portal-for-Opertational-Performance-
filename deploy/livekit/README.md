# LiveKit deployment (iPop calls)

Copy this folder to the LiveKit server (Windows + Docker), then run in an **Administrator** PowerShell:

```powershell
.\setup.ps1 -NodeIp <server-ip-reachable-by-users> -PublicDomain <npm-domain>
```

Example: `.\setup.ps1 -NodeIp 10.20.1.5 -PublicDomain ipop.site-b.co.id`

The script:
1. Creates `.env` with a random API secret (it reuses the existing one if you run it again).
2. Writes `livekit.yaml` with `node_ip`.
3. Opens Windows Firewall: TCP 7880, 7881 and UDP 3478, 50000-50050 (`-SkipFirewall` to skip this).
4. Runs the container and prints the values to put into the API config.

## After running

- **API**: set `Calls__LiveKit__Url`, `Calls__LiveKit__ApiKey`, and `Calls__LiveKit__ApiSecret` to the values the script prints, then restart the API.
- **NPM**: add Custom Location `/rtc` → `http://<NodeIp>:7880` and enable Websockets Support.
- **Network/router**: users must be able to reach `<NodeIp>` directly on TCP 7881, UDP 3478, and UDP 50000-50050. These ports can't go through NPM.

## Troubleshooting

- Logs: `docker logs ipop-livekit`
- Call connects but has no audio/video: `node_ip` is wrong, or the UDP ports are blocked.
- `.env` contains the secret. Don't commit it or share it.

Linux server: run `docker compose up -d` after creating `.env` and `livekit.yaml` manually (same format), and open the ports with `ufw`.
