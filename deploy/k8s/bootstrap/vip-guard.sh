#!/bin/sh
# Drops the cluster VIP from this node when k3s is not running here.
# kube-vip lives in a container; when k3s stops, the container can re-add the address after k3s's own
# ExecStopPost removed it, and a dead node then keeps answering ARP for the VIP while another node holds
# it too (measured: 5–8 min of total outage). Run every 5 s by vip-guard.timer, and after k3s-killall.
VIP="${1:?usage: vip-guard.sh <vip> <iface>}"; IFACE="${2:?}"
if ! systemctl is-active --quiet k3s && ip -4 addr show dev "$IFACE" | grep -q " $VIP/"; then
  ip addr del "$VIP/32" dev "$IFACE" && logger -t vip-guard "released $VIP (k3s not running)"
fi
