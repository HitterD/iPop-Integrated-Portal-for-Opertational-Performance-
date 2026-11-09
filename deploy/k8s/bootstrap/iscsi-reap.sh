#!/bin/sh
# Logs out iSCSI sessions to Longhorn targets whose portal is gone (session state FREE: the old
# instance-manager pod was replaced, its pod IP no longer exists).
#
# Why: Longhorn attaches a volume on a node by first logging out any existing session for the same
# target. A dead session sits in REOPEN for node.session.timeo.replacement_timeout (120 s) per retry,
# and Longhorn gives up after 10 attempts ("failed to logout target ... refresh stale namespace").
# Measured 2026-11-09: after a node failover ipop-data stayed detached for 22 min (API down) until
# these sessions were cleared by hand. Run every minute by iscsi-reap.timer.
#
# Only sessions in state FREE for Longhorn targets are touched; a LOGGED_IN session is never changed.
set -u
command -v iscsiadm >/dev/null || exit 0
for d in /sys/class/iscsi_session/session*; do
  [ -d "$d" ] || continue
  state=$(cat "$d/state" 2>/dev/null)
  [ "$state" = "FREE" ] || continue
  target=$(cat "$d/targetname" 2>/dev/null)
  case "$target" in iqn.2019-10.io.longhorn:*) ;; *) continue;; esac
  sid=${d##*session}
  # Fail I/O on the dead path at once instead of waiting replacement_timeout, then log out.
  echo 1 > "$d/recovery_tmo" 2>/dev/null
  for blk in "$d"/device/target*/*/block/*; do
    [ -e "$blk" ] && echo 1 > "/sys/block/${blk##*/}/device/delete" 2>/dev/null
  done
  if timeout 30 iscsiadm -m session -r "$sid" -u >/dev/null 2>&1; then
    logger -t iscsi-reap "logged out stale session $sid ($target)"
  else
    logger -t iscsi-reap "could not log out stale session $sid ($target)"
  fi
done
# Node records for portals with no session left would make a later manual login hit a dead IP.
iscsiadm -m node 2>/dev/null | while read -r portal target; do
  case "$target" in iqn.2019-10.io.longhorn:*) ;; *) continue;; esac
  iscsiadm -m session 2>/dev/null | grep -q " ${portal%,*},.* $target" && continue
  iscsiadm -m node -T "$target" -p "${portal%,*}" -o delete >/dev/null 2>&1
done
exit 0
