# eListings – Requirements & Specification (handover)

Single source of truth for what eListings does, why, and what's still open. Read with `README.md` (user guide),
`README-DEPLOY.md` (deployment), `docs/PROJECT_STRUCTURE.md` (code layout). Last updated Nov 2026.

## 1. Overview

eListings imports products from dropship suppliers, turns them into marketplace listings (eBay UK today, more
marketplaces later), publishes them to the users' eBay seller accounts, keeps stock/prices in sync, and reports sales
and profit. It's a product of **Digital Club** (company website in `sites/digitalclub`), sold to other sellers later.

Owner: Zee (zeeshan.ahmed@inigoinsurance.com). Repo: github.com/zish82/elistings.

## 2. Stack & architecture

- .NET 10, hosted **Blazor WebAssembly** (Server = ASP.NET Core API + hosts Client), `Shared` DTOs, `Server.Tests` (xUnit, Moq, EF InMemory).
- **EF Core 10**: PostgreSQL in production (chosen automatically when the connection string is `Host=...`), SQLite for
  local dev (`Server/app.db`). No EF migrations: schema changes are idempotent SQL in `Server/Data/PostgresSchemaUpgrades.cs`
  **and** `Server/Data/LegacySqliteSchema.cs` (add both for every new table/column). Seed/backfills in `SeedData.cs`.
- **Hangfire 1.8** (PostgreSQL / SQLite storage): queues `default`, `eden` (1 worker), `safelincs` (1 worker); recurring jobs
  use `MisfireHandling.Ignorable`. Page-started jobs report progress via `BulkJobs` rows (`JobProgress`, `BulkJobProgress` component).
- Per-host request throttling (`RequestThrottle` config; safelincs 3 s, eden 2 s).
- Docker: `Dockerfile`, `docker-compose.yml` (db postgres:17, app, optional `https` profile: `site` builder + Caddy).
- Bootstrap 5 + Bootstrap Icons, Quill editor for descriptions.

## 3. Users, roles and visibility

| Role | Sees | Can |
|---|---|---|
| Admin | everyone's listings, accounts, sales | manage users, suppliers, fees; publish anyone's listing (with the owner's account); share accounts |
| Manager | self + **users assigned to them** (`AppUser.ManagerUserId`, set on Users page) | edit supplier/fee settings; sees disabled suppliers |
| Lister | self only | create/publish own listings |

- Implemented by `UserScope.VisibleUserIdsAsync` (+ `VisibleTo()` extensions) – used by listings, sales, publish, bulk jobs.
- **Per-user supplier access** (`UserSupplierAccess`): Managers/Listers only see ticked suppliers; Admins see all.
- Settings pages are read-only for Listers.

## 4. Marketplace accounts

- eBay accounts = `EbayTokenInfo` (OAuth; refresh token ~18 months, access token 2 h, renewed automatically).
  An account shows "Connected" while its refresh token is valid. Manual tokens (`RefreshToken = "MANUAL"`) can't renew.
  OAuth scopes stored per token (`EbayScopes` Original/Current); sales need `sell.fulfillment` (reconnect old accounts).
- **One user can't connect the same eBay shop twice** (shop ID from Trading `GetUser` → `EbayTokenInfo.EbayUserId`);
  different users may.
- **Shared accounts** (`AccountUserAccess`): an **Admin** shares an account with other users (Accounts overview page).
  Each user only sees **their own** listings on it. `AccountAccess` decides who may post with an account.
- **Duplicate warning**: at "Add to my listings", if another user already published that supplier product on an account
  this user can post to → *"You are about to post this listing which is already posted on this account and on this
  marketplace."* with **Add anyway / Don't add** (`AllowSharedAccountDuplicates`).
- **Accounts overview** page: users (by scope) → accounts → this user's published / failed counts per account
  (links to My Listings filtered `?account=&owner=`), drafts count, Admin share editor.
- eBay Accounts page: connect, manual token (ⓘ help on how to get one), default, refresh, delete (blocked while listings use it).

## 5. Suppliers & products

Supplier settings (`/suppliers`): VAT rate, prices inc/ex VAT, default margin %, SKU prefix, website host, delivery
options, eBay fee category, enabled, **Enable background sync** (scheduled imports only; disabling stops running jobs at checkpoints).

| Supplier | Source | Notes |
|---|---|---|
| DropShipMall (DSM) | trade CSV feeds + website (login) | ex VAT; stock refresh every 4 h, catalogue 01:00 |
| Eden Horticulture | public Shopify JSON + trade prices + stock (login) | ex VAT; 02:30 |
| Safelincs | sitemap + product pages + uploaded Excel trade price list | **blocks the server's IP (403)** – import runs from the Mac against the server DB (README-DEPLOY Part 5b). Asked Safelincs to allow `2.29.130.158` / provide a feed. Products missing from the price list use the website ex-VAT price (`PriceIsEstimate`) |

- Products page: list or **Categories** view (supplier category / tag grouping, in-stock only), filters (search, category,
  tag, price, in stock, hide added, estimated prices only), sort, **Load more** (500 per page, stable ordering by Id),
  remembers filters/view/scroll when returning from a product (`ProductsPageState`), back-to-top button (all pages).
- Product page shows price source and ex/inc VAT, RRP, suggested price.
- **Suggested price** = cost inc VAT × (1 + margin%) rounded up to .99. Known issue: margin is on cost and eBay fees (~16%)
  are not included, so profit can be ~1%. Proposed (not built): price = (cost + fixed fee) / (1 − fee% − margin%).

## 6. Listings & publishing

- Listings (`Listing`): owner, main account (`EbayAccountId`), SKU (`{prefix}-{supplier code}`, unique), price (item +
  delivery folded in), quantity, category, policies, specifics, images, supplier link.
- **Publish to several accounts** (`ListingPublication` rows: listing × marketplace × account, status Published/Failed,
  item id, offer id, last error). Same price/quantity everywhere; business policies on other accounts matched by
  **name**, else first policy. Listing's own `EbayAccountId/EbayItemId/EbayOfferId` = main publication.
  `ListingPublisher` does it; `PublicationQueries.BackfillFromListings` created rows for older listings.
  - Listing page: "Also publish to" checkboxes. Bulk publish: "Publish to:" accounts (drafts → chosen only; published keep theirs too).
  - Re-sync without choosing = every account it's on. Supplier quantity 0/restore applies to every publication.
- Only the owner or an Admin can publish; the account must be one the owner can use.
- **Pictures**: eBay **Media API** (`apim.ebay.com/commerce/media/v1_beta`, createImageFromFile / FromUrl) – Trading
  `UploadSiteHostedPictures` was switched off 30 Sep 2026. If the supplier blocks the download, eBay fetches the URL.
  Publishing continues with the pictures that worked; failures shown as warnings. Live picture-by-picture progress (`PublishProgressStore`).
- Fee/profit estimate per listing from the eBay UK business rate card (tiers, category chain, small-order fee).

## 7. Sales report

- eBay Fulfillment `getOrders` (history `EbayOrders:HistoryDays` 120, auto sync off by default), cost of sales matched by
  SKU, editable Mail / Source Order ID / Supplier Price (Save button), Earning / Profit / ROI, CSV export, UK months.
- Sale amount = order total; fee = eBay's (already inc VAT). Same visibility rules as listings.
- Known: orders on a **shared account** belong to the user who connected it (accepted by owner).

## 8. Digital Club website & landing

- Static site `sites/digitalclub` (EN-GB, EN-US, DE, FR; `node build.mjs`), three services, products (eListings + coming
  soon), contact form → `POST /api/public/contact` (MailKit SMTP, rate-limited, honeypot). Served by Caddy on SITE_DOMAIN.
- eListings `/login` = black landing + sign-in page (4 languages, `LandingText.cs`, `?lang=`).

## 9. Deployment (see README-DEPLOY.md)

- Hetzner CX23 (Helsinki), IP `2.29.130.158`, user `zee` (SSH keys only; `00-hardening.conf` must sort before Hetzner's
  `50-cloud-init.conf`). Testing on `elistings.2-29-130-158.sslip.io` / `2-29-130-158.sslip.io` until a domain is bought.
- `deploy/setup-server.sh` (server setup), `deploy/push.sh user@ip --restart` (deploy from Mac),
  `deploy/backup-server.sh` / `deploy/restore-local.sh` (backups, local Postgres copy in Docker on port 5440),
  `.github/workflows/deploy-hetzner.yml` (optional, needs Tailscale secrets – not set up).
- Planned: domain + Zoho Mail (SMTP 587; Hetzner blocks 25/465).

## 10. Rules for working on this project

- **Never commit secrets.** Real values live in `Server/appsettings.Development.json` (gitignored) or `.env` on the server.
- Don't break existing behaviour; add tests for new features (`Server.Tests`, feature folders).
- Schema changes: entity + `AppDbContext` + Postgres upgrade SQL + SQLite schema (+ backfill in `SeedData` if needed).
- Namespaces don't follow folders (`Server.Services`, `Server.Data`, `Shared`, `Client.Pages.*` for pages).
- Razor: no `$"..."` with quotes inside attribute values (use a helper method); don't name variables `code`.
- The owner prefers **short answers**, Yes/No when enough, and builds/tests locally (`dotnet build`, `dotnet test`) and
  reports errors back.

## 11. Open items / ideas

- Suggested price that includes eBay fees (see §5).
- Safelincs IP allow-list or feed (then remove the Mac workaround).
- Split shared-account sales per listing owner (declined for now).
- Multi-tenant SaaS (clients, client admins, billing) – proposed, not started.
- More marketplaces (model already has `Marketplace` fields).
