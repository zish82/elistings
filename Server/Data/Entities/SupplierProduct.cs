using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

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
}
