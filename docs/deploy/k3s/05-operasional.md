# Operasional

## Deploy versi baru

Tidak ada lagi copy file / restart manual.

1. Perubahan di-merge ke branch `main` di GitHub.
2. GitHub Actions (tab **Actions**): **CI** (tes .NET + JS) → **Build and deploy**: build image `ipop-api` & `ipop-web`,
   push ke GHCR dengan tag = 12 karakter commit, lalu menulis tag itu ke `deploy/k8s/apps/ipop/kustomization.yaml`.
3. Argo CD melihat commit itu (≤ 2 menit) dan menerapkannya:
   database & Redis tetap → **job migrasi** → API diganti (± 30–60 detik iPop "menyambung ulang") → Web diganti bergilir (tanpa putus).
4. Cek di Argo CD › **ipop**: Synced + Healthy.

Kalau tes gagal, tidak ada image yang dibuat dan prod tidak berubah.
Kalau migrasi gagal, Argo berhenti dan API lama tetap jalan — lihat log job `ipop-migrate` di Argo CD.

## Rollback

**Cara 1 (cepat, dari Argo CD):** ipop → **History and rollback** → pilih versi sebelumnya → **Rollback**.
Argo menonaktifkan auto-sync selama rollback; aktifkan lagi (**Enable auto-sync**) setelah perbaikan di-merge.

**Cara 2 (git):** revert commit `deploy: iPop <tag>` di GitHub (atau ubah kedua `newTag` ke tag lama) → push.

> Rollback image tidak membatalkan migrasi database. Migrasi iPop selama ini hanya menambah kolom/tabel, jadi
> versi lama tetap jalan. Kalau sebuah rilis menghapus/mengubah kolom, rollback-nya pakai restore backup.

## Restart

```bash
kubectl -n ipop rollout restart deploy/ipop-api      # API (± 30–60 detik)
kubectl -n ipop rollout restart deploy/ipop-web      # Web (tanpa putus)
kubectl -n ipop rollout restart deploy/livekit       # panggilan yang sedang jalan akan putus
kubectl -n ipop rollout restart deploy/onlyoffice
```
Atau di Argo CD: klik deployment → titik tiga → **Restart**.

## Mengubah setting iPop

Setting non-rahasia ada di `deploy/k8s/apps/ipop/config.yaml` (git). Ubah → commit → push → Argo menerapkan.
API perlu restart agar membaca setting baru: `kubectl -n ipop rollout restart deploy/ipop-api`.
Rahasia: lihat [03-secrets.md](03-secrets.md#mengganti-secret).

## Update OS / reboot node (satu per satu!)

```bash
kubectl drain ipop-02 --ignore-daemonsets --delete-emptydir-data --timeout=10m   # pindahkan pod keluar
ssh ipop-02 'sudo apt update && sudo apt full-upgrade -y && sudo reboot'
kubectl uncordon ipop-02                                                          # setelah node Ready lagi
```
Tunggu node `Ready` dan Longhorn hijau (semua volume Healthy) sebelum node berikutnya.
Node ipop-01 menjalankan LiveKit: drain di luar jam meeting (LiveKit pindah ke ipop-02, panggilan aktif putus).

## Upgrade k3s

Satu node per waktu, mulai ipop-01:
```bash
sudo K3S_TOKEN=<token> INSTALL_K3S_VERSION=v1.37.x+k3s1 bash -c 'curl -sfL https://get.k3s.io | sh -s - server'
```
Baca release notes k3s dulu; naik satu minor version sekali jalan.

## Backup & restore

| Apa | Kapan | Ke mana | Simpan |
|---|---|---|---|
| Database iPop (pg_dump) | 01:00 WIB | `\\192.168.2.16\sja\SJA SPJ\ICT\backup\ipop-db` | 14 harian + 8 mingguan |
| Snapshot etcd (state cluster) | 00:00 & 12:00 di node, salin 01:30 | `...\ipop-db\etcd` | 14 file terakhir |
| Volume Longhorn | 2 salinan di node berbeda | — | — |
| PostgreSQL | primary + replika di node berbeda | — | — |

> Selama masa uji, backup cluster ditulis ke `...\backup\ipop-db-k3s-test` supaya tidak tercampur/memangkas backup prod
> lama. Saat cutover dipindah ke `ipop-db` dan Scheduled Task Windows "iPop DB backup" dimatikan.

Backup manual sebelum perubahan besar:
```bash
kubectl -n ipop create job --from=cronjob/ipop-db-backup manual-$(date +%Y%m%d%H%M)
```

Restore (mengganti seluruh DB):
```bash
# salin dump dari NAS ke ipop-01, lalu
bash ~/ipop/deploy/k8s/bootstrap/restore-db.sh ipop-20261102-0100.dump
```

## Disk penuh

```bash
kubectl get pv                                    # volume mana
sudo du -xh --max-depth=1 /var/lib | sort -h      # di node: rancher (image), longhorn, ...
sudo k3s crictl rmi --prune                       # hapus image container yang tidak dipakai
```
Volume `ipop-data` bisa diperbesar: ubah `storage: 30Gi` di `apps/ipop/storage.yaml` → push (Longhorn mendukung resize).

## Satu node mati — apa yang terjadi otomatis

| Komponen | Perilaku |
|---|---|
| VIP 192.168.2.113 | pindah ke node lain dalam ±3 detik |
| iPop Web | pod lain tetap melayani, tanpa putus |
| iPop API | pindah ke node lain dalam ±1–2 menit (user melihat "menyambung ulang") |
| PostgreSQL | replika dipromosikan jadi primary ±30 detik |
| LiveKit | pindah ke ipop-02 (kalau ipop-01 yang mati); panggilan aktif putus, panggilan baru jalan |
| Longhorn | volume tetap tersedia dari salinan kedua |
| etcd (cluster) | tetap jalan dengan 2 dari 3 node |

Dua node mati bersamaan = cluster berhenti (etcd butuh mayoritas). Hidupkan salah satunya kembali.
