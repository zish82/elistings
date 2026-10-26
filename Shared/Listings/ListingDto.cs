namespace Shared;

public class ListingDto
{
    public int Id { get; set; }
    public int? EbayAccountId { get; set; }
    public int? SupplierProductId { get; set; }
    public string? EbayAccountName { get; set; }
    public int OwnerUserId { get; set; }
    public string? OwnerEmail { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; } = 1;
    public string Status { get; set; } = "Draft"; // Draft, Published
    public string? EbayItemId { get; set; }
    public string? Sku { get; set; }
    public string? EbayOfferId { get; set; }

    /// <summary>Only in the publish response: pictures that were left out, with the reason. Not stored.</summary>
    public List<string> PublishWarnings { get; set; } = new();

    // eBay Specifics
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
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
    public int? SourceStockLevel { get; set; }
    public string? SourceProductCode { get; set; }

    public string Marketplace { get; set; } = "eBay";
    public string? DeliveryOptionName { get; set; }
    public decimal? DeliveryCost { get; set; }
    public bool IncludeDeliveryInPrice { get; set; } = true;
    public Dictionary<string, string> ItemSpecifics { get; set; } = new();
    public string? Barcode { get; set; }
    public string? SourceTitle { get; set; }
    /// <summary>Group of the linked supplier product (variants of the same product share it). Read-only.</summary>
    public string? SupplierGroupKey { get; set; }
    /// <summary>The supplier's own category (the eBay category is <see cref="CategoryName"/>).</summary>
    public string? SupplierCategory { get; set; }
    /// <summary>When the eBay quantity was set to 0 because the supplier was disabled. Read-only.</summary>
    public DateTime? QuantityZeroedAtUtc { get; set; }
    /// <summary>Supplier code of the linked product (null for listings created from a URL). Read-only.</summary>
    public string? SupplierCode { get; set; }
    /// <summary>True when the listing's supplier is disabled (the dashboard hides these by default). Read-only.</summary>
    public bool SupplierDisabled { get; set; }
    /// <summary>Estimated profit per sale after cost, delivery and eBay fees (null when the source price is unknown). Read-only.</summary>
    public decimal? EstimatedProfit { get; set; }
    public decimal? EstimatedFees { get; set; }
    /// <summary>True when <see cref="EstimatedProfit"/> is below the minimum set in the eBay fee settings. Read-only.</summary>
    public bool BelowMinProfit { get; set; }

    /// <summary>Item price + delivery (when included): what the marketplace listing is priced at.</summary>
    public decimal TotalPrice => PricingMath.ListingTotal(Price, DeliveryCost, IncludeDeliveryInPrice);
}
