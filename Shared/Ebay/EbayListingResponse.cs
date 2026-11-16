namespace Shared;

public class EbayListingResponse
{
    public string Sku { get; set; } = string.Empty;
    public string OfferId { get; set; } = string.Empty;
    public string ListingId { get; set; } = string.Empty;
    /// <summary>Variation listings: the offer id of each variant, by SKU.</summary>
    public Dictionary<string, string> VariantOfferIds { get; set; } = new();

    /// <summary>Pictures that couldn't be uploaded (the listing was published with the others), with the reason.</summary>
    public List<string> ImageWarnings { get; set; } = new();
}
