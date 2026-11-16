namespace Shared;

/// <summary>
/// One variation of a listing (e.g. Size = "50 LEDs"): its own SKU, price and quantity. Marketplace-neutral;
/// each marketplace publisher translates the variants of a listing into its own format.
/// </summary>
public class ListingVariantDto
{
    public int Id { get; set; }
    /// <summary>Blank on save = generated from the listing's SKU.</summary>
    public string? Sku { get; set; }
    /// <summary>What differs, e.g. {"Size":"50 LEDs"}. Every variant of a listing uses the same option names.</summary>
    public Dictionary<string, string> Options { get; set; } = new();
    /// <summary>Item price (the listing's delivery cost is added when publishing, as for single listings).</summary>
    public decimal Price { get; set; }
    public int Quantity { get; set; } = 1;
    /// <summary>Optional picture for this variant (must be eBay-hosted or uploaded like other pictures).</summary>
    public string? ImageUrl { get; set; }
    public decimal? SourcePrice { get; set; }
    public int? SupplierProductId { get; set; }
    public string? OfferId { get; set; }
}
