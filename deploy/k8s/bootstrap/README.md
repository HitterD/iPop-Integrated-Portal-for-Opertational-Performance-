# Cluster bootstrap (run once)

Everything after this is GitOps: Argo CD applies `deploy/k8s/argocd/apps/*` from GitHub `main`.

| Script | Runs on | What |
|---|---|---|
| `prepare-node.sh` | each VM (as root) | OS settings, packages, regenerate machine-id/host keys if cloned, SSH hardening |
| `install-k3s.sh first` | ipop-01 | first k3s server + kube-vip VIP 192.168.2.113 |
| `install-k3s.sh join` | ipop-02, ipop-03 | join as servers (etcd quorum of 3) |
| `install-argocd.sh` | anywhere with kubectl | Argo CD + root app → installs everything else |
| `secrets.sh` | anywhere with kubectl | prompts for every password; nothing is written to git or chat |
| `restore-db.sh` | anywhere with kubectl | loads a prod dump into the cluster's PostgreSQL |

The order and the checks between steps are in `docs/deploy/k3s/02-install-cluster.md`.
