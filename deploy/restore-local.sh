#!/usr/bin/env bash
# Loads a server backup into a PostgreSQL on your Mac (in Docker), so you can run eListings locally with the server's data.
#
#   bash deploy/restore-local.sh                          → newest dump in ~/elistings-backups
#   bash deploy/restore-local.sh path/to/file.dump        → a specific dump
#   bash deploy/restore-local.sh --from zee@2.29.130.158  → back up the server first, then load it
#
# Needs Docker Desktop running. The local database lives in the Docker volume "elistings_local_pgdata"
# (container "elistings-local-pg", port 5440 – change with LOCAL_PG_PORT=5441 ...). It is REPLACED on every run.
#
# Safety: in the local copy, every supplier's "Enable background sync" is switched off, so the local app doesn't start
# overnight imports. The copy still holds your real eBay connections – publishing locally posts to real eBay.

set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/.."

NAME="elistings-local-pg"
PORT="${LOCAL_PG_PORT:-5440}"
DB=elistings
DB_USER=elistings
PASSWORD=local
DIR="$HOME/elistings-backups"

if [[ "${1:-}" == "--from" ]]; then
  bash deploy/backup-server.sh "${2:?Usage: --from user@server-ip}" "$DIR"
  FILE="$(ls -1t "$DIR"/elistings-*.dump | head -1)"
else
  FILE="${1:-$(ls -1t "$DIR"/elistings-*.dump 2>/dev/null | head -1 || true)}"
fi
[[ -n "${FILE:-}" && -s "$FILE" ]] || { echo "No backup found. Run: bash deploy/backup-server.sh zee@SERVER_IP" >&2; exit 1; }
command -v docker >/dev/null || { echo "Docker isn't installed (install Docker Desktop)." >&2; exit 1; }
docker info >/dev/null 2>&1 || { echo "Docker isn't running – start Docker Desktop." >&2; exit 1; }

# Start (or create) the local PostgreSQL.
if docker ps -a --format '{{.Names}}' | grep -qx "$NAME"; then
  docker start "$NAME" >/dev/null
else
  docker run -d --name "$NAME" \
    -e POSTGRES_USER="$DB_USER" -e POSTGRES_PASSWORD="$PASSWORD" -e POSTGRES_DB="$DB" \
    -p "127.0.0.1:$PORT:5432" -v elistings_local_pgdata:/var/lib/postgresql/data \
    postgres:17-alpine >/dev/null
fi
echo -n "Waiting for the local database"
for _ in $(seq 1 30); do
  docker exec "$NAME" pg_isready -U "$DB_USER" >/dev/null 2>&1 && break
  echo -n "."; sleep 1
done
echo

echo "Loading $FILE into the local database (replaces what's there)..."
docker exec "$NAME" psql -q -U "$DB_USER" -d postgres -c "DROP DATABASE IF EXISTS $DB WITH (FORCE);" -c "CREATE DATABASE $DB;"
docker exec -i "$NAME" pg_restore -U "$DB_USER" -d "$DB" --no-owner --no-acl < "$FILE"
docker exec "$NAME" psql -q -U "$DB_USER" -d "$DB" -c 'UPDATE "Suppliers" SET "BackgroundSyncEnabled" = false;'

PORT_IN_USE="$(docker port "$NAME" 5432/tcp | head -1 | sed 's/.*://')"
cat <<EOF

Done. To run eListings locally on this data (in a new terminal, from the project folder):

  export ConnectionStrings__DefaultConnection='Host=localhost;Port=${PORT_IN_USE};Database=${DB};Username=${DB_USER};Password=${PASSWORD}'
  dotnet run --project Server

Sign in with your server account. Without those lines the app uses your normal local database (SQLite, Server/app.db).
Stop the local database: docker stop $NAME
EOF
