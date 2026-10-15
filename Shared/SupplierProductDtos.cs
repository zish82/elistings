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
