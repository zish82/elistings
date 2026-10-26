namespace Shared;

public class EbayListingResponse
{
    public string Sku { get; set; } = string.Empty;
    public string OfferId { get; set; } = string.Empty;
    public string ListingId { get; set; } = string.Empty;

    /// <summary>Pictures that couldn't be uploaded (the listing was published with the others), with the reason.</summary>
    public List<string> ImageWarnings { get; set; } = new();
}
