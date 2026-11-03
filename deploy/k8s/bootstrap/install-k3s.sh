#!/bin/bash
# Install k3s as an HA server (embedded etcd). Run as root.
#   ipop-01:            sudo K3S_TOKEN=<token> bash install-k3s.sh first
#   ipop-02 / ipop-03:  sudo K3S_TOKEN=<same token> bash install-k3s.sh join
# The token is any long random string; bootstrap/secrets.sh does not need it afterwards.
set -euo pipefail

MODE="${1:?usage: install-k3s.sh first|join}"
: "${K3S_TOKEN:?set K3S_TOKEN}"
VIP="192.168.2.89"
FIRST="192.168.2.88"
K3S_VERSION="v1.36.5+k3s1"          # stable channel; upgrade one node at a time (docs/deploy/k3s/05-operations.md)
KUBE_VIP_VERSION="v1.3.1"
IFACE="$(ip -4 route show default | awk '{print $5; exit}')"
NODE_IP="$(ip -4 -o addr show "$IFACE" | awk '{print $4}' | cut -d/ -f1)"

mkdir -p /etc/rancher/k3s
cat > /etc/rancher/k3s/config.yaml <<EOF
token: "${K3S_TOKEN}"
node-ip: ${NODE_IP}
tls-san:
  - ${VIP}
  - ${NODE_IP}
  - ipop-k3s.local
write-kubeconfig-mode: "0600"
# Built-in Traefik is the ingress (tuned in deploy/k8s/platform/traefik-config.yaml).
# ServiceLB (klipper) publishes Traefik on every node; kube-vip carries the VIP.
# Nodes share images with each other (Spegel), so a pod moving after a failure does not depend on a
# slow or rate-limited Docker Hub pull. Mirrors listed in /etc/rancher/k3s/registries.yaml.
embedded-registry: true
etcd-snapshot-schedule-cron: "0 0,12 * * *"
etcd-snapshot-retention: 10
# Notice a dead node after 20 s instead of 40 s (kubelets report every 5 s; 4 missed reports).
kube-controller-manager-arg:
  - "node-monitor-grace-period=20s"
kubelet-arg:
  - "node-status-update-frequency=5s"
  - "system-reserved=cpu=500m,memory=1Gi"
  - "kube-reserved=cpu=250m,memory=512Mi"
  - "eviction-hard=memory.available<500Mi,nodefs.available<10%"
EOF

if [ "$MODE" = "first" ]; then
  echo "cluster-init: true" >> /etc/rancher/k3s/config.yaml
  # kube-vip as a static manifest: the VIP exists before anything else, and moves within seconds
  # when its node dies. ARP mode, control plane + LoadBalancer services.
  mkdir -p /var/lib/rancher/k3s/server/manifests
  curl -fsSL https://kube-vip.io/manifests/rbac.yaml -o /var/lib/rancher/k3s/server/manifests/kube-vip-rbac.yaml
  cat > /var/lib/rancher/k3s/server/manifests/kube-vip.yaml <<EOF
apiVersion: apps/v1
kind: DaemonSet
metadata:
  name: kube-vip-ds
  namespace: kube-system
spec:
  selector:
    matchLabels: { app.kubernetes.io/name: kube-vip-ds }
  template:
    metadata:
      labels: { app.kubernetes.io/name: kube-vip-ds }
    spec:
      affinity:
        nodeAffinity:
          requiredDuringSchedulingIgnoredDuringExecution:
            nodeSelectorTerms:
              - matchExpressions: [{ key: node-role.kubernetes.io/control-plane, operator: Exists }]
      containers:
        - name: kube-vip
          image: ghcr.io/kube-vip/kube-vip:${KUBE_VIP_VERSION}
          args: ["manager"]
          env:
            - { name: vip_arp, value: "true" }
            - { name: port, value: "6443" }
            - { name: vip_interface, value: "${IFACE}" }
            - { name: vip_cidr, value: "32" }
            - { name: cp_enable, value: "true" }
            - { name: cp_namespace, value: kube-system }
            - { name: svc_enable, value: "false" }
            - { name: vip_leaderelection, value: "true" }
            - { name: vip_leaseduration, value: "5" }
            - { name: vip_renewdeadline, value: "3" }
            - { name: vip_retryperiod, value: "1" }
            - { name: address, value: "${VIP}" }
          securityContext:
            capabilities: { add: [NET_ADMIN, NET_RAW] }
      hostNetwork: true
      serviceAccountName: kube-vip
      tolerations: [{ effect: NoSchedule, operator: Exists }, { effect: NoExecute, operator: Exists }]
EOF
else
  echo "server: https://${FIRST}:6443" >> /etc/rancher/k3s/config.yaml
fi

# kube-vip dies together with k3s and cannot release the VIP, so a node whose k3s was stopped kept
# answering ARP for it while another node had taken over (measured: 8 min outage). Release it on stop.
mkdir -p /etc/systemd/system/k3s.service.d
printf '[Service]\nExecStopPost=-/usr/sbin/ip addr del %s/32 dev %s\n' "$VIP" "$IFACE" \
  > /etc/systemd/system/k3s.service.d/10-release-vip.conf

printf 'mirrors:
  docker.io:
  ghcr.io:
  quay.io:
  registry.k8s.io:
' > /etc/rancher/k3s/registries.yaml

curl -sfL https://get.k3s.io | INSTALL_K3S_VERSION="$K3S_VERSION" sh -s - server

# kubectl for the administrator account.
mkdir -p /home/administrator/.kube
cp /etc/rancher/k3s/k3s.yaml /home/administrator/.kube/config
chown -R administrator: /home/administrator/.kube
chmod 600 /home/administrator/.kube/config

echo "== k3s $MODE done on $(hostname) ($NODE_IP)"
k3s kubectl get nodes -o wide
