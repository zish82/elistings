namespace Server.Data;

/// <summary>One variation of a multi-variation <see cref="Listing"/>. A listing with variants is published as one marketplace listing.</summary>
public class ListingVariant
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public int SortOrder { get; set; }
    public string Sku { get; set; } = string.Empty;
    /// <summary>JSON object, e.g. {"Size":"50 LEDs"}.</summary>
    public string OptionsJson { get; set; } = "{}";
    public decimal Price { get; set; }
    public int Quantity { get; set; } = 1;
    public string? ImageUrl { get; set; }
    public decimal? SourcePrice { get; set; }
    public int? SupplierProductId { get; set; }
    /// <summary>Marketplace offer id on the listing's main account (set when published).</summary>
    public string? OfferId { get; set; }
}
