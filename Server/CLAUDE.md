# Server (ASP.NET Core API, .NET 10)

The API behind the Blazor WebAssembly Client; it also hosts the Client. Folder map: `docs/PROJECT_STRUCTURE.md`.
Business rules and features: `docs/requirements_specifications.md`. Deployment: `docs/DEPLOY*.md`.

## Working rules
- Don't run the build, tests or server: the user does that. Say what to run instead.
- Namespaces were NOT changed when files moved into feature folders: everything in `Services/**` is `namespace Server.Services`.
- DTOs shared with the Client live in `Shared/` (`namespace Shared`). Pricing/fee maths the UI also shows
  (`PricingMath`, `FeeMath`) lives in Shared so both sides agree. Don't duplicate it on the server.
- Tests: xUnit in `Server.Tests/<Feature>/`, EF InMemory DB + Moq user via `TestHelpers` (`NewContext()`, `User(...)`,
  `WithHttpContext()`). Add or adjust tests alongside behaviour changes.

## Database (no EF migrations)
PostgreSQL in production, SQLite locally/legacy. `DatabaseSetup` runs `EnsureCreated()`, then upgrades:
- **New table:** entity in `Data/Entities/` (one file per table), `DbSet` + indexes in `AppDbContext`.
- **New column or table on an existing DB:** add idempotent SQL at the END of `Data/PostgresSchemaUpgrades.cs`
  (`ADD COLUMN IF NOT EXISTS`, quoted identifiers, dated comment) AND the SQLite equivalent in
  `Data/LegacySqliteSchema.cs` (`EnsureColumn(...)` / `CREATE TABLE IF NOT EXISTS`). Never remove old lines.
- Non-null columns need a `DEFAULT` so existing rows survive.

## Users and access
- Cookie auth; controllers are `[Authorize]`. Current user via `ICurrentUserService` (`UserId`, `Role`,
  `CanManageUsers` = Admin, `CanViewAllListings` = Admin/Manager).
- Scope data to the user: listings via `UserScope.VisibleUserIdsAsync` + `.VisibleTo(...)`; marketplace accounts via
  `AccountAccess`; suppliers via `SupplierAccessService`. Supplier/fee settings: Admin or Manager only.
- Background jobs run without an HTTP context: they set `BackgroundUserContext` to act as a user.

## Background work
Hangfire (Postgres schema `hangfire`, or SQLite `hangfire.db`, or Memory). Recurring jobs (supplier sync, catalogue
imports, eBay order sync) are registered at the end of `Program.cs`. Bulk jobs started from pages go through
`Services/Jobs/BulkJobService` and report progress through `JobProgress`.

## Main areas
- **Listings** (`Services/Listings`): `ListingPublisher` / `ListingPublishHelper` publish to eBay; `ListingVariants`
  (multi-variation listings, trial); `SkuService`; `ProductEnrichmentService`; `PublicationQueries`.
- **eBay** (`Services/Ebay`): `EbayService` (Inventory/Trading APIs, OAuth tokens per marketplace account),
  categories, images (pictures must be eBay-hosted; uploads are reused per account via `EbayPictureCache`), orders/sales. `EbaySettings.IsSandbox` defaults to true.
- **Suppliers** (`Services/Suppliers`, one folder per supplier: DropShipMall, Eden, Safelincs, Godropship): catalogue import
  (`ISupplierCatalogImporter`), stock checks (`ISupplierStockChecker`; registration order in `Program.cs` matters),
  price lists, suggested pricing, tags. New supplier: new folder, register in `Program.cs`, tests in
  `Server.Tests/Suppliers/`.
- **Fees** (`Services/Fees`): eBay fee rate card + per-category rates; profit estimates use these settings.
- **Prices:** listing prices are inc VAT; supplier costs may be ex VAT (supplier's `PricesIncludeVat`/`VatRatePercent`).
  Delivery is currently always included in the listing price (free postage).

## Config and secrets
`appsettings.json` holds defaults only. Real secrets (eBay keys, supplier logins, SMTP) go in
`appsettings.Development.json` or environment variables (`Section__Key`), never in git.
