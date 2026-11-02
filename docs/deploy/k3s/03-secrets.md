# Secret dan dari mana asalnya

Tidak ada satu pun password di git. Semua dibuat oleh `bootstrap/secrets.sh` sebagai Kubernetes Secret.
Untuk melihat/ubah nanti: lihat bagian "Mengganti secret" di bawah.

## Diambil dari prod lama (harus sama, supaya tidak ada yang rusak)

Di PC prod lama, nilai ada di dua tempat:
- `C:\Users\IT12\orca\workspaces\ipop-rescue\src\IPop.Api\appsettings.Local.json`
- User secrets: `C:\Users\IT12\AppData\Roaming\Microsoft\UserSecrets\ipop-api-dev\secrets.json`

| Ditanya `secrets.sh` | Kunci di file lama | Kenapa harus sama |
|---|---|---|
| Jwt:Key | `Jwt.Key` (user secrets / Local) | kalau beda, semua orang ter-logout dan link tanda-tangan lama mati |
| Smtp:Password | `Smtp.Password` | kirim email (lupa password, notifikasi). Bisa juga diubah lewat **Admin › Email** |
| Synology:ServiceAccount:Password | `Synology.ServiceAccount.Password` | akses NAS (file, lampiran, rekaman, backup) |
| Push:VapidPublicKey / VapidPrivateKey | `Push.*` | kalau beda, semua langganan notifikasi browser/Windows harus daftar ulang |
| IDesk:ClientSecret | user secrets `IDesk:ClientSecret` | tiket iDesk + webhook |
| Hris:ApiKey | user secrets `Hris:ApiKey` | sinkron karyawan dari HRIS |
| Oidc:Clients:0:ClientSecret | `Oidc.Clients[0].ClientSecret` | "Sign in with iPop" di iDesk |
| Fcm:ApiKey | `Fcm.ApiKey` | push Android |
| Firebase service-account.json | file `C:\ipop-ops\firebase\service-account.json` | push Android |
| SMTP untuk alert | sama dengan Smtp:Password | email peringatan cluster |

## Dibuat baru oleh skrip (tidak perlu Anda isi)

| Secret | Isi |
|---|---|
| `ipop/ipop-db-app` | password user database `ipop` (acak) |
| `ipop/livekit-keys` | API key + secret LiveKit baru (acak) |
| `monitoring/grafana-admin` | password admin Grafana — **ditampilkan sekali**, simpan di password manager |
| `argocd/argocd-initial-admin-secret` | password admin Argo CD — ditampilkan oleh `install-argocd.sh` |

## Dari akun GitHub (langkah 4 di 01-yang-perlu-anda-lakukan.md)

| Secret | Token |
|---|---|
| `argocd/repo-ipop` | fine-grained, Contents: Read-only |
| `ipop/ghcr` | classic, `read:packages` |

## Mengganti secret

Contoh: password SMTP berganti.
```bash
kubectl -n ipop patch secret ipop-api-secrets --type merge \
  -p "{\"stringData\":{\"Smtp__Password\":\"$(read -r -s -p 'Password baru: ' p; echo "$p")\"}}"
kubectl -n ipop rollout restart deploy/ipop-api
```
(Untuk SMTP lebih mudah lewat **iPop › Admin › Email**, tanpa restart.)

Token GitHub kedaluwarsa (tahunan):
```bash
kubectl -n ipop delete secret ghcr && bash ~/ipop/deploy/k8s/bootstrap/secrets.sh      # hanya membuat yang hilang
kubectl -n argocd delete secret repo-ipop && bash ~/ipop/deploy/k8s/bootstrap/install-argocd.sh
```
