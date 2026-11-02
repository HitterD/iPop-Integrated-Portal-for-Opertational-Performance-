# Cutover: dari PC lama ke cluster

Prasyarat: uji coba di http://ipop.192.168.2.113.nip.io sudah Anda nyatakan **normal**.
Jendela waktu: malam/weekend. Perkiraan downtime: ±15–20 menit (DB ±112 MB).

## H-1
- [ ] TTL DNS `ipop.santos.co.id` diturunkan ke 300 detik.
- [ ] Sertifikat `ipop.santos.co.id` siap di Zoraxy (belum dipakai).
- [ ] Pengumuman ke user: iPop maintenance jam sekian.

## Hari H
1. **Freeze** — hentikan API lama di PC prod (by PID, jangan by image name). Web lama boleh tetap menampilkan halaman.
2. **Dump terakhir** di PC lama → salin ke ipop-01 → `restore-db.sh` (lihat [02-install-cluster.md](02-install-cluster.md#5-data-prod-salinan-untuk-uji)).
   Salin juga ulang folder file lokal (chat-files, mail-files, news-videos, notifier/releases, android).
3. **Nyalakan pengiriman keluar** — di git:
   - `deploy/k8s/apps/ipop/config.yaml`: semua baris bertanda `# CUTOVER` (Workers__Outbound → "true",
     LiveKit → `wss://ipop.santos.co.id`, ONLYOFFICE → `https://ipop.santos.co.id/onlyoffice`)
   - `deploy/k8s/apps/ops/nas-backup.yaml`: kedua `BACKUP_FOLDER` → `SJA SPJ/ICT/backup/ipop-db`
   Push → Argo menerapkan → `kubectl -n ipop rollout restart deploy/ipop-api`.
4. **Matikan backup Windows lama**: Task Scheduler → "iPop DB backup" → Disable (supaya tidak dua sumber menulis ke folder yang sama).
5. **Zoraxy + DNS** → [06-zoraxy.md](06-zoraxy.md).
6. **Tes** (saya + Anda): login, chat 2 arah realtime, kirim mail + lampiran, lupa password (email sampai),
   panggilan 1:1 + grup + meeting room + rekaman, buka dokumen ONLYOFFICE, upload ke Files (NAS), notifikasi push
   Windows (Notifier) & Android, buat tiket iDesk, Sign in with iPop dari iDesk, download Notifier.
7. **Pantau** Grafana & email alert 24 jam pertama.

## Rollback (kapan saja dalam minggu pertama)
1. Zoraxy/DNS kembali ke PC lama.
2. Kalau sudah ada data baru di cluster yang harus ikut: dump dari cluster
   (`kubectl -n ipop create job --from=cronjob/ipop-db-backup rollback-dump`) → restore ke `ipop-postgres` di PC lama.
3. Start API lama (catatan: `ASPNETCORE_ENVIRONMENT=Development`, `MeetingRooms__DeploymentMode=Production`, `--contentRoot src\IPop.Api`).
4. Aktifkan lagi Scheduled Task backup Windows.

PC lama tetap standby **1 minggu** setelah cutover, lalu dibersihkan.

## Setelah stabil
- Ganti password `administrator` di 3 VM ([01](01-yang-perlu-anda-lakukan.md#8-paling-akhir-ganti-password-administrator)).
- Rotasi password SMTP/NAS yang sempat tertulis di file prod lama (opsional, dianjurkan).
- Proyek berikut (terpisah): API multi-replika (worker lock, state ke Redis, sticky session) → baru setelah itu Redis Sentinel & API 2–3 pod.
