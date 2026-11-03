#!/bin/bash
# Prepare one Ubuntu 26.04 LTS VM for k3s. Run as root on each node:  sudo bash prepare-node.sh
# Idempotent: safe to run again.
set -euo pipefail

echo "== $(hostname): preparing for k3s"

# 1. Unique identity. Cloned VMs share /etc/machine-id and SSH host keys, which breaks k3s node
#    identity, Longhorn and SSH host verification. Regenerate if this node still has the clone's id.
CLONE_ID="418781a045324b1672f0f15288dd5df1"   # ipop-01's own id (product_uuid); a node whose product_uuid differs but carries it is a clone
if [ "$(cat /etc/machine-id)" = "$CLONE_ID" ]; then
  echo "-- cloned machine-id found: regenerating machine-id and SSH host keys"
  rm -f /etc/machine-id /var/lib/dbus/machine-id
  systemd-machine-id-setup
  ln -sf /etc/machine-id /var/lib/dbus/machine-id
  rm -f /etc/ssh/ssh_host_*
  ssh-keygen -A
  systemctl restart ssh
fi

# 2. Time zone + NTP (etcd and certificates need synced clocks).
timedatectl set-timezone Asia/Jakarta
timedatectl set-ntp true

# 3. Packages: Longhorn needs open-iscsi + nfs client; SMB backups need cifs-utils.
apt-get update -qq
DEBIAN_FRONTEND=noninteractive apt-get install -y -qq open-iscsi nfs-common cifs-utils jq curl ca-certificates \
  apparmor apparmor-utils >/dev/null
systemctl enable --now iscsid

# 4. Kernel: no swap for kubelet, modules + sysctls for networking and Longhorn.
swapoff -a
sed -i -E 's@^([^#].*\sswap\s.*)$@# \1  # disabled for k3s@' /etc/fstab
cat > /etc/modules-load.d/k3s.conf <<'EOF'
br_netfilter
overlay
iscsi_tcp
dm_crypt
EOF
for m in br_netfilter overlay iscsi_tcp dm_crypt; do modprobe "$m" || true; done
cat > /etc/sysctl.d/90-k3s.conf <<'EOF'
net.ipv4.ip_forward = 1
net.bridge.bridge-nf-call-iptables = 1
net.bridge.bridge-nf-call-ip6tables = 1
fs.inotify.max_user_instances = 8192
fs.inotify.max_user_watches = 1048576
vm.max_map_count = 262144
# LiveKit: larger UDP buffers for media.
net.core.rmem_max = 5000000
net.core.wmem_max = 5000000
EOF
sysctl --system >/dev/null

# 5. Firewall stays off (ufw inactive): cluster traffic is pod-to-pod across nodes. The LAN firewall
#    is the boundary. If ufw is ever enabled, open: 6443, 2379-2380, 10250, 8472/udp, 51820-51821/udp,
#    80, 443, 7881, 3478/udp, 50000-50100/udp.
ufw disable >/dev/null 2>&1 || true

# 6. Unattended upgrades: security patches only, never an automatic reboot (we drain nodes one by one).
cat > /etc/apt/apt.conf.d/52ipop-no-auto-reboot <<'EOF'
Unattended-Upgrade::Automatic-Reboot "false";
EOF

# 7. Multipath must ignore Longhorn's iSCSI devices.
if [ -f /etc/multipath.conf ] && ! grep -q 'devnode "^sd\[a-z0-9\]+"' /etc/multipath.conf; then
  printf '\nblacklist {\n    devnode "^sd[a-z0-9]+"\n}\n' >> /etc/multipath.conf
  systemctl restart multipathd || true
fi

echo "== $(hostname): ready (machine-id $(cut -c1-8 /etc/machine-id)…)"
