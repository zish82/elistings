namespace Shared;

public class CreateListingRequest
{
    public int? EbayAccountId { get; set; }
    public int? SupplierProductId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    // 0 is allowed so drafts without a price can be saved; publishing checks the total is at least £0.99.
    [System.ComponentModel.DataAnnotations.Range(0, 1000000.00, ErrorMessage = "Price cannot be negative.")]
    public decimal Price { get; set; } = 0.99m;
    [System.ComponentModel.DataAnnotations.Range(1, 100000, ErrorMessage = "Quantity must be at least 1.")]
    public int Quantity { get; set; } = 1;
    [System.ComponentModel.DataAnnotations.StringLength(50, ErrorMessage = "SKU must be 50 characters or fewer.")]
    [System.ComponentModel.DataAnnotations.RegularExpression("^[A-Za-z0-9_\\-]*$", ErrorMessage = "SKU can only contain letters, numbers, hyphens and underscores.")]
    public string? Sku { get; set; }
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? FulfillmentPolicyId { get; set; }
    public string? PaymentPolicyId { get; set; }
    public string? ReturnPolicyId { get; set; }
    public string? Type { get; set; } = "Not Specified";
    public string? Brand { get; set; } = "Unbranded";
    public string? Colour { get; set; } = "Multicoloured";

    public string? SourceUrl { get; set; }
    public decimal? SourcePrice { get; set; }
    public int? SourceStockLevel { get; set; }
    public string? SourceProductCode { get; set; }

    public List<string> ImageUrls { get; set; } = new();

    public string Marketplace { get; set; } = "eBay";
    public string? DeliveryOptionName { get; set; }
    [System.ComponentModel.DataAnnotations.Range(0, 10000, ErrorMessage = "Delivery cost cannot be negative.")]
    public decimal? DeliveryCost { get; set; }
    public bool IncludeDeliveryInPrice { get; set; } = true;
    public Dictionary<string, string> ItemSpecifics { get; set; } = new();
    public string? Barcode { get; set; }
}
