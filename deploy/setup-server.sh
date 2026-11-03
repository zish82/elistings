#!/usr/bin/env bash
# One-shot server setup for eListings + the Digital Club website on Ubuntu 24.04 (e.g. Hetzner CX23).
#
#   Run on the server, from the project folder:   sudo bash deploy/setup-server.sh
#
# Safe to run again at any time: every step checks first and skips what is already done.
# It never turns off SSH passwords unless your user already has an SSH key on the server (so you can't lock yourself out).
#
# What it does:
#   1. System: updates, automatic security updates, fail2ban, 2 GB swap, UK timezone
#   2. User: makes sure APP_USER exists, has sudo and your SSH key (copied from root if needed)
#   3. SSH: keys only, no root login (checked with `sshd -t` before restarting)
#   4. Docker: installs it and lets APP_USER use it
#   5. App: checks .env, starts database + app privately, waits until healthy
#   6. Public: after you confirm the admin account exists, starts HTTPS (Caddy + website)
#   7. Backups: nightly database dump at 03:00, kept 14 days
#
# Still manual (see docs/DEPLOY_HETZNER.md): SSH key on your Mac, Hetzner firewall, copying the code, filling in .env,
# creating the admin account through the SSH tunnel, and the eBay accepted URL.

set -euo pipefail

APP_USER="${APP_USER:-${SUDO_USER:-}}"
[[ -z "$APP_USER" || "$APP_USER" == "root" ]] && APP_USER=zee
APP_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SSHD_DIR=/etc/ssh/sshd_config.d

green() { printf '\033[32m✔ %s\033[0m\n' "$*"; }
info()  { printf '\033[36m→ %s\033[0m\n' "$*"; }
warn()  { printf '\033[33m! %s\033[0m\n' "$*"; }
fail()  { printf '\033[31m✖ %s\033[0m\n' "$*"; exit 1; }

[[ $EUID -eq 0 ]] || fail "Run with sudo:  sudo bash deploy/setup-server.sh"
[[ -f "$APP_DIR/docker-compose.yml" ]] || fail "docker-compose.yml not found in $APP_DIR – run this from the copied project."
[[ "$APP_USER" != "root" ]] || fail "Run it as your own user with sudo (or set APP_USER=zee), not as root."

# ------------------------------------------------------------------ 1. System
info "1/7 System updates (can take a few minutes)"
export DEBIAN_FRONTEND=noninteractive
apt-get update -q
apt-get -y -q upgrade
apt-get -y -q install unattended-upgrades fail2ban curl ca-certificates
cat > /etc/apt/apt.conf.d/20auto-upgrades <<'EOF'
APT::Periodic::Update-Package-Lists "1";
APT::Periodic::Unattended-Upgrade "1";
EOF
systemctl enable --now fail2ban >/dev/null
green "Updates installed; automatic security updates and fail2ban on"

if swapon --show=NAME --noheadings | grep -q .; then
  green "Swap already on ($(swapon --show=SIZE --noheadings | head -1 | tr -d ' '))"
else
  fallocate -l 2G /swapfile && chmod 600 /swapfile && mkswap /swapfile >/dev/null && swapon /swapfile
  green "2 GB swap created"
fi
if ! grep -q '^/swapfile ' /etc/fstab 2>/dev/null && [[ -f /swapfile ]]; then echo '/swapfile none swap sw 0 0' >> /etc/fstab; fi

timedatectl set-timezone Europe/London
green "Timezone: Europe/London"

# ------------------------------------------------------------------ 2. User + SSH key
info "2/7 User $APP_USER"
if ! id "$APP_USER" >/dev/null 2>&1; then
  adduser --gecos "" "$APP_USER"
fi
usermod -aG sudo "$APP_USER"
USER_HOME="$(getent passwd "$APP_USER" | cut -d: -f6)"
USER_KEYS="$USER_HOME/.ssh/authorized_keys"
install -d -m 700 -o "$APP_USER" -g "$APP_USER" "$USER_HOME/.ssh"
if [[ ! -s "$USER_KEYS" && -s /root/.ssh/authorized_keys ]]; then
  cp /root/.ssh/authorized_keys "$USER_KEYS"
  info "Copied root's SSH key(s) to $APP_USER"
fi
[[ -f "$USER_KEYS" ]] && chown "$APP_USER:$APP_USER" "$USER_KEYS" && chmod 600 "$USER_KEYS"
HAS_KEY=false
grep -qE '^(ssh-ed25519|ssh-rsa|ecdsa-sha2-)' "$USER_KEYS" 2>/dev/null && HAS_KEY=true
if $HAS_KEY; then green "$APP_USER has sudo and an SSH key"; else warn "$APP_USER has no SSH key yet – on your Mac run:  ssh-copy-id $APP_USER@<server-ip>"; fi

# ------------------------------------------------------------------ 3. SSH hardening
info "3/7 SSH"
# sshd uses the FIRST value it reads, and reads these files in name order, so ours is 00-… (before Hetzner's 50-cloud-init.conf).
rm -f "$SSHD_DIR/99-hardening.conf"
if $HAS_KEY; then
  cat > "$SSHD_DIR/00-hardening.conf" <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
EOF
  [[ -f "$SSHD_DIR/50-cloud-init.conf" ]] && sed -i 's/^PasswordAuthentication yes/PasswordAuthentication no/' "$SSHD_DIR/50-cloud-init.conf"
else
  # No key yet: block root, keep passwords so you can still add your key.
  cat > "$SSHD_DIR/00-hardening.conf" <<'EOF'
PermitRootLogin no
EOF
fi
sshd -t || fail "SSH settings are invalid – not restarting SSH. Check $SSHD_DIR."
systemctl restart ssh
if $HAS_KEY; then green "SSH: keys only, no root login"; else warn "SSH: root login off; passwords still on until $APP_USER has a key (run this script again after ssh-copy-id)"; fi

# ------------------------------------------------------------------ 4. Docker
info "4/7 Docker"
if ! command -v docker >/dev/null 2>&1; then
  curl -fsSL https://get.docker.com | sh
fi
usermod -aG docker "$APP_USER"
systemctl enable --now docker >/dev/null
docker compose version >/dev/null || fail "docker compose is not available"
green "Docker ready ($(docker compose version --short))"

# ------------------------------------------------------------------ 5. App (private)
info "5/7 App settings (.env)"
cd "$APP_DIR"
ENV_FILE="$APP_DIR/.env"
if [[ ! -f "$ENV_FILE" ]]; then
  cp .env.example "$ENV_FILE"
  chown "$APP_USER:$APP_USER" "$ENV_FILE"; chmod 600 "$ENV_FILE"
  warn "Created .env from the template. Fill it in (nano .env), then run this script again."
  warn "A strong database password you can use for POSTGRES_PASSWORD:  $(openssl rand -base64 32)"
  exit 0
fi
chmod 600 "$ENV_FILE"; chown "$APP_USER:$APP_USER" "$ENV_FILE"

# Every line must be blank, a # comment, or NAME=value (no quotes/commas from JSON, no spaces around =).
BAD_LINES="$(grep -nvE '^\s*$|^\s*#|^[A-Za-z_][A-Za-z0-9_]*=' "$ENV_FILE" | cut -d: -f1 | paste -sd, - || true)"
[[ -z "$BAD_LINES" ]] || fail ".env line(s) $BAD_LINES aren't NAME=value. Fix with: nano .env"
env_value() { { grep -E "^$1=" "$ENV_FILE" || true; } | tail -1 | cut -d= -f2- | sed 's/^"\(.*\)"$/\1/'; }
MISSING=()
for name in POSTGRES_PASSWORD DOMAIN EBAY_APP_ID EBAY_CERT_ID EBAY_RUNAME; do
  [[ -n "$(env_value "$name")" ]] || MISSING+=("$name")
done
[[ ${#MISSING[@]} -eq 0 ]] || fail ".env is missing: ${MISSING[*]}. Fill them in (nano .env), then run again."
[[ "$(env_value POSTGRES_PASSWORD)" != change-me* ]] || fail "POSTGRES_PASSWORD is still the example value. Use:  $(openssl rand -base64 32)"
DOMAIN="$(env_value DOMAIN)"
SITE_DOMAIN="$(env_value SITE_DOMAIN)"
green ".env looks complete (app: $DOMAIN${SITE_DOMAIN:+, website: $SITE_DOMAIN})"

info "Starting database + app (the first build takes 5–10 minutes)"
docker compose up -d --build db app
DB_ID="$(docker compose ps -q db)"
for _ in $(seq 1 60); do
  [[ "$(docker inspect -f '{{.State.Health.Status}}' "$DB_ID" 2>/dev/null)" == "healthy" ]] && break
  sleep 3
done
[[ "$(docker inspect -f '{{.State.Health.Status}}' "$DB_ID")" == "healthy" ]] || fail "Database didn't become healthy. See: docker compose logs db"
sleep 5
[[ "$(docker inspect -f '{{.State.Running}}' "$(docker compose ps -q app)")" == "true" ]] || fail "App isn't running. See: docker compose logs app"
green "Database healthy, app running (only reachable from the server itself)"

# ------------------------------------------------------------------ 6. Public (HTTPS)
info "6/7 Going public"
SERVER_IP="$(curl -4 -fsS --max-time 5 https://ifconfig.me || hostname -I | awk '{print $1}')"
echo
echo "  The first person to open /setup-admin becomes admin, so create your admin BEFORE going public."
echo "  On your Mac:   ssh -L 8080:localhost:8080 $APP_USER@$SERVER_IP"
echo "  then open http://localhost:8080 and create the admin account."
echo
read -r -p "  Have you created the admin account? [y/N] " READY
if [[ ! "$READY" =~ ^[Yy]$ ]]; then
  warn "Stopped before going public. Run this script again once the admin exists."
  exit 0
fi
docker compose --profile https up -d --build
green "HTTPS started – Caddy is getting certificates (watch with: docker compose logs -f caddy)"

# ------------------------------------------------------------------ 7. Backups
info "7/7 Nightly database backup"
install -d -o "$APP_USER" -g "$APP_USER" "$USER_HOME/backups"
CRON_LINE="0 3 * * * cd $APP_DIR && docker compose exec -T db pg_dump -U \${POSTGRES_USER:-elistings} elistings | gzip > $USER_HOME/backups/elistings-\$(date +\\%F).sql.gz && find $USER_HOME/backups -name '*.sql.gz' -mtime +14 -delete"
if crontab -u "$APP_USER" -l 2>/dev/null | grep -q 'pg_dump'; then
  green "Backup already scheduled"
else
  ( crontab -u "$APP_USER" -l 2>/dev/null || true; echo "$CRON_LINE" ) | crontab -u "$APP_USER" -
  green "Backup scheduled daily at 03:00 → $USER_HOME/backups (kept 14 days)"
fi

echo
green "All done."
echo "  Website:   https://${SITE_DOMAIN:-<SITE_DOMAIN not set>}"
echo "  eListings: https://$DOMAIN"
echo "  eBay developer portal → RuName accepted URL: https://$DOMAIN/api/auth/callback"
echo "  Log out and back in once so '$APP_USER' can use docker without sudo."
