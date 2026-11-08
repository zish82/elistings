#!/usr/bin/env bash
# Backs up the server's database to your Mac (PostgreSQL custom-format dump).
#
#   bash deploy/backup-server.sh zee@2.29.130.158              → ~/elistings-backups/elistings-YYYY-MM-DD-HHMM.dump
#   bash deploy/backup-server.sh zee@2.29.130.158 ~/Dropbox/db → another folder
#
# Keeps the newest 30 dumps in that folder. Restore one locally with deploy/restore-local.sh.

set -euo pipefail
TARGET="${1:?Usage: bash deploy/backup-server.sh user@server-ip [folder]}"
DIR="${2:-$HOME/elistings-backups}"
mkdir -p "$DIR"
FILE="$DIR/elistings-$(date +%F-%H%M).dump"

echo "Backing up the server database to $FILE ..."
# pg_dump runs inside the server's database container, using its own POSTGRES_USER / POSTGRES_DB.
ssh "$TARGET" "cd ~/elistings && docker compose exec -T db sh -c 'pg_dump -U \"\$POSTGRES_USER\" -d \"\$POSTGRES_DB\" -Fc'" > "$FILE.part"

if [[ ! -s "$FILE.part" ]]; then
  rm -f "$FILE.part"
  echo "Backup failed: the dump is empty." >&2
  exit 1
fi
mv "$FILE.part" "$FILE"
echo "Done: $FILE ($(du -h "$FILE" | cut -f1))"

# Keep the newest 30.
ls -1t "$DIR"/elistings-*.dump 2>/dev/null | tail -n +31 | while read -r old; do rm -f "$old"; done
