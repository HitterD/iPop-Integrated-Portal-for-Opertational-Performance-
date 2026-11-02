#!/bin/sh
# The content root is the persistent /data volume (uploads, caches, logs), but the shipped
# appsettings*.json live in /app. Refresh them into /data on every start so a new image's
# settings always win over an old copy on the volume. appsettings.Production.json is mounted
# from a ConfigMap at /config and copied the same way.
set -e
cp -f /app/appsettings.json /data/appsettings.json
[ -f /config/appsettings.Production.json ] && cp -f /config/appsettings.Production.json /data/appsettings.Production.json
exec dotnet /app/IPop.Api.dll --contentRoot /data "$@"
