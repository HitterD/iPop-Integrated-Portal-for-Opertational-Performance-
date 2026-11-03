#!/bin/bash
# Load a pg_dump (custom format) into the cluster's iPop database, replacing what is there.
#   bash restore-db.sh /path/to/ipop.dump
# Used for the test copy and again at cutover. Stops the API first so nothing writes meanwhile.
set -euo pipefail
DUMP="${1:?usage: restore-db.sh <file.dump>}"
NS=ipop

PRIMARY=$(kubectl -n $NS get pods -l cnpg.io/cluster=ipop-db,cnpg.io/instanceRole=primary -o jsonpath='{.items[0].metadata.name}')
echo "Primary: $PRIMARY"
read -r -p "This REPLACES the cluster's iPop database with $DUMP. Type RESTORE to continue: " ok
[ "$ok" = "RESTORE" ] || exit 1

# The ipop app has no auto-sync (manual release), so Argo CD will not start the API again mid-restore.
kubectl -n $NS scale deploy/ipop-api --replicas=0
kubectl -n $NS wait --for=delete pod -l app.kubernetes.io/name=ipop-api --timeout=120s || true

kubectl -n $NS cp "$DUMP" "$PRIMARY:/var/lib/postgresql/data/restore.dump" -c postgres
kubectl -n $NS exec "$PRIMARY" -c postgres -- bash -ec '
  psql -v ON_ERROR_STOP=1 -d postgres -c "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '"'"'ipop'"'"' AND pid <> pg_backend_pid();"
  psql -v ON_ERROR_STOP=1 -d postgres -c "DROP DATABASE IF EXISTS ipop;" -c "CREATE DATABASE ipop OWNER ipop;"
  pg_restore -d ipop --no-owner --role=ipop --exit-on-error /var/lib/postgresql/data/restore.dump
  rm -f /var/lib/postgresql/data/restore.dump
  psql -d ipop -tAc "SELECT count(*) || '"'"' users'"'"' FROM \"Users\";"'

kubectl -n $NS scale deploy/ipop-api --replicas=1
kubectl -n $NS rollout status deploy/ipop-api --timeout=300s
echo "== restore done"
