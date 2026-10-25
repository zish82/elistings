# Requirements and Specification Document
**Project Specification: Safelincs-to-eBay Listing Manager**

## 1. Project Overview
The goal of this project is to build a SaaS application that streamlines the process of extracting product information from the Safelincs website and publishing it as listings on eBay. The application serves as a bridge, allowing users to scrape data, edit/enrich it locally, and push it to eBay using their own seller accounts.

## 2. Technical Stack & Architecture
*   **Framework**: .NET 10.0 (Preview/RC features enabled)
*   **Architecture**: Hosted Blazor WebAssembly (ASP.NET Core Server + Blazor Client)
*   **Database**: SQLite with Entity Framework Core
*   **Frontend UI**: Blazor Components, Bootstrap 5, Quill.js (planned)
*   **Integrations**: 
    *   eBay REST APIs (Inventory, Fulfillment, Taxonomy, Analytics)
    *   eBay EPS (Trading API) for image uploads
    *   Safelincs Website (HTML scraping)

## 3. Core Functional Requirements

### 3.1. Listings Dashboard
*   **Requirement**: A central dashboard to view all extracted and created listings.
*   **Status**: Implemented (`Home.razor`)
*   **Features**:
    *   Table view of listings with columns for Title, Price, Stock, Status (Draft/Published), and Actions.
    *   Visual indicators for published vs. draft items.
    *   Action buttons: Edit, Publish, Delete.
    *   "Connect eBay" global action for authentication.

### 3.2. Data Extraction (Scraping)
*   **Requirement**: Ability to extract product data from a given Safelincs URL.
*   **Status**: Implemented (`ScraperService.cs`)
*   **Features**:
    *   Extracts Title, Price (VAT handling), Description (text), and Images.
    *   Handles variable URL structures.
    *   Downloads images locally for re-uploading to eBay.
    *   **Future**: Bulk extraction from Category pages (Phase 12).

### 3.3. Listing Management
*   **Requirement**: User interface to create/edit listings before publishing.
*   **Status**: Implemented (`CreateListing.razor`)
*   **Features**:
    *   **Fill from URL**: Auto-populate form fields by pasting a Safelincs URL.
    *   **Fields**: Title, Description, Price, Quantity, eBay Category ID, SKU.
    *   **Policies**: Dropdown selection for Payment, Return, and Shipping policies fetched from eBay (Phase 3).
    *   **Images**: UI to view extracted images and upload them to eBay EPS (Phase 6).
    *   **Persistence**: Save functionality to store drafts in the local SQLite database.

### 3.4. eBay Integration
*   **Requirement**: Full authentication and publication capabilities with eBay.
*   **Status**: Implemented
*   **Features**:
    *   **OAuth 2.0 Flow**: Real "Log in with eBay" implementation using browser redirect and code exchange.
    *   **Token Management**: Secure storage and automatic refreshing of Access/Refresh tokens.
    *   **Inventory API**: Uses the Inventory API to create inventory items (SKU-based) and offers.
    *   **Image Service**: Helper service to upload binary image data to eBay's Picture Service (EPS) handling complex multipart/form-data requirements (Phase 10).
    *   **Taxonomy**: Helper to fetch and suggest Category IDs based on product keywords (Phase 7).

### 3.5. Publishing Workflow
*   **Requirement**: Convert a local draft into a live eBay listing.
*   **Status**: Implemented
*   **Specifications**:
    *   **Validation**: Checks for mandatory fields (SKU, Price, Policies).
    *   **Item Specifics**: Automatically adds required fields like `Brand`, `Type`, `Colour` (defaulting to "Unbranded/TBD" if missing) to satisfy eBay requirements.
    *   **Synchronization**: 
        *   If Not Published: Creates Inventory Item -> Creates Offer -> Publishes Offer.
        *   If Already Published: Updates existing Inventory Item and Offer details (Phase 8/9).
    *   **Feedback**: Real-time UI updates (Spinners, Success/Error toasts).

## 4. Pending Requirements (Current Sprint)

### 4.1. Rich Text Editing (Phase 11)
*   **Requirement**: Replace plain text descriptions with HTML formatting.
*   **Specification**:
    *   Integrate **Quill.js** library.
    *   Allow Bold, Italic, Lists (Ordered/Unordered).
    *   Sanitize HTML input before saving to database.
    *   Ensure HTML renders correctly on eBay listing view.

### 4.2. Bulk Operations (Phase 12)
*   **Requirement**: Scale operations to handle categories of products.
*   **Specification**:
    *   **Bulk Extraction**: Input a Category URL -> Scrape all product links -> Create drafts for each.
    *   **Batch Publish**: Select multiple drafts in Dashboard -> Publish all to eBay in a queue.
    *   **Progress UI**: Dedicated view to show progress bars for extraction and publishing batches.

## 5. Non-Functional Requirements & Constraints
*   **Performance**: Extraction operations should not block the UI (Async/Await usage).
*   **Reliability**: eBay API calls must handle rate limits and transient errors (Retry logic implemented).
*   **Compatibility**: Build must support standard browser environments (solved static asset 404 issues).
*   **Data Integrity**: Prices must be parsed correctly to removing currency symbols and VAT where applicable.

## 6. Development History & Fixes
*   **Resolving 404s**: Fixed issue with Blazor WebAssembly static assets not being served by `dotnet run`.
*   **Image Uploads**: Resolved critical bugs with eBay's multipart image upload by switching to a raw byte array construction method.
*   **Database Migrations**: Addressed noise in migration logs and ensured schema consistency for `EbayTokens` and `Listings` tables.
