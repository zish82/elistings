namespace Shared;

public class ListingDto
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Status { get; set; } = "Draft"; // Draft, Published
    public string? EbayItemId { get; set; }
    public string? Sku { get; set; }
    public string? EbayOfferId { get; set; }
    
    // eBay Specifics
    public string? CategoryId { get; set; }
    public string? FulfillmentPolicyId { get; set; }
    public string? PaymentPolicyId { get; set; }
    public string? ReturnPolicyId { get; set; }

    // Original product URL (if created from external product page)
    public string? SourceUrl { get; set; }
    
    // Mandatory Aspects
    public string? Type { get; set; } = "Not Specified";
    public string? Brand { get; set; } = "Unbranded";
    public string? Colour { get; set; } = "Multicoloured";

    public List<string> ImageUrls { get; set; } = new();
    public decimal? SourcePrice { get; set; }
    public string? SourceProductCode { get; set; }
}
