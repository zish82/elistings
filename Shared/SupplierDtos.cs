namespace Shared;

public class SupplierDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? WebsiteHost { get; set; }
    public string? SkuPrefix { get; set; }
    public bool PricesIncludeVat { get; set; }
    public decimal VatRatePercent { get; set; } = 20m;
    public decimal DefaultMarginPercent { get; set; } = 20m;
    public bool IsActive { get; set; } = true;
    /// <summary>eBay category whose fee rate is used for this supplier's products (and listings without an eBay category), e.g. 159912 Garden &amp; Patio.</summary>
    public string? FeeCategoryId { get; set; }
    public List<SupplierDeliveryOptionDto> DeliveryOptions { get; set; } = new();
    /// <summary>Postage classes found in this supplier's imported products (e.g. "Standard Parcel"). Read-only.</summary>
    public List<string> KnownPostageCategories { get; set; } = new();
}

public class SupplierDeliveryOptionDto
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? PostageCategories { get; set; }
    public decimal Cost { get; set; }
    public bool CostIncludesVat { get; set; }
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Supplier, its delivery options and the suggested option for one listing/product.</summary>
public class ListingSupplierContextDto
{
    public SupplierDto? Supplier { get; set; }
    public string? PostageCategory { get; set; }
    public int? SuggestedDeliveryOptionId { get; set; }
    /// <summary>True when the server can run a live stock check for this listing's supplier.</summary>
    public bool SupportsStockCheck { get; set; }
}

public class EnrichResultDto
{
    public bool Success { get; set; }
    public string? Message { get; set; }
    public ListingDto? Listing { get; set; }
}

/// <summary>
/// Shared VAT/delivery maths so server and client agree. All results are gross (inc VAT) amounts.
/// </summary>
public static class PricingMath
{
    public static decimal AddVat(decimal amount, bool includesVat, decimal vatRatePercent)
        => includesVat ? amount : Math.Round(amount * (1 + vatRatePercent / 100m), 2, MidpointRounding.AwayFromZero);

    public static decimal? CostIncVat(decimal? supplierPrice, SupplierDto? supplier)
        => supplierPrice.HasValue
            ? AddVat(supplierPrice.Value, supplier?.PricesIncludeVat ?? true, supplier?.VatRatePercent ?? 0m)
            : null;

    public static decimal DeliveryIncVat(SupplierDeliveryOptionDto option, SupplierDto supplier)
        => AddVat(option.Cost, option.CostIncludesVat, supplier.VatRatePercent);

    /// <summary>Cost (inc VAT) plus margin, rounded up to the next .99.</summary>
    public static decimal? SuggestItemPrice(decimal? costIncVat, decimal marginPercent)
    {
        if (!costIncVat.HasValue || costIncVat <= 0) return null;
        var target = costIncVat.Value * (1 + marginPercent / 100m);
        var rounded = Math.Floor(target) + 0.99m;
        if (rounded < target) rounded += 1m;
        return rounded;
    }

    /// <summary>The price sent to the marketplace.</summary>
    public static decimal ListingTotal(decimal itemPrice, decimal? deliveryCost, bool includeDelivery)
        => itemPrice + (includeDelivery ? (deliveryCost ?? 0m) : 0m);
}

/// <summary>Result of uploading a supplier trade price list.</summary>
public class PriceListUploadResultDto
{
    public int Rows { get; set; }
    public int ProductsUpdated { get; set; }
    public List<string> Notes { get; set; } = new();
}

public class PriceListStatusDto
{
    public string Supplier { get; set; } = string.Empty;
    public int Rows { get; set; }
    public DateTime? UploadedAtUtc { get; set; }
    /// <summary>Imported products whose SKU is in the price list.</summary>
    public int MatchedProducts { get; set; }
}

/// <summary>Supplier overview for /supplier-feeds: enabled flag, counts and price list.</summary>
public class SupplierOverviewDto
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public int Products { get; set; }
    public int Listings { get; set; }
    public int PublishedListings { get; set; }
    /// <summary>Published listings whose eBay quantity was set to 0 when the supplier was disabled.</summary>
    public int ZeroedListings { get; set; }
    /// <summary>True when the supplier takes an uploaded trade price list (e.g. Safelincs).</summary>
    public bool UsesPriceList { get; set; }
    public PriceListStatusDto? PriceList { get; set; }
}

/// <summary>One row of the Products page "Categories" view.</summary>
public class SupplierCategorySummaryDto
{
    /// <summary>Supplier grouping: the category (null for products without one).</summary>
    public string? Category { get; set; }
    /// <summary>Tag grouping: the tag id (0 = no tag).</summary>
    public int? Id { get; set; }
    /// <summary>What to show, e.g. the category path or tag name.</summary>
    public string Label { get; set; } = string.Empty;
    public int Products { get; set; }
    public int InStock { get; set; }
    public int AlreadyAdded { get; set; }
}
