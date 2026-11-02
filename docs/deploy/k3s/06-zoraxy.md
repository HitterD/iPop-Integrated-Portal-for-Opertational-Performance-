# Zoraxy (192.168.2.2) — setting saat cutover

Selama uji coba **jangan ubah apa pun** di Zoraxy: uji langsung lewat http://ipop.192.168.2.113.nip.io.
Kerjakan ini hanya pada hari cutover ([07-cutover.md](07-cutover.md)).

Login: http://192.168.2.2:8000

## 1. Proxy rule iPop (inti)

**HTTP Proxy → Create Proxy Rule** (atau edit yang sudah ada kalau domain iPop sudah ada di Zoraxy):

| Kolom | Isi |
|---|---|
| Proxy Type | Sub-domain |
| Matching Keyword / Domain | `ipop.santos.co.id` |
| Target IP Address or Domain Name with port | `192.168.2.113:80` |
| Proxy Target require TLS Connection | **tidak dicentang** |
| Skip TLS Validation | tidak relevan |
| Require Basic Auth | tidak |

Lalu buka rule itu → **Edit**:
- **Allow WebSocket**/**Bypass WebSocket Origin Check**: biarkan default (WebSocket otomatis didukung — dipakai chat, notifikasi, panggilan).
- **Custom Headers**: **jangan tambah** `X-Forwarded-Proto`/`X-Forwarded-For` — Zoraxy sudah mengirimnya, dan duplikat
  membuat ONLYOFFICE error -4 (masalah lama di NPM). Traefik di cluster hanya mempercayai header dari 192.168.2.2.
- Max upload: kalau ada opsi batas ukuran body, set ≥ 600 MB (lampiran mail sampai 500 MB).

## 2. Sertifikat + paksa HTTPS

1. **TLS / SSL certificates** → upload sertifikat `ipop.santos.co.id` (sama dengan yang sekarang di NPM 192.168.2.3:
   file `.crt`/fullchain dan `.key`), atau pakai ACME kalau domainnya publik.
2. Halaman **Status** (Global Settings):
   - **Use TLS to serve proxy request** → ON (port 443)
   - **Force redirect HTTP to HTTPS** → ON

Menu **Redirection Rules** tidak perlu diisi.

## 3. DNS

Ubah DNS internal `ipop.santos.co.id` dari **192.168.2.3** (NPM lama) ke **192.168.2.2** (Zoraxy).
TTL pendek (300 detik) sehari sebelumnya supaya perpindahan cepat.

## 4. Media panggilan (tidak lewat Zoraxy)

Sinyal LiveKit lewat Zoraxy di path `/rtc` (rule di atas sudah mencakupnya). Media suara/video **langsung**
dari PC user ke node LiveKit:

| Port | Tujuan |
|---|---|
| UDP 50000–50100 | 192.168.2.88 dan 192.168.2.89 |
| TCP 7881 | 192.168.2.88 dan 192.168.2.89 |
| UDP 3478 (STUN) | 192.168.2.88 dan 192.168.2.89 |

Dalam satu LAN tidak ada yang perlu dibuka. Kalau ada firewall antar subnet/site (misalnya Karawang, Jatibening,
Semarang), minta port di atas dibuka dari subnet-subnet itu ke dua IP tersebut.

## 5. Cek setelah setting

```text
https://ipop.santos.co.id                   → halaman login iPop
https://ipop.santos.co.id/healthz           → {"status":"ok"}
https://ipop.santos.co.id/onlyoffice/       → mengarah ke .../welcome/
```
Saya akan mengetes chat realtime, panggilan, ONLYOFFICE, upload, dan Sign in with iPop dari iDesk.

## Rollback

Edit rule `ipop.santos.co.id` → Target kembali ke alamat lama (atau kembalikan DNS ke 192.168.2.3). Selesai dalam 1 menit.
