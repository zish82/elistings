using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class Listing
{
    public int Id { get; set; }
    public int OwnerUserId { get; set; }
    public int? EbayAccountId { get; set; }
    public int? SupplierProductId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Quantity { get; set; } = 1;
    public string Status { get; set; } = "Draft";
    public string? EbayItemId { get; set; }
    public string? CategoryId { get; set; }
    public string? CategoryName { get; set; }
    public string? FulfillmentPolicyId { get; set; }
    public string? PaymentPolicyId { get; set; }
    public string? ReturnPolicyId { get; set; }
    public string? Type { get; set; }
    public string? Brand { get; set; }
    public string? Colour { get; set; }
    public string? Sku { get; set; }
    public string? EbayOfferId { get; set; }
    public string? ImageUrlsJson { get; set; }
    public string? SourceUrl { get; set; }
    public decimal? SourcePrice { get; set; }
    public int? SourceStockLevel { get; set; }
    public string? SourceProductCode { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Target marketplace. Only "eBay" is implemented today.</summary>
    public string Marketplace { get; set; } = "eBay";
    public string? DeliveryOptionName { get; set; }
    /// <summary>Delivery charge added to the item price (gross, inc VAT where applicable).</summary>
    public decimal? DeliveryCost { get; set; }
    /// <summary>Always true for now: delivery is folded into the marketplace price (no separate postage yet).</summary>
    public bool IncludeDeliveryInPrice { get; set; } = true;
    /// <summary>JSON object of item specifics (e.g. {"Material":"Plastic"}) sent to the marketplace.</summary>
    public string? ItemSpecificsJson { get; set; }
    public string? Barcode { get; set; }
    /// <summary>The supplier's original title, kept for tracing after the listing title is optimised.</summary>
    public string? SourceTitle { get; set; }
    /// <summary>The supplier's own category (e.g. "Fire Extinguisher ID Signs"). The eBay category is CategoryId/CategoryName.</summary>
    public string? SupplierCategory { get; set; }
    /// <summary>Set when the eBay quantity was set to 0 because the supplier was disabled; cleared when restored.</summary>
    public DateTime? QuantityZeroedAtUtc { get; set; }
}
