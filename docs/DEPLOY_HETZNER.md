# Going live on Hetzner Cloud (CX23) – step by step

End result, all on one server:

| Address | What |
|---|---|
| `https://yourdomain.co.uk` (+ `www.`) | Digital Club website |
| `https://elistings.yourdomain.co.uk` | eListings |
| `hello@yourdomain.co.uk` | your mailbox (Zoho Mail); the website contact form is sent through it |

Replace `yourdomain.co.uk` everywhere below with your real domain. Allow ~2 hours the first time.

Rough monthly cost: server ~€6 + backups ~€1.20 + mailbox ~£1 per user + domain (~£5–12 a year).

---

## 1. Buy the domain

1. Check the name is free and buy it at a registrar, e.g. **Cloudflare Registrar** (sold at cost, free DNS),
   Namecheap, Porkbun or – for `.co.uk` – any Nominet registrar.
2. Turn on auto-renew and two-factor login on the registrar account.
3. You'll add DNS records in step 6, so leave the DNS page open. If you use Cloudflare, every record below must be
   **"DNS only" (grey cloud)**, not proxied – the server gets its own HTTPS certificates.

## 2. Create the server

1. Sign up at **hetzner.com** → *Cloud* (they may ask for ID). Turn on two-factor login.
2. On your Mac, make an SSH key if you don't have one:
   ```bash
   ssh-keygen -t ed25519 -C "zee@mac"      # press Enter for the default file, set a passphrase
   cat ~/.ssh/id_ed25519.pub               # copy this line
   ```
3. Hetzner Console → *New project* "Digital Club" → **Add Server**:
   - Location: **Falkenstein** or **Nuremberg** (Germany)
   - Image: **Ubuntu 24.04**
   - Type: Shared vCPU → x86 → **CX23**
   - Networking: IPv4 + IPv6
   - SSH keys: paste the key from step 2 (then no root password is created)
   - **Firewalls → Create firewall** "web":
     - Inbound TCP **22** – source: *your home IP only* (search "what is my IP"; add more later if needed)
     - Inbound TCP **80** and **443** – any
     - Inbound ICMP – any
     - Nothing else (this blocks 5432, 8080 etc. even if something opens them by mistake)
   - **Backups**: on (daily snapshots, kept 7 days)
   - Name: `digitalclub-1` → **Create & Buy**
4. Note the server's **IPv4 address** (e.g. `203.0.113.10`).

## 3. Secure the server

Log in and create your own user (replace the IP):

```bash
ssh root@203.0.113.10

adduser zee                                   # choose a strong password (needed for sudo)
usermod -aG sudo zee
rsync --archive --chown=zee:zee ~/.ssh /home/zee
```

In a **second** terminal check you can log in as the new user before going on: `ssh zee@203.0.113.10`.

Then, as `zee`:

```bash
# Updates now, and security updates automatically from now on
sudo apt update && sudo apt -y upgrade
sudo apt -y install unattended-upgrades fail2ban
sudo dpkg-reconfigure -plow unattended-upgrades      # answer Yes

# SSH: keys only, no root login
sudo tee /etc/ssh/sshd_config.d/00-hardening.conf >/dev/null <<'EOF'
PermitRootLogin no
PasswordAuthentication no
KbdInteractiveAuthentication no
EOF
sudo systemctl restart ssh

# 2 GB swap (helps while Docker builds the app)
sudo fallocate -l 2G /swapfile && sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile
echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab

sudo timedatectl set-timezone Europe/London
sudo reboot
```

Notes:
- The Hetzner firewall (step 2) is the main firewall. Don't rely on `ufw` alone – Docker bypasses it.
- `fail2ban` blocks IPs that keep failing SSH logins.

## 4. Install Docker and get the code

```bash
ssh zee@203.0.113.10
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker zee
exit                                   # log out and back in so the group applies
ssh zee@203.0.113.10
docker compose version                 # should print v2.x
```

Get the code (pick one):

- **From GitHub (private repo):** on the server `ssh-keygen -t ed25519 -C deploy@digitalclub-1`, add
  `~/.ssh/id_ed25519.pub` as a read-only **Deploy key** on the repo, then
  `git clone git@github.com:YOU/elistings.git ~/elistings`
- **From your Mac:** `rsync -av --exclude bin --exclude obj --exclude '*.db*' --exclude .env ./ zee@203.0.113.10:~/elistings/`

## 5. Settings (.env) and first start – before DNS

```bash
cd ~/elistings
cp .env.example .env
chmod 600 .env                         # only you can read it
openssl rand -base64 32                # use this as POSTGRES_PASSWORD
nano .env
```

Fill in at least:

```ini
POSTGRES_PASSWORD=<the random value>
DOMAIN=elistings.yourdomain.co.uk
SITE_DOMAIN=yourdomain.co.uk
SITE_CONTACT_EMAIL=hello@yourdomain.co.uk
EBAY_APP_ID=... EBAY_CERT_ID=... EBAY_DEV_ID=... EBAY_RUNAME=...
EDEN_USERNAME=... EDEN_PASSWORD=... DSM_USERNAME=... DSM_PASSWORD=...
# email: filled in at step 8
```

Start the app and database only (no public HTTPS yet):

```bash
docker compose up -d --build           # first build takes a few minutes
docker compose ps                      # db "healthy", app "running"
docker compose logs -f app             # Ctrl+C to stop watching
```

**Create your admin account now, privately** (the first person to open `/setup-admin` becomes admin, so do this
before the site is public). On your Mac:

```bash
ssh -L 8080:localhost:8080 zee@203.0.113.10
```

Open http://localhost:8080 on your Mac → create the admin → close the tunnel.

Moving existing data (optional): see `docs/DEPLOY.md` section 2 – do it now, before going public.

## 6. DNS: domain and sub-domain

At your registrar / Cloudflare DNS, add (TTL: auto or 300):

| Type | Name | Value |
|---|---|---|
| A | `@` | `203.0.113.10` |
| A | `www` | `203.0.113.10` |
| A | `elistings` | `203.0.113.10` |
| AAAA (optional) | `@`, `www`, `elistings` | the server's IPv6 (ends in `::1`) |

`elistings` is the sub-domain – any future product is just another A record (e.g. `nextproduct`) plus a block in
`deploy/Caddyfile`.

Check from your Mac (can take a few minutes up to a few hours):

```bash
dig +short yourdomain.co.uk
dig +short elistings.yourdomain.co.uk
```

## 7. Go live with HTTPS

When both names show the server's IP:

```bash
cd ~/elistings
docker compose --profile https up -d --build
docker compose logs -f caddy           # look for "certificate obtained successfully"
```

Open `https://yourdomain.co.uk` (website) and `https://elistings.yourdomain.co.uk` (sign-in page).

Then in the **eBay developer portal** set the RuName *auth accepted URL* to
`https://elistings.yourdomain.co.uk/api/auth/callback` and reconnect your eBay account(s) from *eBay Accounts*.

## 8. Email: mailbox + website contact form

Hetzner blocks outgoing mail ports 25 and 465 on new servers, so the app sends through your mail provider on
**port 587** (already the default).

1. Sign up at **zoho.eu** (EU data centre) → **Mail Lite** plan (the free plan has no SMTP) → add your domain.
2. Zoho shows DNS records to add – add them exactly as shown. They look like:

   | Type | Name | Value |
   |---|---|---|
   | TXT | `@` | `zoho-verification=...` (only for verifying) |
   | MX | `@` | `mx.zoho.eu` priority 10, `mx2.zoho.eu` 20, `mx3.zoho.eu` 50 |
   | TXT | `@` | `v=spf1 include:zohomail.eu ~all` |
   | TXT | `zmail._domainkey` | DKIM key from Zoho (*Email Authentication → DKIM*) |
   | TXT | `_dmarc` | `v=DMARC1; p=none; rua=mailto:hello@yourdomain.co.uk` |

   (After a few weeks with no problems, change DMARC to `p=quarantine`.)
3. Create the mailbox `hello@yourdomain.co.uk`, turn on two-factor login, then create an
   **App-specific password** (*My Account → Security → App passwords*) for "website".
4. On the server add to `.env`:
   ```ini
   CONTACT_TO_EMAIL=hello@yourdomain.co.uk
   CONTACT_FROM_EMAIL=hello@yourdomain.co.uk
   SMTP_HOST=smtp.zoho.eu
   SMTP_PORT=587
   SMTP_USERNAME=hello@yourdomain.co.uk
   SMTP_PASSWORD=<the app-specific password>
   ```
   ```bash
   docker compose --profile https up -d          # restarts the app with the new settings
   ```
5. Test: send a message from the website's contact form; it arrives in the mailbox with *Reply-To* set to the visitor.
   If not: `docker compose logs app | grep -i contact`.

Alternative for app sending only: Brevo (free up to 300 emails/day, SMTP `smtp-relay.brevo.com:587`).

## 9. Backups

- Hetzner server backups are on (step 2) – they restore the whole server.
- Database dump every night, kept 14 days:
  ```bash
  mkdir -p ~/backups
  crontab -e
  ```
  add:
  ```
  0 3 * * * cd ~/elistings && docker compose exec -T db pg_dump -U elistings elistings | gzip > ~/backups/elistings-$(date +\%F).sql.gz && find ~/backups -name '*.sql.gz' -mtime +14 -delete
  ```
- Copy backups **off** the server now and then, e.g. from your Mac: `rsync -av zee@203.0.113.10:backups/ ~/elistings-backups/`
  (or a Hetzner Storage Box).
- Restore test (do it once): `gunzip -c backup.sql.gz | docker compose exec -T db psql -U elistings elistings` on a test copy.

## 10. Updating

```bash
cd ~/elistings
git pull                                   # or rsync from your Mac
docker compose --profile https up -d --build
docker image prune -f                      # remove old images
```

## 11. Security checklist

- [ ] SSH: keys only, no root login, port 22 open only to your IP (Hetzner firewall)
- [ ] Hetzner firewall: only 22, 80, 443 (+ICMP) inbound
- [ ] Automatic security updates on (`unattended-upgrades`), `fail2ban` running
- [ ] `.env` is `chmod 600` and never committed; strong `POSTGRES_PASSWORD`
- [ ] Database and app ports only on `127.0.0.1` (docker-compose already does this)
- [ ] Admin created privately (step 5); strong passwords for every eListings user
- [ ] Two-factor login on Hetzner, the registrar, Zoho, GitHub and eBay
- [ ] Backups running and copied off the server; restore tested once
- [ ] HTTPS working on all addresses (Caddy renews certificates automatically)

## Troubleshooting

| Problem | Check |
|---|---|
| Browser can't reach the site | `dig +short yourdomain.co.uk` shows the server IP? Hetzner firewall allows 80/443? |
| Certificate errors | `docker compose logs caddy` – DNS must point at the server; Cloudflare records must be "DNS only" |
| eBay login fails | RuName accepted URL = `https://elistings.yourdomain.co.uk/api/auth/callback` |
| Contact form says "not available" | `CONTACT_TO_EMAIL` and `SMTP_HOST` set in `.env`, then `docker compose --profile https up -d` |
| Contact form says "couldn't be sent" | `docker compose logs app` – wrong app password, or port not 587 |
| Locked out of SSH | Hetzner Console → server → *Console* (browser terminal), or update the firewall's source IP |
