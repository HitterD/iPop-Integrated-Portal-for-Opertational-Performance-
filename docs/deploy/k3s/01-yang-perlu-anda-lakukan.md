# Langkah yang butuh Anda

Saya tidak bisa melakukan ini sendiri (akses hypervisor, akun pribadi, atau password yang tidak boleh lewat chat).
Kerjakan berurutan; tiap langkah ada cara mengecek bahwa sudah benar.

---

## 1. Install ulang ketiga VM dengan Ubuntu Server 26.04 LTS

**Kenapa:** VM sekarang Ubuntu 26.10 (non-LTS, habis support ±Juli 2027) dan hasil clone — `machine-id`
dan SSH host key ketiganya sama persis, yang membuat k3s/Longhorn bingung membedakan node.
26.10 → 26.04 tidak bisa di-upgrade (itu turun versi), jadi harus install ulang.

Untuk **tiap** VM (jangan clone dari VM lain — install masing-masing dari ISO):

1. Download ISO: `ubuntu-26.04-live-server-amd64.iso` dari https://ubuntu.com/download/server
2. Di hypervisor: matikan VM → pasang ISO ke CD-ROM → boot dari CD.
   Spesifikasi tetap: 6 vCPU, 15 GB RAM, disk 150 GB.
3. Di installer:
   - Bahasa: English. Keyboard: English (US).
   - Jenis install: **Ubuntu Server** (bukan minimized).
   - Network `ens3`: **Manual (static)**

     | VM | Address | Gateway | DNS |
     |---|---|---|---|
     | ipop-01 | 192.168.2.88/24 | 192.168.2.1 | 192.168.2.14, 192.168.2.15 |
     | ipop-02 | 192.168.2.112/24 | 192.168.2.1 | 192.168.2.14, 192.168.2.15 |
     | ipop-03 | 192.168.2.113/24 | 192.168.2.1 | 192.168.2.14, 192.168.2.15 |
   - Proxy: kosong. Mirror: default.
   - Storage: **Use an entire disk**, centang **Set up this disk as an LVM group**.
     Di layar ringkasan, ubah `ubuntu-lv` supaya memakai **seluruh** ruang (default Ubuntu hanya 100 GB) → Edit → Size = max.
   - Profile: name `administrator`, server name `ipop-01` / `ipop-02` / `ipop-03`, username `administrator`, password sementara (boleh sama dengan sekarang; diganti di langkah terakhir).
   - Ubuntu Pro: Skip.
   - **SSH: centang "Install OpenSSH server"**. Import SSH key: tidak.
   - Featured snaps: **jangan pilih apa pun**.
4. Selesai → Reboot → lepas ISO.
5. Login, lalu: `sudo apt update && sudo apt full-upgrade -y && sudo reboot`

**Cek:** dari PC Anda, `ssh administrator@192.168.2.88` (dan .112, .113) bisa masuk, dan
`cat /etc/machine-id` di ketiganya **berbeda**.

➡️ Setelah ketiganya selesai, kabari saya. Saya yang jalankan sisanya (persiapan node, k3s, dll).

---

## 2. Pastikan IP 192.168.2.89 aman dipakai sebagai VIP

Cek di DHCP server/router bahwa **192.168.2.89 tidak ada di range DHCP** dan tidak dipakai perangkat lain.
Kalau ada di range DHCP, buat reservasi/exclusion untuk IP itu.

**Cek:** `ping 192.168.2.89` dari PC Anda **tidak** menjawab (sebelum cluster dipasang).

---

## 3. Minta tim security membuat pengecualian Panda Security

Agent Panda di VM bisa memblokir/melambatkan container. Minta exclusion di 3 VM untuk:

- Folder: `/var/lib/rancher/`, `/var/lib/kubelet/`, `/run/k3s/`, `/var/lib/longhorn/`, `/var/log/pods/`
- Proses: `k3s`, `containerd`, `containerd-shim-runc-v2`, `runc`
- Interface jaringan: `cni0`, `flannel.1`, `flannel-wg*`, `kube-ipvs0`

Kalau Panda dipasang ulang setelah install ulang OS, minta exclusion ini sekalian.

---

## 4. Token GitHub untuk cluster (±3 menit)

Repo dan image iPop **private**, jadi cluster butuh token baca-saja. Buat **dua** token di akun GitHub `HitterD`:

**a. Token baca repo (untuk Argo CD)**
1. https://github.com/settings/personal-access-tokens/new (Fine-grained token)
2. Name: `ipop-k3s-argocd`, Expiration: 1 year
3. Repository access: **Only select repositories** → `iPop-Integrated-Portal-for-Opertational-Performance-`
4. Permissions → Repository → **Contents: Read-only**
5. Generate → salin token.

**b. Token tarik image (untuk GHCR)** — satu-satunya token yang masih dibutuhkan (Argo CD sudah memakai deploy key baca-saja)
1. https://github.com/settings/tokens/new (Tokens classic — GHCR belum mendukung fine-grained)
2. Note: `ipop-k3s-ghcr`, Expiration: 1 year
3. Centang **hanya `read:packages`**
4. Generate → salin token.

Simpan keduanya di password manager. **Jangan kirim lewat chat** — Anda akan menempelkannya sendiri saat
`secrets.sh` / `install-argocd.sh` bertanya (langkah 6). Pasang pengingat kalender 11 bulan lagi untuk memperbarui.

---

## 5. Secret GitHub Actions untuk DevExpress (±5 menit)

Build di GitHub butuh paket DevExpress (tidak ada di nuget.org) dan lisensinya.

1. Login ke https://nuget.devexpress.com dengan akun DevExpress kantor → salin **Your Feed URL**
   (bentuknya `https://nuget.devexpress.com/<kunci>/api/v3/index.json`).
2. Buka https://github.com/HitterD/iPop-Integrated-Portal-for-Opertational-Performance-/settings/secrets/actions
3. **New repository secret**:
   - Name `DEVEXPRESS_NUGET_FEED` → Value: URL feed di atas.
   - Name `DEVEXPRESS_LICENSE` → Value: isi file `C:\Users\IT12\AppData\Roaming\DevExpress\DevExpress_License.txt`
     (buka dengan Notepad, Ctrl+A, Ctrl+C).
4. Settings → Actions → General → Workflow permissions → **Read and write permissions** → Save
   (supaya workflow bisa menulis tag image baru ke repo).

**Cek:** tab **Actions** di repo → workflow **CI** → **Run workflow** (atau tunggu push berikutnya) → hijau.

---

## 6. Isi password prod ke cluster (bersama saya, ±10 menit)

Setelah cluster jalan saya akan memberi tahu. Anda login SSH ke ipop-01 lalu menjalankan:

```bash
cd ~/ipop/deploy/k8s/bootstrap
bash secrets.sh
```

Skrip menanyakan tiap password (tidak tampil saat diketik). Ambil nilainya dari PC prod lama —
daftar lengkap dan lokasinya ada di [03-secrets.md](03-secrets.md). Anda juga diminta token dari langkah 4b.

---

## 7. Saat cutover: Zoraxy

Lihat [06-zoraxy.md](06-zoraxy.md). Saya kirim isian persisnya ketika uji coba Anda nyatakan normal.

---

## 8. Paling akhir: ganti password `administrator`

Setelah saya bilang selesai, di **tiap** VM:

```bash
passwd
```

Login SSH dengan password sudah saya matikan dan diganti SSH key, jadi password ini hanya untuk
console hypervisor dan `sudo`. Untuk memberi akses SSH ke orang lain, tambahkan public key mereka ke
`~/.ssh/authorized_keys`.
