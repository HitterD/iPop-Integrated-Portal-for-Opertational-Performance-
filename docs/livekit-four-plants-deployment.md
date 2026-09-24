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

## Acceptance checks

1. From SPJ, KRW, JTB and SMG, log in as a distinct invited employee; open the same `/meet/meeting-<id>` link. Verify the server rejects a non-invitee, connects invitees to one room and disconnects on Leave.
2. With at least three participants across plants, verify two-way audio, video on/off and microphone mute/unmute. Repeat on a restrictive network to confirm TURN relay is used and media still works. Inspect browser console for WebRTC/LiveKit errors and record actual connection metrics.
3. On each plant, test direct calls in both directions. Verify incoming notification, answer, hangup, history, and media fallback as configured.
4. Send an internal mail with a file from one account to another account with **no access to the sender's NAS home**. Confirm the recipient can download and preview the attachment and an unrelated account receives 403. Test an orphan upload and deleting one's own orphan.
5. Confirm backups, monitoring/alerts, TLS renewal, DNS failover and secret rotation owners. Record the chosen hostname, ports, server owner, NAS share, and test date in the operator runbook (never record secret values here).

Until these checks are completed, LiveKit/TURN four-plant availability and the Synology shared-folder integration remain unverified operational dependencies.
