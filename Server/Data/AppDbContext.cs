using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<AppUser> Users { get; set; }
    public DbSet<Listing> Listings { get; set; }
    public DbSet<EbayTokenInfo> EbayTokens { get; set; }
    public DbSet<Policy> Policies { get; set; }
    public DbSet<SupplierSyncRun> SupplierSyncRuns { get; set; }
    public DbSet<SupplierListingSnapshot> SupplierListingSnapshots { get; set; }
    public DbSet<SupplierFeed> SupplierFeeds { get; set; }
    public DbSet<SupplierProduct> SupplierProducts { get; set; }
    public DbSet<Supplier> Suppliers { get; set; }
    public DbSet<SupplierDeliveryOption> SupplierDeliveryOptions { get; set; }
    public DbSet<BulkJobRecord> BulkJobs { get; set; }
    public DbSet<SupplierPriceListItem> SupplierPriceListItems { get; set; }
    public DbSet<CatalogCategory> CatalogCategories { get; set; }
    public DbSet<ChannelCategoryMapping> ChannelCategoryMappings { get; set; }
    public DbSet<SupplierCategoryMapping> SupplierCategoryMappings { get; set; }
    public DbSet<ProductTag> ProductTags { get; set; }
    public DbSet<SupplierProductTag> SupplierProductTags { get; set; }

    /// <summary>
    /// Indexes (used when EF creates the schema, i.e. PostgreSQL and new databases; existing SQLite databases
    /// get the same indexes from LegacySqliteSchema).
    /// </summary>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<AppUser>().HasIndex(u => u.Email).IsUnique();
        modelBuilder.Entity<EbayTokenInfo>().HasIndex(t => t.UserId);
        modelBuilder.Entity<Listing>().HasIndex(l => l.OwnerUserId);
        modelBuilder.Entity<Listing>().HasIndex(l => l.SupplierProductId);
        modelBuilder.Entity<Listing>().HasIndex(l => l.Sku);
        modelBuilder.Entity<SupplierFeed>().HasIndex(f => new { f.Supplier, f.FeedType, f.Url }).IsUnique();
        modelBuilder.Entity<SupplierProduct>().HasIndex(p => new { p.Supplier, p.SupplierSku });
        modelBuilder.Entity<SupplierProduct>().HasIndex(p => new { p.Supplier, p.ExternalProductId });
        modelBuilder.Entity<SupplierProduct>().HasIndex(p => p.GroupKey);
        modelBuilder.Entity<SupplierListingSnapshot>().HasIndex(s => new { s.ListingId, s.CheckedAtUtc });
        modelBuilder.Entity<Supplier>().HasIndex(s => s.Code).IsUnique();
        modelBuilder.Entity<SupplierDeliveryOption>().HasIndex(o => o.SupplierId);
        modelBuilder.Entity<BulkJobRecord>().HasIndex(j => new { j.Type, j.Tag });
        modelBuilder.Entity<SupplierPriceListItem>().HasIndex(i => new { i.Supplier, i.ProductCode });
        modelBuilder.Entity<CatalogCategory>().HasIndex(c => c.ParentId);
        modelBuilder.Entity<ChannelCategoryMapping>().HasIndex(m => new { m.CatalogCategoryId, m.Channel }).IsUnique();
        modelBuilder.Entity<SupplierCategoryMapping>().HasIndex(m => new { m.Supplier, m.SourceType, m.SourceValue }).IsUnique();
        modelBuilder.Entity<ProductTag>().HasIndex(t => new { t.Supplier, t.NormalizedName }).IsUnique();
        modelBuilder.Entity<SupplierProductTag>().HasIndex(t => new { t.SupplierProductId, t.ProductTagId }).IsUnique();
        modelBuilder.Entity<SupplierProductTag>().HasIndex(t => t.ProductTagId);
        modelBuilder.Entity<SupplierProduct>().HasIndex(p => p.CatalogCategoryId);
    }
}

/// <summary>
/// Status/progress of a background job started from a page (bulk extract, bulk publish, imports, ...).
/// Hangfire runs the job; this row is what the page's progress bar polls, so it survives refreshes and restarts.
/// </summary>
public class BulkJobRecord
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;   // extract | publish | enrich | import
    public string? Tag { get; set; }                   // e.g. supplier code for imports
    public int OwnerUserId { get; set; }
    public string Status { get; set; } = "Queued";     // Queued | Running | Completed | Failed
    public string? Message { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string? ErrorsJson { get; set; }
    public string? HangfireJobId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>
/// "My categories": the app's own category tree, independent of any supplier. Supplier categories and type tags are
/// mapped onto it (<see cref="SupplierCategoryMapping"/>), and it is mapped onto each sales channel
/// (<see cref="ChannelCategoryMapping"/>: eBay now; NopCommerce and Amazon later).
/// </summary>
public class CatalogCategory
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>A catalog category's category on a sales channel, e.g. eBay category 46782 "Candles".</summary>
public class ChannelCategoryMapping
{
    public int Id { get; set; }
    public int CatalogCategoryId { get; set; }
    /// <summary>"eBay", later "NopCommerce", "Amazon" (for Amazon: the product type).</summary>
    public string Channel { get; set; } = string.Empty;
    public string ExternalId { get; set; } = string.Empty;
    public string? ExternalName { get; set; }
}

/// <summary>
/// Maps a supplier category (SourceType "category", value = category path) or type tag (SourceType "tag",
/// value = tag's normalised name) to one of my categories. Set once; every import then sorts products automatically.
/// </summary>
public class SupplierCategoryMapping
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceValue { get; set; } = string.Empty;
    public int CatalogCategoryId { get; set; }
}

/// <summary>A supplier's product tag/collection, e.g. Eden "Carbon Filters". Per supplier, because names mean different things.</summary>
public class ProductTag
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Lower case, "&amp;" = "and", punctuation removed: "Pots, Tanks &amp; Trays" = "pots tanks and trays".</summary>
    public string NormalizedName { get; set; } = string.Empty;
    /// <summary>"type" (what the product is: used for filtering and categories), "brand", "promo" (Sale, Best Sellers) or "ignore".</summary>
    public string Type { get; set; } = ProductTagTypes.Type;
}

public static class ProductTagTypes
{
    public const string Type = "type";
    public const string Brand = "brand";
    public const string Promo = "promo";
    public const string Ignore = "ignore";
    public static readonly string[] All = { Type, Brand, Promo, Ignore };
}

public class SupplierProductTag
{
    public int Id { get; set; }
    public int SupplierProductId { get; set; }
    public int ProductTagId { get; set; }
}

/// <summary>
/// One row of a supplier's trade price list (e.g. the Safelincs Excel file uploaded on /supplier-feeds).
/// Uploading a new list replaces the supplier's rows. Matched to products by <see cref="SupplierProduct.SupplierSku"/>.
/// </summary>
public class SupplierPriceListItem
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    /// <summary>Trade price, ex VAT.</summary>
    public decimal Price { get; set; }
    /// <summary>Supplier's delivery charge for this product, ex VAT (when the list has one).</summary>
    public decimal? DeliveryCost { get; set; }
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A product source (DropShipMall, Eden Horticulture, Safelincs, ...). <see cref="Code"/> matches
/// <see cref="SupplierProduct.Supplier"/>; <see cref="WebsiteHost"/> matches listings created from a URL.
/// </summary>
public class Supplier
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? WebsiteHost { get; set; }
    /// <summary>Short code that starts every SKU for this supplier's products, e.g. "DSM" → DSM-S16301.</summary>
    public string? SkuPrefix { get; set; }
    /// <summary>True when the supplier's product prices already include VAT.</summary>
    public bool PricesIncludeVat { get; set; }
    public decimal VatRatePercent { get; set; } = 20m;
    /// <summary>Default markup applied to cost (inc VAT) when suggesting an item price.</summary>
    public decimal DefaultMarginPercent { get; set; } = 20m;
    /// <summary>
    /// Enabled. A disabled supplier is not imported or synced, and its products and listings are hidden in the app
    /// (listings stay on eBay unless their quantity is set to 0 from /supplier-feeds).
    /// </summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>What the supplier charges us to deliver an order (e.g. Standard £4.99, Express £9.95).</summary>
public class SupplierDeliveryOption
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Comma-separated supplier postage categories this option applies to by default (e.g. "Standard Parcel,Small Parcel").</summary>
    public string? PostageCategories { get; set; }
    public decimal Cost { get; set; }
    public bool CostIncludesVat { get; set; }
    /// <summary>Used when no postage-category match is found.</summary>
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

public class AppUser
{
    public int Id { get; set; }
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public string Role { get; set; } = Shared.AuthRoles.Lister;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class Listing
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }
    public int? EbayAccountId { get; set; }
    public int? SupplierProductId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; } = 1;
    public string Status { get; set; } = "Draft";
    public string? EbayItemId { get; set; }
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? FulfillmentPolicyId { get; set; }
    public string? PaymentPolicyId { get; set; }
    public string? ReturnPolicyId { get; set; }
    public string? Type { get; set; }
    public string? Brand { get; set; }
    public string? Colour { get; set; }
    public string? Sku { get; set; }
    public string? EbayOfferId { get; set; }
    public string? ImageUrlsJson { get; set; }
    public string? SourceUrl { get; set; }
    public decimal? SourcePrice { get; set; }
    public int? SourceStockLevel { get; set; }
    public string? SourceProductCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Target marketplace. Only "eBay" is implemented today.</summary>
    public string Marketplace { get; set; } = "eBay";
    public string? DeliveryOptionName { get; set; }
    /// <summary>Delivery charge added to the item price (gross, inc VAT where applicable).</summary>
    public decimal? DeliveryCost { get; set; }
    /// <summary>Always true for now: delivery is folded into the marketplace price (no separate postage yet).</summary>
    public bool IncludeDeliveryInPrice { get; set; } = true;
    /// <summary>JSON object of item specifics (e.g. {"Material":"Plastic"}) sent to the marketplace.</summary>
    public string? ItemSpecificsJson { get; set; }
    public string? Barcode { get; set; }
    /// <summary>The supplier's original title, kept for tracing after the listing title is optimised.</summary>
    public string? SourceTitle { get; set; }
    /// <summary>The supplier's own category (e.g. "Fire Extinguisher ID Signs"). The eBay category is CategoryId/CategoryName.</summary>
    public string? SupplierCategory { get; set; }
    /// <summary>Set when the eBay quantity was set to 0 because the supplier was disabled; cleared when restored.</summary>
    public DateTime? QuantityZeroedAtUtc { get; set; }
    /// <summary>My category (from the supplier product's mapping, unless chosen by hand).</summary>
    public int? CatalogCategoryId { get; set; }
    /// <summary>True when the user picked <see cref="CatalogCategoryId"/>; mapping changes then leave it alone.</summary>
    public bool CatalogCategoryIsManual { get; set; }
}

public class Policy
{
    public int Id { get; set; }
    public string Marketplace { get; set; } = string.Empty; // e.g. "ebay", "amazon"
    public string PolicyType { get; set; } = string.Empty; // e.g. "payment", "fulfillment", "return"
    public string PolicyKey { get; set; } = string.Empty; // the provider policy id
    public string Name { get; set; } = string.Empty; // human friendly name
    public bool IsDefault { get; set; } = false;
}

public class EbayTokenInfo
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "eBay Account";
    public bool IsDefault { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiryTime { get; set; }
    public DateTime RefreshTokenExpiryTime { get; set; }
}

public class SupplierSyncRun
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public int ProcessedCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public string TriggeredBy { get; set; } = "scheduler";
}

public class SupplierListingSnapshot
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public int? SyncRunId { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string? SupplierSku { get; set; }
    public decimal? SupplierPrice { get; set; }
    public string StockStatus { get; set; } = "Unknown";
    public string? StockText { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}

public class SupplierFeed
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string FeedType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CookieHeader { get; set; }
    public bool IsRecommended { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public class SupplierProduct
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public int? SupplierFeedId { get; set; }
    public string? ExternalProductId { get; set; }
    public string? Handle { get; set; }
    public string? SupplierSku { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Brand { get; set; }
    public string? ProductUrl { get; set; }
    public string? ImageUrl { get; set; }
    public string? CategoryName { get; set; }
    public string? ImageUrlsJson { get; set; } // JSON array of image URLs
    public decimal? SupplierPrice { get; set; }
    public int? StockLevel { get; set; }
    public decimal? SuggestedPrice { get; set; }
    public string Currency { get; set; } = "GBP";
    public bool IsActive { get; set; } = true;
    public string? RawPayloadJson { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Supplier's delivery class, e.g. "Standard Parcel", "Fragile", "Oversize".</summary>
    public string? PostageCategory { get; set; }
    public string? Barcode { get; set; }
    /// <summary>JSON object of specifications scraped from the supplier product page.</summary>
    public string? SpecificsJson { get; set; }
    /// <summary>When description/specifics were last fetched from the supplier website.</summary>
    public DateTime? DetailsFetchedAtUtc { get; set; }

    // ---- Variants. One SupplierProduct row per sellable variant (size/colour);
    // rows of the same supplier product share GroupKey, ready for multi-variation listings later.
    /// <summary>Supplier's parent product id (e.g. Shopify product id). Null for single products without one.</summary>
    public string? GroupKey { get; set; }
    /// <summary>Variant label, e.g. "1L" or "100 Bag". Null for single-variant products.</summary>
    public string? VariantName { get; set; }
    /// <summary>JSON object of variant options, e.g. {"Size":"1L"}.</summary>
    public string? VariantOptionsJson { get; set; }

    /// <summary>Supplier's recommended retail price, when known.</summary>
    public decimal? RrpPrice { get; set; }
    /// <summary>True while <see cref="SupplierPrice"/> is not a confirmed trade price.</summary>
    public bool PriceIsEstimate { get; set; }
    public DateTime? TradePriceFetchedAtUtc { get; set; }
    /// <summary>
    /// Set when the product no longer appears in the supplier's full catalogue (removed from their site);
    /// cleared if it comes back. Separate from IsActive, which is the user's own delete.
    /// </summary>
    public DateTime? RemovedFromSupplierAtUtc { get; set; }
    /// <summary>Supplier's delivery charge for this product, ex VAT, when it is per product (Safelincs price list / product page).</summary>
    public decimal? DeliveryCost { get; set; }
    /// <summary>Supplier's full category path, e.g. "Extinguishers &amp; Equipment › Fire Extinguisher ID Signs" (null = just CategoryName).</summary>
    public string? CategoryPath { get; set; }
    /// <summary>My category, worked out from the supplier category/tag mappings.</summary>
    public int? CatalogCategoryId { get; set; }
}
