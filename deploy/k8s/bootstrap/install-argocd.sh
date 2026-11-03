#!/bin/bash
# Install Argo CD and hand the cluster to GitOps. Run once, with kubectl pointing at the cluster:
#   bash install-argocd.sh
# Needs: helm, kubectl, and the private half of the repo's read-only deploy key.
set -euo pipefail
cd "$(dirname "$0")/.."

REPO="git@github.com:HitterD/iPop-Integrated-Portal-for-Opertational-Performance-.git"

# Labels used by LiveKit placement: ipop-01 media (preferred), ipop-02 media standby.
kubectl label node ipop-01 ipop/media=true ipop/media-primary=true --overwrite
kubectl label node ipop-02 ipop/media=true --overwrite

helm repo add argo https://argoproj.github.io/argo-helm >/dev/null
helm repo update argo >/dev/null
helm upgrade --install argocd argo/argo-cd --version 10.11.6 -n argocd --create-namespace \
  -f values/argocd.yaml --wait --timeout 10m

# Repo access: a read-only GitHub deploy key (repo › Settings › Deploy keys), private half given as $1.
if ! kubectl -n argocd get secret repo-ipop >/dev/null 2>&1; then
  KEY="${1:?usage: install-argocd.sh <path to the deploy key private file>}"
  kubectl -n argocd create secret generic repo-ipop     --from-literal=type=git --from-literal=url="$REPO" --from-file=sshPrivateKey="$KEY"
  kubectl -n argocd label secret repo-ipop argocd.argoproj.io/secret-type=repository
fi

kubectl apply -f argocd/root.yaml
echo
echo "Argo CD: http://argocd.192.168.2.89.nip.io  (user admin)"
echo "First password:"
kubectl -n argocd get secret argocd-initial-admin-secret -o jsonpath='{.data.password}' | base64 -d; echo
