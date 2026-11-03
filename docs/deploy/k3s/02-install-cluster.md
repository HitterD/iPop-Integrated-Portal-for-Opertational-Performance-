# Install cluster (yang saya jalankan)

Dicatat supaya bisa diulang kalau cluster harus dibangun ulang. Semua skrip ada di `deploy/k8s/bootstrap/`.

## 0. Salin repo ke ipop-01

```bash
# di ipop-01, sebagai administrator
git clone https://github.com/HitterD/iPop-Integrated-Portal-for-Opertational-Performance-.git ~/ipop
```
(Repo private: pakai token dari langkah 4a sebagai password, atau `scp` folder `deploy/k8s` dari PC.)

## 1. Persiapan tiap node (ketiganya)

```bash
sudo bash ~/ipop/deploy/k8s/bootstrap/prepare-node.sh
```
Zona waktu Asia/Jakarta, NTP, open-iscsi/nfs/cifs (Longhorn + backup SMB), swap off, sysctl, kernel modules.
Kalau VM masih punya machine-id hasil clone, skrip membuat ulang machine-id dan SSH host key.

## 2. k3s

```bash
TOKEN=$(head -c 32 /dev/urandom | base64 | tr -dc A-Za-z0-9)   # simpan di password manager
# ipop-01
sudo K3S_TOKEN=$TOKEN bash ~/ipop/deploy/k8s/bootstrap/install-k3s.sh first
# ipop-02, lalu ipop-03 (token sama)
sudo K3S_TOKEN=$TOKEN bash ~/ipop/deploy/k8s/bootstrap/install-k3s.sh join
```
Cek: `kubectl get nodes` → 3 node `Ready`, role `control-plane,etcd`. `ping 192.168.2.89` menjawab.

## 3. Secret (bersama Anda)

```bash
bash ~/ipop/deploy/k8s/bootstrap/secrets.sh
```

## 4. Argo CD + semua aplikasi

```bash
bash ~/ipop/deploy/k8s/bootstrap/install-argocd.sh
```
Argo CD lalu memasang sendiri, berurutan (sync-wave): CNPG, Longhorn, csi-smb, Argo CD (mengelola
dirinya sendiri) → monitoring, Loki, Traefik config → Alloy → iPop (Postgres, Redis → migrasi → API, Web, media) → backup.

Cek: http://argocd.192.168.2.89.nip.io → semua aplikasi **Synced / Healthy**.

## 5. Data prod (salinan, untuk uji)

Di PC prod lama (PowerShell):
```powershell
docker exec ipop-postgres pg_dump -U ipop -d ipop -Fc -f /tmp/ipop.dump
docker cp ipop-postgres:/tmp/ipop.dump .\ipop.dump
docker exec ipop-postgres rm /tmp/ipop.dump
scp .\ipop.dump administrator@192.168.2.88:~/
```
Di ipop-01:
```bash
bash ~/ipop/deploy/k8s/bootstrap/restore-db.sh ~/ipop.dump && rm ~/ipop.dump
```

File lokal (rilis Notifier, APK, video news, upload yang belum pindah ke NAS) disalin ke volume `ipop-data`:
```bash
POD=$(kubectl -n ipop get pod -l app.kubernetes.io/name=ipop-api -o name | head -1)
kubectl -n ipop cp ./notifier-releases  ${POD#pod/}:/data/notifier/releases
kubectl -n ipop cp ./android            ${POD#pod/}:/data/android
kubectl -n ipop cp ./news-videos        ${POD#pod/}:/data/news-videos
kubectl -n ipop cp ./chat-files         ${POD#pod/}:/data/chat-files
kubectl -n ipop cp ./mail-files         ${POD#pod/}:/data/mail-files
```
(sumber: `C:\ipop-ops\notifier\releases`, `C:\ipop-ops\android`, dan folder di `ipop-rescue\src\IPop.Api\`.)

## 6. Kunci SSH, matikan login password

```bash
# dari PC admin: ssh-copy-id administrator@192.168.2.88  (dan .112, .113)
sudo sed -i 's/^#\?PasswordAuthentication.*/PasswordAuthentication no/' /etc/ssh/sshd_config
sudo sed -i 's/^#\?KbdInteractiveAuthentication.*/KbdInteractiveAuthentication no/' /etc/ssh/sshd_config
sudo systemctl restart ssh
```
