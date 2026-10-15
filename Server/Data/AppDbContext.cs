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
}
