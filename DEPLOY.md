# Deploying eListings (PostgreSQL + Docker)

The app runs as one container next to a PostgreSQL container. Background jobs (Hangfire) run inside the app,
and Hangfire keeps its tables in the same PostgreSQL database, in a separate `hangfire` schema.

## 1. Run locally with Docker

```bash
cp .env.example .env          # set POSTGRES_PASSWORD (and supplier/eBay values if you want them)
docker compose up --build     # app on http://localhost:8080
```

### Or: only PostgreSQL in Docker, app from your IDE
```bash
docker compose up -d db
```
In `Server/appsettings.Development.json` set:
```json
"ConnectionStrings": { "DefaultConnection": "Host=localhost;Port=5432;Database=elistings;Username=elistings;Password=<POSTGRES_PASSWORD>" }
```
The app picks PostgreSQL automatically when the connection string contains `Host=`; with `Data Source=app.db` it still uses SQLite.

## 2. Move existing data from SQLite (one-off)

1. Run the app once on SQLite with the current version (so `app.db` has every column), then stop it.
2. Point `DefaultConnection` at PostgreSQL (above) and run:
   ```bash
   dotnet run --project Server -- --copy-sqlite-to-postgres Server/app.db
   ```
   It creates the schema, copies users, eBay accounts, suppliers, delivery options, feeds, products, listings and
   job history (ids kept), and refuses to run on a database that already has data (`--force` to override).
3. Start the app normally. Keep `app.db` as a backup until you're happy.

Hangfire's own history isn't copied; schedules are recreated at startup.

## 3. Deploy to a server (VPS)

A small VPS (e.g. Hetzner CX22 / DigitalOcean 2 GB) with Docker installed is enough.

1. Point your domain's DNS **A record** at the server's IP.
2. Copy the repo (or `git clone`) to the server, create `.env` with `DOMAIN=your.domain` and all secrets.
3. Start with HTTPS:
   ```bash
   docker compose --profile https up -d --build
   ```
   Caddy obtains and renews the HTTPS certificate automatically.
4. In the eBay developer portal set the RuName *auth accepted URL* to `https://your.domain/api/auth/callback`
   (docker-compose sets `EbaySettings__RedirectUri` from `DOMAIN`; no ngrok needed in production).
5. Copy your data across if needed (it replaces the seeded suppliers/feeds) (section 2: copy `app.db` to the server, or run the copy from your machine
   against the server's database through an SSH tunnel), then sign in.

Updates: `git pull && docker compose --profile https up -d --build`.

### Backups (do this!)
Nightly dump, e.g. in the server's crontab:
```bash
0 3 * * * cd /path/to/elistings && docker compose exec -T db pg_dump -U elistings elistings | gzip > /backups/elistings-$(date +\%F).sql.gz
```
Restore: `gunzip -c backup.sql.gz | docker compose exec -T db psql -U elistings elistings`.
Copy backups off the server (e.g. to cloud storage).

## 4. Managed alternative (Azure)

Azure Container Apps (or App Service for containers) + **Azure Database for PostgreSQL – Flexible Server**:
set `ConnectionStrings__DefaultConnection` to the managed server (`Host=...;Database=...;Username=...;Password=...;SSL Mode=Require`)
and the other settings from `.env.example` as app settings. Backups and patching are handled by Azure.
See `DEPLOY_AZURE.md` for the container/CI setup (replace its SQLite connection string with the PostgreSQL one).

## 5. Notes
- Supplier imports and scheduled jobs run 24/7 once the server is up; check them at `/hangfire` (development only
  for now — ask to enable an admin-protected dashboard in production).
- Supplier websites see requests coming from the server's IP. DropShipMall and Eden work with logins; Safelincs blocks
  plain server requests, so a Safelincs import would need a scraping proxy.
- Secrets live only in `.env` / host settings, never in git.
