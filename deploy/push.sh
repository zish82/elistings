#!/usr/bin/env bash
# Run on your Mac, from the project folder, to send the latest code to the server and restart it.
#
#   bash deploy/push.sh zee@2.29.130.158          (first time: copies the code only)
#   bash deploy/push.sh zee@2.29.130.158 --restart (also rebuilds and restarts on the server)
#
# Your local secrets (.env, Server/appsettings.Development.json) and databases are never copied.

set -euo pipefail
TARGET="${1:?Usage: bash deploy/push.sh user@server-ip [--restart]}"
RESTART="${2:-}"
cd "$(dirname "${BASH_SOURCE[0]}")/.."

rsync -av --delete \
  --exclude .git --exclude bin --exclude obj --exclude '*.db' --exclude '*.db-*' \
  --exclude .env --exclude 'Server/appsettings.Development.json' \
  --exclude 'sites/digitalclub/dist' --exclude node_modules \
  ./ "$TARGET:~/elistings/"

if [[ "$RESTART" == "--restart" ]]; then
  ssh "$TARGET" 'cd ~/elistings && docker compose --profile https up -d --build && docker image prune -f >/dev/null && docker compose ps'
fi
echo "Done."
