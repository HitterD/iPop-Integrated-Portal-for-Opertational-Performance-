# iPop di k3s — panduan

Prod iPop pindah dari PC Windows ke cluster k3s 3 node. Dokumen ini untuk admin ICT.

| Node | IP | Isi utama |
|---|---|---|
| ipop-01 | 192.168.2.88 | LiveKit (panggilan grup & meeting), Traefik, sebagian aplikasi |
| ipop-02 | 192.168.2.112 | PostgreSQL (primary/replica), cadangan LiveKit |
| ipop-03 | 192.168.2.113 | PostgreSQL (replica/primary), Egress, monitoring |
| **VIP** | **192.168.2.89** | alamat "mengambang" — selalu dipegang node yang hidup |

Semua alamat dashboard memakai VIP, jadi tetap sama walau satu node mati.

## Urutan baca

1. [01-yang-perlu-anda-lakukan.md](01-yang-perlu-anda-lakukan.md) — **langkah yang butuh Anda** (install ulang OS, token, secret, Panda, Zoraxy).
2. [02-install-cluster.md](02-install-cluster.md) — apa yang saya jalankan di VM (supaya bisa diulang).
3. [03-secrets.md](03-secrets.md) — daftar semua password/secret dan dari mana asalnya.
4. [04-monitoring.md](04-monitoring.md) — **tutorial monitoring harian**: Grafana, log, alert email.
5. [05-operasional.md](05-operasional.md) — deploy, rollback, restart, update OS, backup & restore.
6. [06-zoraxy.md](06-zoraxy.md) — setting reverse proxy saat cutover.
7. [07-cutover.md](07-cutover.md) — checklist pindah dari PC lama ke cluster, dan cara kembali.

## Gambaran singkat

```
Browser ──https──▶ Zoraxy 192.168.2.2 ──http──▶ VIP 192.168.2.89 (Traefik)
                                                     ├─ /api /hubs /connect ─▶ iPop API (1 pod)
                                                     ├─ /rtc ───────────────▶ LiveKit
                                                     ├─ /onlyoffice ────────▶ ONLYOFFICE
                                                     └─ selain itu ─────────▶ iPop Web (2 pod)
Media panggilan (UDP 50000-50100, TCP 7881, UDP 3478) ─▶ langsung ke node LiveKit (ipop-01 / ipop-02)

GitHub main ─▶ Actions: test → build image → GHCR → tulis tag baru ─▶ Argo CD di cluster menarik & deploy
```

## Kenapa API hanya 1 pod

Kode iPop menyimpan presence, status panggilan, alur SSO, kode pairing Notifier, dan versi dokumen
ONLYOFFICE di memori, dan ~25 background worker-nya tidak punya kunci bersama. Dua pod = email
terkirim dua kali dan panggilan putus. Jadi API 1 pod; kalau node-nya mati, Kubernetes memindahkannya
ke node lain dalam ±1–2 menit. Membuat API aman di banyak pod adalah proyek terpisah (worker lock,
state ke Redis, sticky session) — setelah itu Redis Sentinel juga baru berguna.
