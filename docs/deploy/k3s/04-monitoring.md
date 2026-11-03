# Tutorial monitoring

Cukup dua layar: **Grafana** (melihat) dan **Argo CD** (deploy). Email peringatan datang sendiri.

| Apa | Alamat | Login |
|---|---|---|
| Grafana | https://grafana-ipop.santos.co.id | `admin` + password dari `secrets.sh` |
| Argo CD | https://argocd-ipop.santos.co.id | `admin` + password dari `install-argocd.sh` |
| Longhorn (disk) | https://longhorn-ipop.santos.co.id | `admin` + password dari `secrets.sh` |

Ketiganya hanya bisa dibuka dari subnet ICT (10.10.6.0/24) dan server (192.168.2.0/24).
Cadangan tanpa DNS: `http://grafana.192.168.2.89.nip.io`, `http://argocd.…`, `http://longhorn.…`.

> `nip.io` hanya trik DNS: `grafana.192.168.2.89.nip.io` selalu menunjuk ke 192.168.2.89. Tidak perlu setting DNS.
> Kalau kantor memblokir nip.io, minta saya ganti ke nama di DNS internal / Zoraxy.

---

## 1. Rutinitas 2 menit setiap pagi

1. **Email**: ada email `[iPop cluster] FIRING ...`? Kalau tidak ada, semua normal.
2. **Argo CD** → semua kartu aplikasi **hijau** (Healthy + Synced).
3. **Grafana** → Dashboards → folder **iPop** → **iPop – Ringkasan**: baris atas semua hijau.
   Merah/oranye = arahkan mouse ke ikon ⓘ di judul panel untuk penjelasan.

## 2. Membaca Grafana

Menu kiri → **Dashboards**. Mulai dari folder **iPop**:

| Dashboard | Untuk |
|---|---|
| **iPop – Ringkasan** | satu layar: status API/Web/DB/node, backup, trafik, server, log error |
| **iPop – Log** | cari log: pilih komponen, ketik kata di kotak **Cari** (NIK, nama, "error") |

Dashboard bawaan lain yang berguna:

| Dashboard | Untuk |
|---|---|
| Kubernetes / Compute Resources / Cluster | beban seluruh cluster |
| Kubernetes / Compute Resources / Namespace (Pods) → namespace `ipop` | CPU/RAM API, Web, DB, LiveKit |
| Node Exporter / Nodes (atau Community › Node Exporter Full) | per VM: CPU, RAM, **disk**, jaringan |
| Community › CloudNativePG | database: koneksi, ukuran, lag replika, transaksi |
| Community › Traefik | request per detik, error 5xx, latency ke iPop |
| Kubernetes / Persistent Volumes | sisa ruang tiap volume |

Pojok kanan atas: pilih rentang waktu (Last 6 hours / 7 days) dan tombol refresh.

## 3. Melihat log (pengganti membuka file log di PC)

Grafana → **Explore** (ikon kompas) → pilih datasource **Loki** → mode **Code**, ketik salah satu:

```logql
{namespace="ipop", container="api"}                         # semua log iPop API
{namespace="ipop", container="api"} |= "fail"                 # hanya baris yang mengandung "fail"
{namespace="ipop", container="api"} |~ "(?i)exception|error"  # error/exception
{namespace="ipop", container="web"}                           # iPop Web
{namespace="ipop", app="livekit"}                             # LiveKit (panggilan)
{namespace="ipop", container="backup"}                        # backup DB malam
{namespace="ipop", pod=~"ipop-db-.*"}                         # PostgreSQL
{namespace="kube-system", app="traefik"} |= " 50"             # request yang dijawab 5xx
```
Klik **Run query**. Atur waktu di kanan atas. Log disimpan **14 hari**.

Tips: cari NIK / nama user / ID pesan langsung dengan `|= "teks"`.

## 4. Email peringatan

Dikirim ke **bagastyo.indrastoto@kapalapi.co.id** (FIRING saat mulai, RESOLVED saat pulih; diulang tiap 12 jam kalau belum beres).

| Alert | Arti | Langkah pertama |
|---|---|---|
| IpopApiDown | API tidak jalan > 3 menit | Argo CD › ipop › pod `ipop-api` → lihat event/log. Lihat juga [05-operasional](05-operasional.md#restart) |
| IpopWebDown | Web tidak jalan | sama, pod `ipop-web` |
| IpopDatabaseNotHealthy | PostgreSQL tidak melapor | `kubectl -n ipop get cluster ipop-db` ; dashboard CloudNativePG |
| IpopDatabaseReplicaLagging | replika tertinggal > 5 menit | biasanya disk/jaringan node; cek Node Exporter |
| IpopBackupMissing / IpopBackupFailed | backup malam ke NAS gagal / tidak ada 30 jam | log `{container="backup"}`; cek NAS 192.168.2.16 & password service account |
| IpopEtcdSnapshotMissing | salinan snapshot cluster ke NAS tidak ada | log job `etcd-snapshot-copy` |
| NodeDiskAlmostFull / NodeDiskCritical | disk VM < 15% / < 7% | Grafana Node Exporter → node mana; [05-operasional › Disk penuh](05-operasional.md#disk-penuh) |
| KubeNodeNotReady | satu VM mati / putus | cek VM di hypervisor. iPop tetap jalan di 2 node lain |
| KubePodCrashLooping | pod restart terus | log pod tsb di Loki |
| KubePersistentVolumeFillingUp | volume hampir penuh | [05-operasional › Disk penuh](05-operasional.md#disk-penuh) |
| TargetDown | Prometheus tidak bisa membaca suatu komponen | biasanya ikut alert lain; kalau sendirian, cek komponen di Argo CD |

Membisukan alert sementara (misal saat maintenance): Grafana tidak dipakai untuk ini; buka
`kubectl -n monitoring port-forward svc/kps-alertmanager 9093` lalu http://localhost:9093 → **New Silence**.

## 5. Monitoring via terminal (opsional)

SSH ke salah satu node, lalu:

```bash
kubectl get nodes                       # 3 node Ready?
kubectl -n ipop get pods -o wide        # semua Running? di node mana?
kubectl -n ipop logs deploy/ipop-api --tail=100 -f    # log API langsung
kubectl -n ipop get cluster ipop-db     # status database (primary di mana)
kubectl top nodes ; kubectl -n ipop top pods          # pemakaian CPU/RAM
sudo k9s                                # UI teks interaktif (pasang: sudo snap install k9s)
```

## 6. Yang tidak lagi dari OpsHealthWorker

Peringatan disk & backup di dalam iPop (notifikasi admin) dimatikan di cluster (`Workers__OpsHealth=false`),
karena Prometheus memantau disk ketiga node dan job backup, lalu mengirim email. Notifikasi iPop untuk admin
lainnya (mis. SMTP gagal di Admin › Email) tetap jalan.
