# Deploying eListings + Digital Club (Hetzner)

Everything runs on one Ubuntu 24.04 server (Hetzner Cloud CX23, ~€6/month) with Docker:

| Address | What | Served by |
|---|---|---|
| `https://SITE_DOMAIN` (+ `www.`) | Digital Club website (EN-GB, EN-US, DE, FR) | Caddy (static files) |
| `https://SITE_DOMAIN/products/elistings` | redirects to the eListings sign-in page | Caddy |
| `https://SITE_DOMAIN/api/public/contact` | website contact form → email | the app |
| `https://DOMAIN` (e.g. `elistings.yourdomain.co.uk`) | eListings | the app |

Until you buy a domain, use free **sslip.io** names built from the server IP (`2.29.130.158` → `2-29-130-158.sslip.io`):
`DOMAIN=elistings.2-29-130-158.sslip.io`, `SITE_DOMAIN=2-29-130-158.sslip.io`. HTTPS works on these too.

Other guides: `docs/DEPLOY.md` (Docker basics, SQLite → PostgreSQL copy), `docs/DEPLOY_AZURE.md`,
`docs/DEPLOY_HETZNER.md` (longer explanations).

---

## Part 1 – Manual steps (once)

### 1.1 SSH key on your Mac

```bash
ssh-keygen -t ed25519            # Enter for the default file, choose a passphrase
```

Creates `~/.ssh/id_ed25519` (private – never share) and `~/.ssh/id_ed25519.pub` (public – goes on servers).
Do this on each computer you'll log in from.

### 1.2 Create the server (Hetzner Console)

- Location Falkenstein / Nuremberg / Helsinki · Image **Ubuntu 24.04** · Type **CX23** · IPv4 + IPv6
- **SSH keys → Add SSH key** → paste the output of `cat ~/.ssh/id_ed25519.pub` (then no root password is used)
- **Backups**: on
- Note the server's **IPv4** (Console → server). Below it's written `SERVER_IP`.

**If the server was created without a key:**

```bash
ssh root@SERVER_IP               # Hetzner's emailed password; you must set a new one straight away
ssh-copy-id root@SERVER_IP       # then add your key with the new password
```

`ssh-copy-id` can't change an expired password – log in once with `ssh` first, or it fails with
"Password change required but no TTY available".

### 1.3 Firewall (Hetzner Console → Firewalls → Create, then apply to the server)

| Direction | Protocol | Port | Source |
|---|---|---|---|
| Inbound | TCP | 22 | your home IP + `/32` (find it with `curl -4 ifconfig.me`) |
| Inbound | TCP | 80, 443 | Any |
| Inbound | ICMP | – | Any |

Home IPs change now and then; if SSH stops connecting, update the source IP here.

### 1.4 Your own user (new server only – skip if `ssh zee@SERVER_IP` already works)

```bash
ssh root@SERVER_IP
adduser zee                                   # password for sudo; Enter to skip name / room / phones
usermod -aG sudo zee
rsync --archive --chown=zee:zee ~/.ssh /home/zee
exit
ssh zee@SERVER_IP                             # must work before going on
```

### 1.5 Copy the code (on your Mac, in the project folder)

```bash
bash deploy/push.sh zee@SERVER_IP
```

Copies the project to `~/elistings` on the server. Never copies `.env`, `Server/appsettings.Development.json`,
databases, build output or `.git`.

---

## Part 2 – Server setup with the script

On the server, from the project folder:

```bash
ssh zee@SERVER_IP
cd ~/elistings
sudo bash deploy/setup-server.sh
```

What it does (each step checks first, so it's safe to run again):

1. Updates, automatic security updates, fail2ban, 2 GB swap, UK timezone
2. Makes sure user `zee` exists, has sudo and your SSH key (copied from root if needed)
3. SSH: keys only, no root login – **only if `zee` has a key**, so you can't lock yourself out
4. Installs Docker
5. Checks `.env` (creates it from the template on the first run and stops so you can fill it in), starts the
   database and app privately, waits until healthy
6. Asks whether the admin account exists, then starts HTTPS (Caddy + website)
7. Schedules the nightly database backup

### 2.1 Fill in `.env` (the script stops here on the first run)

```bash
nano .env
```

Every line must be `NAME=value` – no quotes, no commas, no spaces around `=` (don't paste JSON from
`appsettings.Development.json`).

```ini
POSTGRES_PASSWORD=<output of: openssl rand -base64 32>
DOMAIN=elistings.2-29-130-158.sslip.io
SITE_DOMAIN=2-29-130-158.sslip.io
SITE_CONTACT_EMAIL=
EBAY_APP_ID=...
EBAY_CERT_ID=...
EBAY_DEV_ID=...
EBAY_RUNAME=...
EDEN_USERNAME=...
EDEN_PASSWORD=...
DSM_USERNAME=...
DSM_PASSWORD=...
# email (Part 4): CONTACT_TO_EMAIL, CONTACT_FROM_EMAIL, SMTP_HOST, SMTP_PORT, SMTP_USERNAME, SMTP_PASSWORD
```

Save in nano: **Ctrl+X**, **Y**, **Enter**. (In VS Code's terminal turn on
*Settings → Terminal › Integrated: Send Keybindings To Shell*, or use the Mac Terminal app.)
Then run the script again.

### 2.2 Create the admin account – before going public

The first person to open `/setup-admin` becomes admin. When the script asks, on your Mac:

```bash
ssh -L 8080:localhost:8080 zee@SERVER_IP
```

Open http://localhost:8080 → create the admin → close the tunnel → answer `y` in the script.

### 2.3 eBay

eBay developer portal → your RuName → *auth accepted URL*: `https://DOMAIN/api/auth/callback`
(the script prints it). Then sign in to eListings → **eBay Accounts** → reconnect.

### 2.4 Check

- `https://SITE_DOMAIN` – the website; `https://DOMAIN` – eListings sign-in
- `docker compose ps` – db *healthy*, app / caddy *running*
- `docker compose logs -f caddy` – "certificate obtained successfully"

---

## Part 3 – Domain (when you buy it)

1. Buy it (e.g. Cloudflare Registrar, Namecheap, Porkbun; `.co.uk` at any Nominet registrar). Turn on auto-renew + 2FA.
2. DNS records (Cloudflare: **DNS only / grey cloud**):

   | Type | Name | Value |
   |---|---|---|
   | A | `@` | SERVER_IP |
   | A | `www` | SERVER_IP |
   | A | `elistings` | SERVER_IP |

   Future products are just another A record (e.g. `nextproduct`) plus a block in `deploy/Caddyfile`.
3. Wait until `dig +short yourdomain.co.uk` shows the IP.
4. On the server set `DOMAIN=elistings.yourdomain.co.uk` and `SITE_DOMAIN=yourdomain.co.uk` in `.env`, then
   `docker compose --profile https up -d --build`.
5. Change the eBay accepted URL to the new `DOMAIN`.

## Part 4 – Email (needs the domain)

Hetzner blocks outgoing mail ports 25/465 – the app sends through your mail provider on **587** (default).

1. **zoho.eu** → Mail Lite (the free plan has no SMTP) → add the domain → add the DNS records Zoho shows:
   - MX `mx.zoho.eu` (10), `mx2.zoho.eu` (20), `mx3.zoho.eu` (50)
   - TXT `@` `v=spf1 include:zohomail.eu ~all`
   - TXT `zmail._domainkey` – the DKIM value from Zoho
   - TXT `_dmarc` `v=DMARC1; p=none; rua=mailto:hello@yourdomain.co.uk`
2. Create `hello@yourdomain.co.uk`, turn on 2FA, create an **app-specific password**.
3. In `.env`:
   ```ini
   SITE_CONTACT_EMAIL=hello@yourdomain.co.uk
   CONTACT_TO_EMAIL=hello@yourdomain.co.uk
   CONTACT_FROM_EMAIL=hello@yourdomain.co.uk
   SMTP_HOST=smtp.zoho.eu
   SMTP_PORT=587
   SMTP_USERNAME=hello@yourdomain.co.uk
   SMTP_PASSWORD=<app-specific password>
   ```
4. `docker compose --profile https up -d` and send a test message from the website's contact form.

---

## Part 5 – Everyday

| Task | Command |
|---|---|
| Update after code changes | GitHub → Actions → **Deploy to Hetzner** → Run workflow (Part 7), or from the Mac `bash deploy/push.sh zee@SERVER_IP --restart` |
| Status | `docker compose ps` |
| Logs | `docker compose logs -f app` (or `db`, `caddy`) |
| Restart | `docker compose --profile https restart` |
| Stop everything | `docker compose --profile https down` (data is kept) |
| Backups | `~/backups/elistings-YYYY-MM-DD.sql.gz`, 03:00 nightly, kept 14 days |
| Copy backups to the Mac | `rsync -av zee@SERVER_IP:backups/ ~/elistings-backups/` |
| Restore a backup | `gunzip -c FILE.sql.gz \| docker compose exec -T db psql -U elistings elistings` |
| Server/Docker web UIs (optional) | Cockpit / Portainer through an SSH tunnel – see `docs/DEPLOY_HETZNER.md` |

---

## Part 5b – Safelincs import from your Mac (while Safelincs blocks the server)

Safelincs returns **403 Forbidden** to the server's IP (data-centre addresses are blocked), but allows your home
connection. Until they allow `2.29.130.158` (or provide a feed), run the import on your Mac **against the server's
database** – Safelincs sees your home IP, the products are saved straight on the server.

Before the first time: in eListings on the server → **Supplier Feeds** → Safelincs → turn **Enable background sync** off
(the nightly import would only get 403). Database password: on the server `grep POSTGRES_PASSWORD ~/elistings/.env`.

1. **Mac, terminal 1 – tunnel to the server's database** (leave it open; it shows nothing and waits):
   ```bash
   ssh -N -L 5434:localhost:5432 zee@2.29.130.158
   ```
2. **Mac, terminal 2 – run the app on the server's database** (same code version as the server):
   ```bash
   cd ~/_source/dsc/elistings
   export ConnectionStrings__DefaultConnection='Host=localhost;Port=5434;Database=elistings;Username=elistings;Password=PASTE_PASSWORD_HERE'
   export Hangfire__Storage=Memory
   dotnet run --project Server
   ```
   `Hangfire__Storage=Memory` keeps the job on your Mac (otherwise the server could pick it up and get 403 again).
3. **Open http://localhost:5197**, sign in with your **server** account, and start the Safelincs import on
   *Supplier Feeds* (upload the trade price list there first if it changed). Takes ~2–3 hours: keep the Mac awake and
   both terminals open. When it finishes: Ctrl+C in both terminals.

If port 5434 is busy ("Address already in use"), use another (e.g. 5440) in both step 1 and step 2.
Other Safelincs website calls from the server (stock checks of published Safelincs listings, "fill from URL") also
get 403 until Safelincs allows the server's IP.

## Part 5c – Database backup to your Mac, and a local copy of the server data

| Task (on your Mac, project folder) | Command |
|---|---|
| Back up the server database | `bash deploy/backup-server.sh zee@2.29.130.158` → `~/elistings-backups/elistings-DATE.dump` (newest 30 kept) |
| Load the newest backup into a local PostgreSQL | `bash deploy/restore-local.sh` |
| Back up **and** load in one go | `bash deploy/restore-local.sh --from zee@2.29.130.158` |

`restore-local.sh` needs **Docker Desktop**. It runs PostgreSQL in a container (`elistings-local-pg`, port 5440),
**replaces** its data with the backup, switches off every supplier's background sync in that copy, and prints the
two lines to run the app on it:

```bash
export ConnectionStrings__DefaultConnection='Host=localhost;Port=5440;Database=elistings;Username=elistings;Password=local'
dotnet run --project Server
```

Without those lines the app uses your normal local database (SQLite, `Server/app.db`).
The copy holds your real eBay connections – publishing from the local app posts to real eBay.

## Part 6 – Manual reference (what the script does)

Only needed if you prefer doing it by hand. Run as `zee` unless noted.

```bash
# user (as root)
adduser zee                                   # Enter to skip name / room / phones
usermod -aG sudo zee
rsync --archive --chown=zee:zee ~/.ssh /home/zee
# → test `ssh zee@SERVER_IP` in a new window before continuing

# updates + protection
sudo apt update && sudo apt -y upgrade
sudo apt -y install unattended-upgrades fail2ban
sudo dpkg-reconfigure -plow unattended-upgrades          # Yes

# SSH: keys only. File name starts with 00- so it's read before Hetzner's 50-cloud-init.conf
# (sshd uses the first value it reads).
sudo tee /etc/ssh/sshd_config.d/00-hardening.conf >/dev/null <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
EOF
sudo sshd -t && sudo systemctl restart ssh
sudo sshd -T | grep -Ei 'permitrootlogin|passwordauthentication'   # both "no"

# swap + timezone (one line at a time)
sudo fallocate -l 2G /swapfile
sudo chmod 600 /swapfile
sudo mkswap /swapfile
sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
sudo timedatectl set-timezone Europe/London
sudo reboot

# Docker
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker zee                    # log out and back in afterwards

# app
cd ~/elistings && cp .env.example .env && chmod 600 .env && nano .env
docker compose up -d --build                   # private: db + app
# create the admin through the tunnel (2.2), then:
docker compose --profile https up -d --build   # public: + Caddy + website
```

---

## Part 7 – Deploy from GitHub (button)

GitHub → **Actions** → **Deploy to Hetzner** → **Run workflow**. It builds and tests, copies the code to the server,
rebuilds and restarts, and checks the app answers (`.github/workflows/deploy-hetzner.yml`).
GitHub reaches the server through **Tailscale** (a free private network), so SSH stays closed to the internet.

### One-time setup

1. **Tailscale account** – sign up free at tailscale.com.
2. **Server joins Tailscale:**
   ```bash
   curl -fsSL https://tailscale.com/install.sh | sh
   sudo tailscale up            # open the link it prints and approve the server
   tailscale ip -4              # note this 100.x.x.x address = DEPLOY_HOST
   ```
   In the Tailscale admin → Machines → the server → **Disable key expiry**.
3. **Tailscale tag + OAuth client** (lets GitHub join for one job):
   - Admin → **Access controls** – add to the policy:
     ```json
     "tagOwners": { "tag:ci": ["autogroup:admin"] },
     ```
     (keep the existing rules; the default policy lets your devices reach each other).
   - Admin → **Settings → OAuth clients → Generate** – scope **Auth Keys: Write**, tag `tag:ci`.
     Copy the client ID and secret.
4. **Deploy SSH key** (only for GitHub; on your Mac):
   ```bash
   ssh-keygen -t ed25519 -f ~/.ssh/elistings_deploy -N "" -C github-deploy
   ssh-copy-id -i ~/.ssh/elistings_deploy.pub zee@2.29.130.158
   ```
5. **Server's fingerprint** (on the server, using the address from step 2):
   ```bash
   ssh-keyscan -t ed25519 $(tailscale ip -4)
   ```
6. **GitHub → repo → Settings → Secrets and variables → Actions → New repository secret:**

   | Secret | Value |
   |---|---|
   | `TS_OAUTH_CLIENT_ID` | from step 3 |
   | `TS_OAUTH_SECRET` | from step 3 |
   | `DEPLOY_HOST` | the 100.x.x.x address from step 2 |
   | `DEPLOY_USER` | `zee` |
   | `DEPLOY_SSH_KEY` | the whole output of `cat ~/.ssh/elistings_deploy` (private key, incl. BEGIN/END lines) |
   | `DEPLOY_KNOWN_HOSTS` | the line printed in step 5 |

7. Optional: GitHub → Settings → **Environments → production** → *Required reviewers* (you) for an extra
   confirmation click before each deploy.
8. Push the code to GitHub (`git push`), then run the workflow.

Tip: install Tailscale on your Mac too – then `ssh zee@100.x.x.x` works from anywhere, and you could remove the
port-22 rule from the Hetzner firewall entirely.

## Troubleshooting

| Problem | Fix |
|---|---|
| `Connection refused` on SSH | SSH service isn't running – Hetzner web console → `sudo sshd -t` (shows the bad line) → fix → `sudo systemctl restart ssh` |
| `Permission denied (publickey)` | The user has no key: allow passwords briefly in `00-hardening.conf` (`PasswordAuthentication yes`), `sudo systemctl restart ssh`, run `ssh-copy-id zee@SERVER_IP` on the Mac, then set it back to `no` |
| Still asked for a password after hardening | Hetzner's `50-cloud-init.conf` wins – our file must be named `00-hardening.conf` |
| Root password rejected over SSH | Expected – root login is off. Log in as `zee` |
| Web console rejects the password | Its keyboard may be US layout: avoid `@ " £ # \` in passwords; type the password in the *login* field first to see what comes out |
| Locked out completely | Hetzner Console → **Rescue** → enable & power cycle (note the password) → `ssh-keygen -R SERVER_IP` → `ssh root@SERVER_IP` → `mount /dev/sda1 /mnt` → fix files under `/mnt` (e.g. `chroot /mnt passwd zee`) → `reboot` |
| "REMOTE HOST IDENTIFICATION HAS CHANGED" | Expected after rescue mode or a rebuild: `ssh-keygen -R SERVER_IP` |
| `.env: line N: unexpected character` | That line isn't `NAME=value` – fix with `nano .env` |
| Pasted commands run together | Paste one line at a time |
| `fallocate: Text file busy` | Swap already exists – check `swapon --show` |
| Safelincs import: `403 (Forbidden)` | Safelincs blocks the server's IP – use Part 5b, and ask Safelincs to allow `2.29.130.158` |
| Site doesn't open | Hetzner firewall allows 80/443? `dig +short` shows the server IP? `docker compose logs caddy` |
| Certificate errors | DNS must point at the server; Cloudflare records "DNS only"; sslip.io is occasionally rate-limited – try later |
| eBay sign-in fails | RuName accepted URL must be `https://DOMAIN/api/auth/callback` |
| Contact form "not available" | `CONTACT_TO_EMAIL` and `SMTP_HOST` set in `.env`, then `docker compose --profile https up -d` |
| Contact form "couldn't be sent" | `docker compose logs app` – wrong app password or port not 587 |
