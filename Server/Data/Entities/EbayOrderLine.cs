using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class EbayOrderLine
{
    public int Id { get; set; }
    public int EbayOrderId { get; set; }
    public string LineItemId { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public string? LegacyItemId { get; set; }
    public string Title { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
    public decimal LineTotal { get; set; }
    /// <summary>Matched listing / supplier product (by SKU), when found.</summary>
    public int? ListingId { get; set; }
    public int? SupplierProductId { get; set; }
    /// <summary>Supplier cost per unit inc VAT, when matched (snapshot at the time of the first sync).</summary>
    public decimal? UnitCost { get; set; }
    /// <summary>Supplier delivery for this line, inc VAT, when matched.</summary>
    public decimal? DeliveryCost { get; set; }
}
