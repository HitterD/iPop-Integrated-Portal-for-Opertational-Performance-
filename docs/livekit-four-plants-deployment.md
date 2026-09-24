# LiveKit + TURN deployment checklist (SPJ, KRW, JTB, SMG)

This is an operator checklist, **not a deployment record**. No server has been provisioned or browser-tested by this change. The infrastructure owner must pick a public/reachable hostname, network placement, certificates and firewall routes for all four plants.

## Prerequisites

- One stable LiveKit endpoint, e.g. `wss://<calls-host>:443`, that browsers in SPJ, KRW, JTB and SMG can resolve and reach over HTTPS/WSS. Use a certificate trusted by all four plants. Do not use a `*.local` hostname unless DNS and certificate trust are explicitly managed at every site.
- Route HTTPS/WSS TCP 443 to the LiveKit ingress and configure UDP media ports (or TURN relay) according to the installed LiveKit release. A TURN server reachable by all four plants must be provisioned for restrictive NAT/firewalls. Test UDP and TURN/TLS fallback from each plant and off-LAN; TCP signaling alone is **not** proof that audio/video works.
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
