#!/bin/sh
set -e
RADDB=/etc/freeradius
cp /kibo/clients.conf "$RADDB/clients.conf"
cp /kibo/sql "$RADDB/mods-enabled/sql"
cp /kibo/queries.conf "$RADDB/mods-config/sql/main/postgresql/kibo-queries.conf"
cp /kibo/default "$RADDB/sites-enabled/default"
cp /kibo/dynamic-clients "$RADDB/sites-enabled/dynamic-clients"
chmod 640 "$RADDB/clients.conf" "$RADDB/mods-enabled/sql" "$RADDB/mods-config/sql/main/postgresql/kibo-queries.conf" \
    "$RADDB/sites-enabled/default" "$RADDB/sites-enabled/dynamic-clients"
if [ -n "$KIBO_RADIUS_DEBUG" ]; then exec freeradius -X; fi
exec freeradius -f -l stdout
