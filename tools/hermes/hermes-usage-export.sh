#!/bin/sh
# Hermes usage export for Quotalis. Run every minute by a root cron, for example /etc/cron.d/hermes-usage-export:
#   * * * * * root HERMES_CONTAINER=... HERMES_OUTBOX=... HERMES_READER=... /usr/local/sbin/hermes-usage-export.sh 2>&1 | logger -t hermes-usage-export
# Writes $HERMES_OUTBOX/hermes-usage.json (mode 400, owned by $HERMES_READER, the read-only account Quotalis copies it with).
set -eu
C=${HERMES_CONTAINER:?set HERMES_CONTAINER to the Hermes container name}
O=${HERMES_OUTBOX:?set HERMES_OUTBOX to the folder the reader account can read}
R=${HERMES_READER:?set HERMES_READER to the read-only account}
T=$(mktemp)
trap 'rm -f "$T"' EXIT
docker exec -i "$C" python3 - < /usr/local/lib/hermes-usage-export.py > "$T"
[ -s "$T" ] || { echo "hermes-usage-export: empty output, outbox file left as it was" >&2; exit 1; }
install -m 400 -o "$R" -g "$R" "$T" "$O/.hermes-usage.json.new"
mv -f "$O/.hermes-usage.json.new" "$O/hermes-usage.json"
