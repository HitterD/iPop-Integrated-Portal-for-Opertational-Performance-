# Audit integritas data & stabilitas — 2026-11-22

Scope: prod Windows (users), cluster k3s ns `ipop`, NAS backup + NAS file store. Read-only on prod data.

## Integritas data (hasil ukur)

| Cek | Windows prod | k3s `ipop` | Backup 02:00 (direstore) |
|---|---|---|---|
| amcheck btree (heapallindexed) | 487 index OK | 487 OK | 487 OK |
| verify_heapam: baris korup | 0 | 0 | 0 |
| Index invalid / constraint unvalidated | 0 / 0 | 0 / 0 | 0 / 0 |
| Tabel tanpa primary key | 0 | 0 | – |
| FK | 252 | 252 | 536 constraint (= live) |
| Migrasi | 131 (NotificationActor) | 131 | – |
| Checksum failure / deadlock | 0 / 0 | 0 / 0 | – |
| Hitungan baris vs live | – | – | 166/171 tabel identik; 5 beda = baris baru setelah 02:00 (sesi, notif) |
| Hash dump NAS = lokal | – | – | SHA-256 sama |

File lampiran di NAS `ict` (FileStation API, semua baris DB):

| Jenis | Baris DB | Ada di NAS | Ukuran cocok | SHA-256 cocok |
|---|---|---|---|---|
| Chat | 418 | 418 | 418 | – (DB tidak simpan hash) |
| Mail | 2.753 | 2.753 | 2.753 | – |
| Attachments (Notes/Hub/News/inline) | 2.315 | 2.315 | 2.315 | **2.315 / 2.315** |
| Sticker / rekaman / video | 152 / 5 / 1 | semua | – | – |
| **Total** | **5.644** | **5.644 (0 hilang)** | 0 beda | 0 beda |

Referensi tanpa FK (66 kolom uuid dicek): 0 orphan nyata. Sisa data uji lama: 2 mail "sent" tanpa penerima (Sep/Okt, sebelum validasi `no_recipients`), 2 meeting duplikat "Standup Kalender UI" (data uji Sep). Pesan meeting `UserId = 0000…` = pesan sistem (by design). Tidak ada duplikat MailRecipients/ChatRoomMembers/NIK; antrean outbox/push/mail-notify 0 tertunda.

## Stabilitas

| Area | Sebelum | Sesudah |
|---|---|---|
| HRIS sync | Gagal 11 hari (10.10.6.51:27080 refused) **tanpa alert** | Alert Admin (high, 1×/hari) setelah 48 jam tanpa sukses — `42634cc` |
| Backup harian | Hanya `pg_restore -l` (bisa dibaca ≠ bisa direstore) | Restore penuh ke DB scratch + amcheck + hitung tabel/baris tiap malam (11 dtk) — `72eedb2` |
| Monitoring cluster | `TargetDown ipop-api` menyala permanen sejak 12-Nov (scrape JSON ditolak Prometheus 3) → outage asli tak terlihat | `fallbackScrapeProtocol` — `aa842d1` |
| Alert basi | 3 `KubeJobFailed` dari job 11–13 hari lalu (sudah digantikan run sukses) | Job lama dihapus; alert bersih |
| Query `Take()` tanpa `OrderBy` | Search global & auto-stop timer bisa mengembalikan baris acak (EF warning 10102 di log prod) | Urutan stabil — `033b83b` |
| Log API prod 4 jam | 0 `fail`/`crit` | – |
| Cluster | 3 node Ready, CNPG 2/2 sehat, WAL archive OK (terakhir gagal 11-Nov), Longhorn 3 volume healthy + backup harian Completed | – |

## Putaran 2 — HRIS tidak bocor ke user + celah lain

| Area | Sebelum | Sesudah |
|---|---|---|
| Notifikasi "HRIS Sync Complete" | `Target.All` → **10 sync sejak 30-Sep = 49.198 notifikasi ke user biasa** | Hanya role Admin, buka `/admin` — `14c2e1e` |
| API Dashboard `Status` (HRIS, outbox mail, NAS) | Panel disembunyikan di UI, tapi JSON tetap dikirim ke semua user | Non-admin terima list kosong (DB tidak dibaca) — `d04e7d7` |
| Endpoint `/api/admin/hris/*` | AdminOnly | Dicek ulang: semua AdminOnly; scan 297 route tanpa login → hanya `/api/app/android/latest` (info versi APK, by design) |
| Alert HRIS gagal | Admin only | Tetap Admin only (+ alert stale 48 jam baru) |
| Buat DM paralel | 2 request bersamaan → **2 room DM** (dibuktikan di Postgres) | Advisory lock per pasangan: 5 paralel → 1 room — `2725cc7` |
| Lockout login (5× salah) | Hitungan baca-tulis: 2 salah paralel tercatat 1 (test RED) → tebakan paralel tak pernah terkunci | UPDATE atomik — `e5a89e0` (RED `43a8846` → GREEN) |
| Mail terjadwal | Penerima "pending" masih bisa unduh lampiran/thumbnail, pasang label, minta PDF, lihat gambar inline (jika tahu GUID) | Aturan `Folder != "pending"` di 5 jalur + test — commit mail |
| Pengingat meeting | `Take(200)` tanpa urutan → backlog >200 bisa melewatkan yang terlama | Urut dari jatuh tempo terlama — `b101431` |
| Audit IDOR (Mail, Notifikasi, Calendar, Files, Notes, Recording) | – | Semua endpoint ber-id dicek sampai ke service: tidak ada IDOR yang bisa dieksploitasi |

Bersih prod: 0 DM ganda (492 DM), 0 duplikat penerima/anggota. Data uji lama yang **tidak** saya hapus (perlu izin): 2 meeting "Standup Kalender UI" (ipopadmin, Sep), 2 mail "sent" tanpa penerima (Sep/Okt).

## Masih terbuka (butuh keputusan/akses user)

1. **HRIS 10.10.6.51:27080 mati** sejak 2026-11-11 — perlu tim HRIS/server; directory tidak menerima karyawan baru/keluar.
2. **Drive C: 24,5 GB (5 %)** — alert kritis sudah jalan. Butuh admin: hapus `C:\Windows.old` (~59 GB), batasi pagefile.
3. **Task "iPop DB backup" = Interactive only (user IT12)** — tidak jalan bila IT12 logoff. Butuh admin untuk "Run whether user is logged on".
4. **DB cluster tertinggal 10 hari** dari Windows (by design, belum cutover) — perlu re-copy sebelum cutover.
5. `longhorn-pre-upgrade` Failed (by design, preUpgradeChecker off) — dibiarkan.
6. `data_checksums` off di kedua DB (aktifkan hanya saat maintenance window: `pg_checksums --enable` butuh DB berhenti).
