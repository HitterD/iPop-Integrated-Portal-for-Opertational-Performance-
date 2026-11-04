# iPop Notifier di k3s: rilis lewat GitHub + .msi untuk GPO

## Alurnya

```
git tag notifier-v1.4.8  ─►  GitHub Actions (Windows)  ─►  GitHub Release
                               build + vpk + .msi GPO          │
                                                               ▼  (cluster menarik tiap 5 menit, CronJob notifier-sync)
                                                 /data/notifier/releases  = PILOT (ICT + Admin)
                                                               │
                                     Admin › Notifier › "Release 1.4.8 to everyone"
                                                               ▼
                                                       semua PC update sendiri
```

GitHub tidak pernah masuk ke LAN; cluster yang menarik. Push biasa ke `main` **tidak** membuat rilis Notifier.

## Membuat rilis

```powershell
# versi harus lebih baru dari rilis terakhir; pesan tag = "What's new" di popup update
git tag -a notifier-v1.4.8 -m "Perbaikan popup tiket"
git push github notifier-v1.4.8
# wajib dipasang langsung setelah dirilis ke semua: tulis [mandatory] di pesan tag
```

Atau: GitHub › Actions › **Notifier release** › Run workflow (isi versi, flavour `prod`).

Dalam ±10 menit build selesai, ≤5 menit kemudian versi itu muncul sebagai **Pilot** di Admin › Notifier.
PC ICT/Admin mendapatkannya duluan. Kalau aman: klik **Release … to everyone**.

QA/staging: tag `notifier-qa-v0.9.2` → paket `iPopNotifierQA` yang tersambung ke
`http://staging.192.168.2.89.nip.io`, masuk ke feed staging saja.

## Sekali saja: token GitHub untuk cluster

Fine-grained token, repo iPop saja, izin **Contents: Read-only**:

```bash
kubectl -n ipop         create secret generic ipop-notifier-github --from-literal=token=github_pat_…
kubectl -n ipop-staging create secret generic ipop-notifier-github --from-literal=token=github_pat_…
```

Tanpa token, Admin › Notifier menampilkan "GitHub sync not set up" (bukan alarm).
Gagal 3× berturut-turut → notifikasi iPop ke Admin; tidak sukses >20 menit → email Alertmanager (`NotifierSyncFailing`).

## .msi untuk Group Policy

Unduh dari Admin › Notifier › **Download .msi** (`iPopNotifier-win-GPO.msi`), taruh di share yang bisa dibaca
semua *computer account* (mis. `\\dc1\NETLOGON\ipop\`), lalu:

GPMC › GPO baru › **Computer Configuration › Policies › Software Settings › Software installation** ›
New › Package › path UNC › **Assigned**.

- Saat PC boot, .msi memasang `C:\Program Files\iPop Notifier\` (tanpa UI).
- Saat tiap user login, Notifier terpasang untuk user itu (diam-diam), muncul di tray, membuka browser sekali untuk sign-in.
- Sudah punya Notifier (dari Setup.exe)? Dilewati.
- Update berikutnya datang dari feed iPop. **GPO tidak perlu diganti setiap versi**; .msi baru hanya perlu
  kalau isinya sendiri berubah (GPO › Upgrade).
- GPO dihapus / PC keluar OU (pilih "Immediately uninstall") → Notifier dihapus dari **semua** profil di PC itu.
- Log: `C:\ProgramData\iPop Notifier\deploy.log` (uninstall) dan `%TEMP%\ipop-notifier-deploy.log` (per user).

Uji manual di satu PC (admin):

```powershell
msiexec /i iPopNotifier-win-GPO.msi /qn /l*v C:\temp\gpo-install.log
# logout/login → Notifier muncul
msiexec /x iPopNotifier-win-GPO.msi /qn /l*v C:\temp\gpo-remove.log
```

Belum ditandatangani (belum ada sertifikat Code Signing). Kalau Panda memblokir: kecualikan
`%LocalAppData%\iPopNotifier` dan `C:\Program Files\iPop Notifier`.

## Cutover dari PC Windows lama

Salin seluruh `C:\ipop-ops\notifier\releases` (termasuk `release.json` dan `pilot\`) ke volume
`/data/notifier/releases` di namespace `ipop`, cek `https://ipop.santos.co.id/download/notifier/releases.win.json`,
baru arahkan domain. Notifier yang sudah terpasang tidak perlu apa-apa: alamatnya tetap `ipop.santos.co.id`.
Rilis cluster berikutnya harus lebih baru dari versi terakhir di folder itu (saat ini 1.4.7 pilot / 1.4.4 semua).
