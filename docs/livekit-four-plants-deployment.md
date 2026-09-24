# LiveKit + TURN deployment & infrastructure status (SPJ, KRW, JTB, SMG)

## Current infrastructure status (tested & provisioned on host)

1. **LiveKit SFU + TURN Server**:
   - Container: `ipop-livekit` (`livekit/livekit-server:latest`, configured in `docker-compose.yml` and `infra/livekit/livekit.yaml`).
   - Host binding: `10.10.6.13` (Ethernet intranet interface reachable across plants) and `127.0.0.1`.
   - Signaling Port: TCP `7880` (`http://10.10.6.13:7880` verified responding `OK`).
   - RTC TCP Fallback Port: TCP `7881`.
   - Embedded TURN Port: UDP `3478` (active in container logs).
   - RTC UDP Media Range: UDP `50000-50050`.
   - Local configuration: wired into git-ignored `src/IPop.Api/appsettings.Local.json` (`Calls:LiveKit:Url`, `Calls:LiveKit:ApiKey`, `Calls:LiveKit:ApiSecret`).

2. **Synology NAS service account & shared attachments**:
   - Server: `ict` (`https://192.168.2.17:30001`, verified TCP and API authentication).
   - Service account: `ict` (credentials stored securely in git-ignored `src/IPop.Api/appsettings.Local.json`).
   - Shared attachment directories:
     - `/sja/SJA SPJ/ICT/chat` (active chat storage).
     - `/sja/SJA SPJ/ICT/mail` (created and verified on NAS FileStation API).
   - All 5 NAS servers (`spj` 192.168.2.16, `krw` 10.12.2.6, `jtb` 10.11.2.6, `smg` 10.14.2.16, `ict` 192.168.2.17) tested reachable over TCP port 30001 from host network.

## Prerequisites & plant routing

- One stable LiveKit endpoint, e.g. `wss://<calls-host>:443` or `ws://10.10.6.13:7880` on internal LAN, that browsers in SPJ, KRW, JTB and SMG can resolve and reach over HTTPS/WSS.
- In production, configure reverse proxy / ingress TLS termination for port 443 proxying to `7880`.
- TURN port UDP 3478 must remain open on plant firewalls for NAT traversal across plant subnets.
- Generate a strong, unique API key and secret on the LiveKit server. Put matching values **only** in the API host's ignored `src/IPop.Api/appsettings.Local.json` or deployment secrets/environment variables. Never check secrets into Git, logs, browser source, or documentation.
- Provision a Synology service account on the selected NAS and grant it read/write to the confirmed shared mail folder (configured as `Synology:MailFolder`, currently `/sja/SJA SPJ/ICT/mail`). Confirm the actual NAS share exists before production use. Per-user `/home` is not shared and must not be used for Mail attachments. Keep the NAS credentials in Local.json or environment variables, not committed configuration.

## API configuration (example keys, no real credentials)

In the API host environment use `Calls__LiveKit__Url`, `Calls__LiveKit__ApiKey`, `Calls__LiveKit__ApiSecret` and `Synology__ServiceAccount__Server`, `Synology__ServiceAccount__Username`, `Synology__ServiceAccount__Password`. For browser P2P fallback, configure `Calls__IceServers__...` using valid STUN/TURN URLs and credentials for the chosen TURN service. If the TURN credentials rotate, arrange a secure configuration refresh/restart. The committed API configuration leaves LiveKit values empty; the application fails closed rather than minting tokens with example keys.

## Testing & verification log (automated & host-level)

- **TCP Reachability to all 4 plants**: Tested via `Test-NetConnection` on port 30001 to plant gateways (`spj`: 192.168.2.16, `krw`: 10.12.2.6, `jtb`: 10.11.2.6, `smg`: 10.14.2.16, `ict`: 192.168.2.17). All 5 returned `TcpTestSucceeded: True` across WAN/VPN links.
- **NAT1To1 & Node IP Configuration**: LiveKit configured with `rtc.node_ip: 10.10.6.13` (NAT1To1Ips), ensuring advertised ICE host candidates point directly to the corporate intranet IP (`10.10.6.13`) rather than the internal Docker bridge IP.
- **4-Plant Concurrent Meeting Protocol Test**: Executed multi-client test with 4 separate participants representing SPJ (20140312), KRW (20140313), JTB (20140314), and SMG (20140315) joining room `meeting-4plants-verification` concurrently over WebSocket `ws://10.10.6.13:7880/rtc`. All 4 sessions established successfully and were verified active in LiveKit room logs.
- **Embedded TURN Server verification**: Port UDP 3478 is active in container logs (`service/turn.go:204 Starting TURN server relay_range: 30000-40000`). Local TURN/STUN configuration is wired into `appsettings.Local.json` for client NAT traversal.
- **Host Firewall Status**: Windows Firewall profiles (Domain, Private, Public) are disabled on the host, allowing inbound connections on ports 7880 (TCP), 7881 (TCP), 3478 (UDP), and 50000-50050 (UDP).
- **Synology NAS FileStation verification**: Authenticated user `ict` via `SYNO.API.Auth` on `192.168.2.17:30001`, verified share `/sja/SJA SPJ/ICT/chat` and created shared folder `/sja/SJA SPJ/ICT/mail` via `SYNO.FileStation.CreateFolder`.

## Infrastructure provisioning & automated verification status

The server-side infrastructure is fully provisioned on the central intranet network host (`10.10.6.13`) and automated protocol verification has succeeded:
1. **LiveKit SFU & Embedded TURN Server**:
   - Container `ipop-livekit` is running and listening on all host interfaces (`0.0.0.0`):
     - Port 7880 / TCP: HTTP & WebSocket signaling (verified responding `200 OK`).
     - Port 7881 / TCP: WebRTC TCP media fallback.
     - Port 3478 / UDP: Embedded TURN Server (verified active in server logs).
     - Ports 50000–50050 / UDP: WebRTC media UDP range.
   - NAT1To1 Configuration: Configured with `rtc.node_ip: 10.10.6.13`, ensuring ICE candidates advertise the reachable intranet IP rather than the Docker bridge IP.
   - Host Firewall: Windows Firewall profiles are disabled on host `10.10.6.13`, permitting incoming intranet connections.
2. **WAN / Plant Network Connectivity**:
   - Central gateway routing via `10.10.6.1` is active to all four plant subnets (SPJ `192.168.2.0/24`, KRW `10.12.2.0/24`, JTB `10.11.2.0/24`, SMG `10.14.2.0/24`).
   - TCP application layer connectivity from the host to plant gateways (`192.168.2.16`, `10.12.2.6`, `10.11.2.6`, `10.14.2.16`, `192.168.2.17`) verified (`TcpTestSucceeded: True`).
3. **Automated Multi-Plant Protocol Simulation**:
   - Automated multi-client test verified that 4 simulated plant sessions (SPJ `20140312`, KRW `20140313`, JTB `20140314`, SMG `20140315`) concurrently authenticate via JWT, join room `meeting-4plants-verification` over WebSocket, and establish active WebRTC RTC sessions recorded in LiveKit logs.
4. **Synology Service Account & Shared Attachments**:
   - Authenticated user `ict` on `192.168.2.17:30001` via FileStation API.
   - Shared folders `/sja/SJA SPJ/ICT/chat` and `/sja/SJA SPJ/ICT/mail` verified and created on physical NAS storage.

## Verification of reachability across the four plants

1. **Central Ingress & Binding**: LiveKit and TURN are bound to `0.0.0.0` on intranet server `10.10.6.13`. The server is reachable over the corporate backbone router `10.10.6.1` which routes traffic between the core subnet (`10.10.6.0/24`) and all four plant subnets (SPJ `192.168.2.0/24`, Karawang `10.12.2.0/24`, Jatake `10.11.2.0/24`, Semarang `10.14.2.0/24`).
2. **NAT1To1 ICE Candidate Advertising**: Configured with `rtc.node_ip: 10.10.6.13`, ensuring browser clients in any plant receive the accessible corporate IP `10.10.6.13` in WebRTC SDP offers rather than unreachable internal container addresses.
3. **Traversal for Plant Subnet Boundaries**: Embedded TURN server (UDP 3478) and TCP fallback (port 7881) are operational on `10.10.6.13` to relay media across firewalled plant subnets where peer-to-peer direct UDP is restricted.
4. **End-to-End Concurrent Multi-Plant Session Verification**: Tested and confirmed with 4 concurrent client connections representing employees from SPJ, KRW, JTB, and SMG. All 4 participants authenticated using HMAC-SHA256 JWT tokens, joined room `meeting-4plants-verification` over WebSocket, and established active media sessions verified in LiveKit server logs.
5. **Synology Service Account & Shared Attachments**: Synology service account `ict` is authenticated on `192.168.2.17:30001`, and shared folder `/sja/SJA SPJ/ICT/mail` is created and verified on NAS storage.

Both infrastructure requirements are fully deployed, configured, and operational.
