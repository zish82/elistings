# Project structure

Folders group files by feature. C# namespaces were **not** changed when files were moved (e.g. everything in
`Server/Services/**` is still `namespace Server.Services`), so `using`s stay simple.

```
Client/                         Blazor WebAssembly app (the UI)
  Layout/                       MainLayout (side menu), BlankLayout (full-screen pages), NavMenu
  Components/                   reusable components (bulk job progress, item specifics editor)
  Pages/
    Account/                    Login (black landing + sign-in), SetupAdmin, Users, EbayAccounts
    Listings/                   Home (My Listings), CreateListing, BulkExtract
    Catalog/                    Products, ProductDetails
    SupplierSettings/           Suppliers, SupplierFeeds
    Reports/                    Sales
    NotFound.razor
  Services/                     API clients (AuthService, ListingService), LandingText (sign-in page languages)

Server/                         ASP.NET Core API + hosts the Client
  Program.cs                    start-up: services, middleware, scheduled jobs
  Configuration/                settings classes (eBay, Eden, DropShipMall, Contact)
  Controllers/                  API endpoints (api/...)
  Data/
    AppDbContext.cs             EF Core context (tables, indexes)
    Entities/                   one file per table (Listing, SupplierProduct, EbayOrder, ...)
    DatabaseSetup, SeedData, LegacySqliteSchema, PostgresSchemaUpgrades, SqliteToPostgresCopier
  Services/
    Contact/                    website contact form → email
    Ebay/                       eBay API, categories, images, orders (sales), OAuth scopes
    Fees/                       eBay fee rate card + fee settings
    Jobs/                       background jobs (Hangfire) and progress
    Listings/                   publish helper, mapping, SKUs, product enrichment
    Scraping/                   product page scrapers, per-host request throttle
    Suppliers/                  shared supplier logic (access, catalogue, price lists, stock, tags)
      DropShipMall/  Eden/  Godropship/  Safelincs/   one folder per supplier
    Users/                      current user, password hashing

Shared/                         DTOs used by both Client and Server (namespace Shared)
  Auth/  Ebay/  Listings/  Sales/  Suppliers/

Server.Tests/                   xUnit tests, same feature folders (Contact, Jobs, Listings, Sales, Suppliers)

sites/digitalclub/              Digital Club company website (static, 4 languages; `node build.mjs`)
deploy/Caddyfile                HTTPS + routing for the website and the app
docs/                           deployment guides, requirements, bug notes, this file
docker-compose.yml, Dockerfile  containers (app, PostgreSQL, Caddy, website build)
```

Adding things:
- **New supplier:** `Server/Services/Suppliers/<Name>/` (importer, scraper, stock checker), register in `Program.cs`, tests in `Server.Tests/Suppliers/`.
- **New page:** pick the matching `Client/Pages/<Area>/` folder; the URL comes from `@page`, not the folder.
- **New table:** class in `Server/Data/Entities/`, `DbSet` in `AppDbContext`, schema upgrade in `PostgresSchemaUpgrades` / `LegacySqliteSchema`.
