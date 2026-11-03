#!/bin/bash
# Secrets the cluster can make on its own (random values). Non-interactive, idempotent:
# existing secrets are kept. Run once after k3s is up:   bash secrets.sh
# The passwords that must match the old prod server (SMTP, NAS, iDesk, …) are entered by an
# admin with secrets-integrations.sh — never in git, never in chat.
set -euo pipefail

gen() { head -c 64 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c "${1:-40}"; }
have() { kubectl -n "$1" get secret "$2" >/dev/null 2>&1; }
for ns in ipop ipop-staging monitoring; do kubectl create namespace $ns --dry-run=client -o yaml | kubectl apply -f - >/dev/null; done

if ! have ipop ipop-db-app; then
  kubectl -n ipop create secret generic ipop-db-app --type=kubernetes.io/basic-auth \
    --from-literal=username=ipop --from-literal=password="$(gen 32)"
fi
DB_PASS=$(kubectl -n ipop get secret ipop-db-app -o jsonpath='{.data.password}' | base64 -d)

if ! have ipop livekit-keys; then
  K="ipop_$(gen 10)"; S="$(gen 48)"
  kubectl -n ipop create secret generic livekit-keys \
    --from-literal=keys="$K: $S" --from-literal=api-key="$K" --from-literal=api-secret="$S"
fi
LK_KEY=$(kubectl -n ipop get secret livekit-keys -o jsonpath='{.data.api-key}' | base64 -d)
LK_SECRET=$(kubectl -n ipop get secret livekit-keys -o jsonpath='{.data.api-secret}' | base64 -d)

# Jwt:Key is NEW and random. (The old server's key is the sample value committed in
# appsettings.Development.json, so it must not be reused; a new key signs everyone out once.)
if ! have ipop ipop-api-secrets; then
  kubectl -n ipop create secret generic ipop-api-secrets \
    --from-literal=ConnectionStrings__IPopDb="Host=ipop-db-rw;Port=5432;Database=ipop;Username=ipop;Password=${DB_PASS};Timeout=5" \
    --from-literal=Jwt__Key="$(gen 64)" --from-literal=Jwt__Issuer=ipop --from-literal=Jwt__Audience=ipop \
    --from-literal=Calls__LiveKit__ApiKey="$LK_KEY" --from-literal=Calls__LiveKit__ApiSecret="$LK_SECRET"
fi

# Placeholder pull secret (images are loaded onto the nodes during the test phase);
# secrets-integrations.sh replaces it with a real read:packages token.
if ! have ipop ghcr; then
  kubectl -n ipop create secret docker-registry ghcr --docker-server=ghcr.io --docker-username=unset --docker-password=unset
fi

# Longhorn UI has no login of its own: basic auth in front (user admin).
if ! have longhorn-system longhorn-auth; then
  LP="$(gen 20)"
  kubectl -n longhorn-system create secret generic longhorn-auth     --from-literal=users="$(htpasswd -nbB admin "$LP" 2>/dev/null || printf 'admin:%s' "$(openssl passwd -apr1 "$LP")")"
  echo "Longhorn UI: user admin, password $LP  (save it in your password manager)"
fi

if ! have monitoring grafana-admin; then
  kubectl -n monitoring create secret generic grafana-admin \
    --from-literal=admin-user=admin --from-literal=admin-password="$(gen 20)"
fi
# Placeholder so Alertmanager can start; secrets-integrations.sh puts the real SMTP password in.
if ! have monitoring alertmanager-smtp; then
  kubectl -n monitoring create secret generic alertmanager-smtp --from-literal=password=unset
fi
# --- Staging (namespace ipop-staging): its own DB password and JWT key; LiveKit keys and pull secret shared.
if ! have ipop-staging ipop-db-app; then
  kubectl -n ipop-staging create secret generic ipop-db-app --type=kubernetes.io/basic-auth     --from-literal=username=ipop --from-literal=password="$(gen 32)"
fi
SDB=$(kubectl -n ipop-staging get secret ipop-db-app -o jsonpath='{.data.password}' | base64 -d)
if ! have ipop-staging ipop-api-secrets; then
  kubectl -n ipop-staging create secret generic ipop-api-secrets     --from-literal=ConnectionStrings__IPopDb="Host=ipop-db-rw;Port=5432;Database=ipop;Username=ipop;Password=${SDB};Timeout=5"     --from-literal=Jwt__Key="$(gen 64)" --from-literal=Jwt__Issuer=ipop --from-literal=Jwt__Audience=ipop     --from-literal=Calls__LiveKit__ApiKey="$LK_KEY" --from-literal=Calls__LiveKit__ApiSecret="$LK_SECRET"
fi
if ! have ipop-staging ghcr; then
  kubectl -n ipop get secret ghcr -o json | python3 -c "import sys,json;d=json.load(sys.stdin);[d['metadata'].pop(k,None) for k in ('namespace','uid','resourceVersion','creationTimestamp','managedFields','ownerReferences')];print(json.dumps(d))"     | kubectl -n ipop-staging apply -f -
fi

echo "== generated secrets ready"
