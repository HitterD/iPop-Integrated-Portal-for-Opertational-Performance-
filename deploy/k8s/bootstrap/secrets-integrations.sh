#!/bin/bash
# Passwords that must be the SAME as on the old prod server, typed by an admin (hidden input).
# Safe to run again: it replaces the values and restarts the API.
#   ssh administrator@192.168.2.88
#   bash ~/ipop/deploy/k8s/bootstrap/secrets-integrations.sh
# Where each value is on the old server: docs/deploy/k3s/03-secrets.md
# Leave a prompt empty to keep the value already in the cluster (or skip it).
set -euo pipefail
export KUBECONFIG="${KUBECONFIG:-$HOME/.kube/config}"

ask() { local v; read -r -s -p "$1: " v; echo >&2; printf '%s' "$v"; }
cur() { kubectl -n ipop get secret ipop-api-integrations -o jsonpath="{.data.$1}" 2>/dev/null | base64 -d 2>/dev/null || true; }
pick() { local typed="$1" key="$2"; [ -n "$typed" ] && printf '%s' "$typed" || cur "$key"; }

echo "== iPop: passwords from the old prod server (empty = keep current)"
SMTP=$(pick "$(ask 'Smtp:Password (sysadmin.sja@kapalapi.co.id)')" Smtp__Password)
read -r -p "Synology service account user [ict]: " NAS_USER; NAS_USER=${NAS_USER:-ict}
NAS_PASS=$(pick "$(ask 'Synology:ServiceAccount:Password')" Synology__ServiceAccount__Password)
VAPID_PUB=$(pick "$(ask 'Push:VapidPublicKey')" Push__VapidPublicKey)
VAPID_PRIV=$(pick "$(ask 'Push:VapidPrivateKey')" Push__VapidPrivateKey)
IDESK=$(pick "$(ask 'IDesk:ClientSecret')" IDesk__ClientSecret)
HRIS=$(pick "$(ask 'Hris:ApiKey')" Hris__ApiKey)
OIDC=$(pick "$(ask 'Oidc:Clients:0:ClientSecret (Sign in with iPop for iDesk)')" Oidc__Clients__0__ClientSecret)
FCM=$(pick "$(ask 'Fcm:ApiKey (Android push)')" Fcm__ApiKey)

args=()
add() { [ -n "$2" ] && args+=(--from-literal="$1=$2"); return 0; }
add Smtp__Password "$SMTP"
add Synology__ServiceAccount__Username "$NAS_USER"
add Synology__ServiceAccount__Password "$NAS_PASS"
add Push__VapidPublicKey "$VAPID_PUB"
add Push__VapidPrivateKey "$VAPID_PRIV"
add IDesk__ClientSecret "$IDESK"
add Hris__ApiKey "$HRIS"
add Fcm__ApiKey "$FCM"
if [ -n "$OIDC" ]; then
  add Oidc__Clients__0__ClientId idesk
  add Oidc__Clients__0__DisplayName iDesk
  add Oidc__Clients__0__ClientSecret "$OIDC"
  add Oidc__Clients__0__RedirectUris__0 "https://idesk.santos.co.id/api/v1/auth/oidc/callback"
  add Oidc__Clients__0__PostLogoutRedirectUris__0 "https://idesk.santos.co.id/login"
  add Oidc__Clients__0__AllowPassword true
fi
kubectl -n ipop create secret generic ipop-api-integrations "${args[@]}" --dry-run=client -o yaml | kubectl apply -f -

# Backups over SMB use a dedicated local DSM account (svc-ipop-backup: SMB only, read/write on share "sja"),
# not the FileStation account - DSM refuses SMB logins for accounts without the SMB application permission.
read -r -p "NAS backup (SMB) user [svc-ipop-backup]: " BK_USER; BK_USER=${BK_USER:-svc-ipop-backup}
read -r -s -p "NAS backup (SMB) password (empty = skip): " BK_PASS; echo
if [ -n "$BK_PASS" ]; then
  kubectl -n ipop create secret generic nas-smb --from-literal=username="$BK_USER" --from-literal=password="$BK_PASS"     --dry-run=client -o yaml | kubectl apply -f -                       # nightly pg_dump + etcd snapshot copy
  kubectl -n longhorn-system create secret generic backup-cifs --from-literal=CIFS_USERNAME="$BK_USER"     --from-literal=CIFS_PASSWORD="$BK_PASS" --dry-run=client -o yaml | kubectl apply -f -   # Longhorn target nas-cifs
fi
if [ -n "$SMTP" ]; then        # alert emails
  kubectl -n monitoring create secret generic alertmanager-smtp --from-literal=password="$SMTP" \
    --dry-run=client -o yaml | kubectl apply -f -
fi

read -r -p "Path to Firebase service-account.json on this machine (empty = skip): " FB
if [ -n "$FB" ]; then
  kubectl -n ipop create secret generic ipop-firebase --from-file=service-account.json="$FB" --dry-run=client -o yaml | kubectl apply -f -
fi

read -r -p "Update the GHCR image pull token too? [y/N]: " G
if [ "${G,,}" = "y" ]; then
  read -r -p "GitHub username: " GH_USER
  GH_TOKEN=$(ask "Token with read:packages")
  kubectl -n ipop create secret docker-registry ghcr --docker-server=ghcr.io \
    --docker-username="$GH_USER" --docker-password="$GH_TOKEN" --dry-run=client -o yaml | kubectl apply -f -
fi

kubectl -n ipop rollout restart deploy/ipop-api >/dev/null 2>&1 && echo "API restarting with the new values." || true
kubectl -n monitoring rollout restart statefulset/alertmanager-kps-alertmanager >/dev/null 2>&1 || true
echo "== done"
