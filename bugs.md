# Known Bugs and Gaps

These issues were identified during the requirements review. They are documented for follow-up only; no fixes have been applied here.

## 1. Server tests do not compile

`ListingsController` now requires `IEdenSupplierSyncService` and `ICurrentUserService`, but the tests still use the old constructor signature. This prevents `dotnet test Server.Tests/Server.Tests.csproj` from running.

Affected files:

- [Server/Controllers/ListingsController.cs](Server/Controllers/ListingsController.cs)
- [Server.Tests/ListingsControllerTests.cs](Server.Tests/ListingsControllerTests.cs)

## 2. Rich-text HTML is not sanitized

Quill HTML is persisted directly by the create and update listing endpoints. There is no server-side HTML sanitizer before saving descriptions to SQLite.

Affected file:

- [Server/Controllers/ListingsController.cs](Server/Controllers/ListingsController.cs)

## 3. Listing quantity is missing and hard-coded to one

The listing model and editor do not expose a quantity field. eBay inventory and offer payloads always use quantity `1`.

Affected files:

- [Shared/CreateListingRequest.cs](Shared/CreateListingRequest.cs)
- [Server/Data/AppDbContext.cs](Server/Data/AppDbContext.cs)
- [Server/Services/EbayService.cs](Server/Services/EbayService.cs)
- [Client/Pages/CreateListing.razor](Client/Pages/CreateListing.razor)

## 4. Listing SKU is not editable in the listing form

SKU is stored and can be generated during publication, but the create/edit UI does not provide a SKU field even though SKU is part of the listing requirements.

Affected files:

- [Client/Pages/CreateListing.razor](Client/Pages/CreateListing.razor)
- [Shared/CreateListingRequest.cs](Shared/CreateListingRequest.cs)

## 5. Dashboard lacks an individual Delete action

The dashboard provides Edit and Publish/Re-sync actions and supports bulk delete, but it does not provide a delete action for an individual listing as required.

Affected file:

- [Client/Pages/Home.razor](Client/Pages/Home.razor)

## 6. Bulk operations are synchronous rather than queued

Bulk extraction and bulk publishing process all items inside a single HTTP request. They are not durable background jobs and cannot resume after a server restart.

Affected file:

- [Server/Controllers/ListingsController.cs](Server/Controllers/ListingsController.cs)

## 7. Bulk operations have no progress tracking UI

The bulk extraction and publishing interfaces show only a spinner and final counts. There are no job IDs, persisted progress records, progress bars, or dedicated progress view.

Affected files:

- [Client/Pages/BulkExtract.razor](Client/Pages/BulkExtract.razor)
- [Client/Pages/Home.razor](Client/Pages/Home.razor)
- [Server/Controllers/ListingsController.cs](Server/Controllers/ListingsController.cs)

## 8. Bulk extraction can create duplicate drafts

Bulk extraction does not check whether a product link or source product already exists. Re-running the same category can create duplicate listings.

Affected file:

- [Server/Controllers/ListingsController.cs](Server/Controllers/ListingsController.cs)

## 9. Scraped images are not downloaded locally

Scraping stores external image URLs. Images are downloaded later during eBay publication, so the source image must remain available and the documented local-download behavior is not implemented.

Affected files:

- [Server/Services/ScraperService.cs](Server/Services/ScraperService.cs)
- [Server/Services/SafelincsProductScraper.cs](Server/Services/SafelincsProductScraper.cs)
- [Client/Pages/CreateListing.razor](Client/Pages/CreateListing.razor)

## 10. Image upload failures fall back to a placeholder

If all extracted image uploads fail, publication adds `https://via.placeholder.com/500` and can continue. This may create a live listing with an incorrect image instead of reporting a blocking publication error.

Affected file:

- [Server/Services/EbayService.cs](Server/Services/EbayService.cs)

## 11. eBay retry handling is too narrow

Retry logic only handles eBay error `25604` during offer publication. It does not generally handle HTTP 429 rate limits, transient 5xx responses, network failures, `Retry-After`, or failures from the other eBay API calls.

Affected file:

- [Server/Services/EbayService.cs](Server/Services/EbayService.cs)

## 12. Publish validation is incomplete

Publish endpoints do not explicitly validate all required fields before calling eBay. Missing validation includes SKU, category, payment policy, fulfillment policy, return policy, valid images, title, and description. The service can silently substitute fallback category and aspect values.

Affected files:

- [Server/Controllers/EbayController.cs](Server/Controllers/EbayController.cs)
- [Server/Controllers/ListingsController.cs](Server/Controllers/ListingsController.cs)
- [Server/Services/EbayService.cs](Server/Services/EbayService.cs)

## 13. Required item-specific defaults are unsafe for arbitrary products

Publication defaults `Type` to `Fire Extinguisher` and `Colour` to `Red` for listings that do not provide those values. These values are not suitable for arbitrary product categories and do not match the documented `Unbranded/TBD` behavior.

Affected file:

- [Server/Services/EbayService.cs](Server/Services/EbayService.cs)

## 14. Dashboard publish failures are not user-visible

The edit page displays publication errors, but dashboard single-listing publication writes failures only to the browser console. Users do not receive an error notification or persistent failure state.

Affected file:

- [Client/Pages/Home.razor](Client/Pages/Home.razor)

## Test coverage gap

The current test suite does not cover scraper parsing, VAT handling, OAuth state validation, token refresh, account ownership isolation, image multipart payloads, eBay request payloads, publish validation, sanitization, retry behavior, bulk extraction, duplicate detection, or UI workflows.

Affected file:

- [Server.Tests/ListingsControllerTests.cs](Server.Tests/ListingsControllerTests.cs)
