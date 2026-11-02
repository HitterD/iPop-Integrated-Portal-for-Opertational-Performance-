#!/bin/bash
# Creates every secret the cluster needs. Passwords are typed at the prompt (hidden), or generated
# when there is no existing value to keep. Nothing goes to git, the chat, or shell history.
# Re-running only creates secrets that are missing; delete one first to change it.
#   bash secrets.sh
set -euo pipefail

ask() { local v; read -r -s -p "$1: " v; echo >&2; printf '%s' "$v"; }
gen() { head -c 48 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "${1:-40}"; }
have() { kubectl -n "$1" get secret "$2" >/dev/null 2>&1; }

kubectl create namespace ipop --dry-run=client -o yaml | kubectl apply -f - >/dev/null
kubectl create namespace monitoring --dry-run=client -o yaml | kubectl apply -f - >/dev/null

# --- GHCR pull access (images are private like the repo)
if ! have ipop ghcr; then
  echo "== GitHub Container Registry (token with read:packages)"
  read -r -p "GitHub username: " GH_USER
  GH_TOKEN=$(ask "Token (read:packages)")
  kubectl -n ipop create secret docker-registry ghcr --docker-server=ghcr.io \
    --docker-username="$GH_USER" --docker-password="$GH_TOKEN"
fi

# --- Database: app user password (generated; only the cluster needs it)
if ! have ipop ipop-db-app; then
  kubectl -n ipop create secret generic ipop-db-app --type=kubernetes.io/basic-auth \
    --from-literal=username=ipop --from-literal=password="$(gen 32)"
fi
DB_PASS=$(kubectl -n ipop get secret ipop-db-app -o jsonpath='{.data.password}' | base64 -d)

# --- LiveKit keys (new pair for the cluster)
if ! have ipop livekit-keys; then
  LK_KEY="ipop_$(gen 10)"; LK_SECRET="$(gen 48)"
  kubectl -n ipop create secret generic livekit-keys \
    --from-literal=keys="$LK_KEY: $LK_SECRET" --from-literal=api-key="$LK_KEY" --from-literal=api-secret="$LK_SECRET"
fi
LK_KEY=$(kubectl -n ipop get secret livekit-keys -o jsonpath='{.data.api-key}' | base64 -d)
LK_SECRET=$(kubectl -n ipop get secret livekit-keys -o jsonpath='{.data.api-secret}' | base64 -d)

# --- API secrets. Copy these values from the current prod server (appsettings.Local.json + user secrets)
#     so sessions, push subscriptions and integrations keep working after the move.
if ! have ipop ipop-api-secrets; then
  echo "== iPop API secrets — paste the CURRENT prod values (see docs/deploy/k3s/03-secrets.md for where each one is)"
  JWT=$(ask "Jwt:Key (prod; keeps everyone signed in)")
  SMTP=$(ask "Smtp:Password (sysadmin.sja@kapalapi.co.id)")
  NAS_USER=$(read -r -p "Synology service account user [ict]: " u; echo "${u:-ict}")
  NAS_PASS=$(ask "Synology:ServiceAccount:Password")
  VAPID_PUB=$(ask "Push:VapidPublicKey")
  VAPID_PRIV=$(ask "Push:VapidPrivateKey")
  IDESK=$(ask "IDesk:ClientSecret")
  HRIS=$(ask "Hris:ApiKey")
  OIDC=$(ask "Oidc:Clients:0:ClientSecret (iDesk sign-in)")
  FCM_KEY=$(ask "Fcm:ApiKey")
  kubectl -n ipop create secret generic ipop-api-secrets \
    --from-literal=ConnectionStrings__IPopDb="Host=ipop-db-rw;Port=5432;Database=ipop;Username=ipop;Password=${DB_PASS};Timeout=5" \
    --from-literal=Jwt__Key="$JWT" --from-literal=Jwt__Issuer=ipop --from-literal=Jwt__Audience=ipop \
    --from-literal=Smtp__Password="$SMTP" \
    --from-literal=Synology__ServiceAccount__Username="$NAS_USER" --from-literal=Synology__ServiceAccount__Password="$NAS_PASS" \
    --from-literal=Push__VapidPublicKey="$VAPID_PUB" --from-literal=Push__VapidPrivateKey="$VAPID_PRIV" \
    --from-literal=IDesk__ClientSecret="$IDESK" --from-literal=Hris__ApiKey="$HRIS" \
    --from-literal=Oidc__Clients__0__ClientId=idesk --from-literal=Oidc__Clients__0__DisplayName=iDesk \
    --from-literal=Oidc__Clients__0__ClientSecret="$OIDC" \
    --from-literal=Oidc__Clients__0__RedirectUris__0="https://idesk.santos.co.id/api/v1/auth/oidc/callback" \
    --from-literal=Oidc__Clients__0__PostLogoutRedirectUris__0="https://idesk.santos.co.id/login" \
    --from-literal=Oidc__Clients__0__AllowPassword=true \
    --from-literal=Fcm__ApiKey="$FCM_KEY" \
    --from-literal=Calls__LiveKit__ApiKey="$LK_KEY" --from-literal=Calls__LiveKit__ApiSecret="$LK_SECRET"
  # NAS account for the SMB backup mount (same service account).
  kubectl -n ipop create secret generic nas-smb --from-literal=username="$NAS_USER" --from-literal=password="$NAS_PASS"
fi

# --- Firebase service account (Android push): the JSON file from C:/ipop-ops/firebase
if ! have ipop ipop-firebase; then
  read -r -p "Path to Firebase service-account.json (empty = skip, Android push off): " FB
  [ -n "$FB" ] && kubectl -n ipop create secret generic ipop-firebase --from-file=service-account.json="$FB"
fi

# --- Monitoring
if ! have monitoring grafana-admin; then
  GP="$(gen 20)"
  kubectl -n monitoring create secret generic grafana-admin --from-literal=admin-user=admin --from-literal=admin-password="$GP"
  echo "Grafana admin password (save it in your password manager): $GP"
fi
if ! have monitoring alertmanager-smtp; then
  SMTP_A=${SMTP:-$(ask "SMTP password for alert emails (sysadmin.sja)")}
  kubectl -n monitoring create secret generic alertmanager-smtp --from-literal=password="$SMTP_A"
fi
echo "== secrets done"
