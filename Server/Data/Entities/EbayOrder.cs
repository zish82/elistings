using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>
/// An eBay order (sales report). eBay fields are refreshed by every sync; the cost/source fields are yours:
/// SupplierPrice is worked out from the matched listing when first seen (or typed in), SourceOrderId/SourceEmail are typed in.
/// </summary>
public class EbayOrder
{
    public int Id { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public int EbayAccountId { get; set; }
    public int OwnerUserId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastModifiedAtUtc { get; set; }
    public string? BuyerUsername { get; set; }
    /// <summary>What the buyer paid you (item + postage, excluding tax eBay collects itself).</summary>
    public decimal SaleTotal { get; set; }
    public decimal ItemsTotal { get; set; }
    public decimal PostageCharged { get; set; }
    public decimal RefundedTotal { get; set; }
    public string Currency { get; set; } = "GBP";
    public string? FulfillmentStatus { get; set; }
    public string? PaymentStatus { get; set; }
    public string? CancelState { get; set; }
    /// <summary>eBay's fees: actual (from the order) when eBay reports them, else estimated; incl. VAT when not VAT-registered.</summary>
    public decimal? Fees { get; set; }
    public bool FeesAreActual { get; set; }
    public string? TrackingNumber { get; set; }
    public string? DeliveryCompany { get; set; }
    public DateTime? ShippedAtUtc { get; set; }

    // ---- yours
    /// <summary>What the supplier charges you for the order (items inc VAT + delivery). Null = unknown (not matched).</summary>
    public decimal? SupplierPrice { get; set; }
    /// <summary>True when typed in (syncs then leave it alone).</summary>
    public bool SupplierPriceIsManual { get; set; }
    public string? Supplier { get; set; }
    /// <summary>The supplier's order number for this sale.</summary>
    public string? SourceOrderId { get; set; }
    /// <summary>Email account used to place the supplier order.</summary>
    public string? SourceEmail { get; set; }
    /// <summary>Link to the item at the supplier, typed in for sales whose item isn't in our catalogue.</summary>
    public string? SourceUrl { get; set; }
    public DateTime SyncedAtUtc { get; set; } = DateTime.UtcNow;
}
