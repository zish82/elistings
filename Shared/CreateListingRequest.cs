namespace Shared;

public class CreateListingRequest
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    [System.ComponentModel.DataAnnotations.Range(0.99, 1000000.00, ErrorMessage = "Price must be at least £0.99 for eBay UK.")]
    public decimal Price { get; set; } = 0.99m;
    public string? CategoryId { get; set; }
    public string? FulfillmentPolicyId { get; set; }
    public string? PaymentPolicyId { get; set; }
    public string? ReturnPolicyId { get; set; }
    public string? Type { get; set; } = "Not Specified";
    public string? Brand { get; set; } = "Unbranded";
    public string? Colour { get; set; } = "Multicoloured";

    public string? SourceUrl { get; set; }
    public decimal? SourcePrice { get; set; }
    public string? SourceProductCode { get; set; }

    public List<string> ImageUrls { get; set; } = new();
}
