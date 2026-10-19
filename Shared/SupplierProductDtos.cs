namespace Shared;

public class SupplierProductListItemDto
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? SupplierSku { get; set; }
    public string? ProductUrl { get; set; }
    public string? ImageUrl { get; set; }
    public decimal? SupplierPrice { get; set; }
    public int? StockLevel { get; set; }
    public decimal? SuggestedPrice { get; set; }
    public bool AlreadyAdded { get; set; }
    public int? ExistingListingId { get; set; }
    public string? CategoryName { get; set; }
    public string? ImageUrlsJson { get; set; } // JSON array of image URLs
    public decimal? RrpPrice { get; set; }
    /// <summary>True when SupplierPrice isn't a confirmed trade price yet.</summary>
    public bool PriceIsEstimate { get; set; }
    public string? GroupKey { get; set; }
    public string? VariantName { get; set; }
    /// <summary>How many visible products share <see cref="GroupKey"/> (1 when not part of a group).</summary>
    public int GroupSize { get; set; } = 1;
    /// <summary>The product's type tags (e.g. "Carbon Filters").</summary>
    public List<string> Tags { get; set; } = new();

    /// <summary>
    /// Estimated profit per sale at the suggested price with the default delivery: after the supplier cost (inc VAT
    /// unless VAT-registered), delivery and estimated eBay fees (see /suppliers → eBay fees). Null when unknown.
    /// </summary>
    public decimal? EstimatedProfit { get; set; }
    /// <summary>Estimated eBay fees for that sale (final value + per-order + regulatory, plus VAT on fees when not VAT-registered).</summary>
    public decimal? EstimatedFees { get; set; }
}

/// <summary>Filters and sort order for the /products grid (bound from the query string).</summary>
public class SupplierProductQuery
{
    public const int DefaultLimit = 500;
    /// <summary><see cref="Category"/> value that selects products without a category.</summary>
    public const string Uncategorised = "__uncategorised__";
    public const int MaxLimit = 2000;

    public string? Supplier { get; set; }
    public string? Search { get; set; }
    public string? Category { get; set; }
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool InStockOnly { get; set; }
    public bool HideAdded { get; set; }
    /// <summary>Only products of this group (all variants of one supplier product).</summary>
    public string? GroupKey { get; set; }
    /// <summary>Products with this tag (0 = products without any type tag).</summary>
    public int? TagId { get; set; }
    /// <summary>One of <see cref="SupplierProductSort"/>.</summary>
    public string? Sort { get; set; }
    public int Limit { get; set; } = DefaultLimit;
}

public static class SupplierProductSort
{
    public const string PriceHighToLow = "price_desc";
    public const string PriceLowToHigh = "price_asc";
    public const string ProfitHighToLow = "profit_desc";
    public const string StockHighToLow = "stock_desc";
    public const string Title = "title";
}

public class SupplierProductDetailDto : SupplierProductListItemDto
{
    public string? Description { get; set; }
    public string? Brand { get; set; }
    public string Currency { get; set; } = "GBP";
    public DateTime UpdatedAtUtc { get; set; }
}

public class AddSupplierProductsRequest
{
    public List<int> ProductIds { get; set; } = new();
    public bool AllowDuplicates { get; set; }
}

public class AddSupplierProductsResult
{
    public int Added { get; set; }
    public int SkippedAlreadyAdded { get; set; }
    public List<int> AddedListingIds { get; set; } = new();
    /// <summary>Background job fetching descriptions/specifics for the new listings (null if nothing to fetch).</summary>
    public string? EnrichmentJobId { get; set; }
}

public class BulkProductDeleteResult
{
    public int Deleted { get; set; }
}
