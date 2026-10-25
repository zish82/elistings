using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>
/// A product source (DropShipMall, Eden Horticulture, Safelincs, ...). <see cref="Code"/> matches
/// <see cref="SupplierProduct.Supplier"/>; <see cref="WebsiteHost"/> matches listings created from a URL.
/// </summary>
public class Supplier
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? WebsiteHost { get; set; }
    /// <summary>Short code that starts every SKU for this supplier's products, e.g. "DSM" → DSM-S16301.</summary>
    public string? SkuPrefix { get; set; }
    /// <summary>True when the supplier's product prices already include VAT.</summary>
    public bool PricesIncludeVat { get; set; }
    public decimal VatRatePercent { get; set; } = 20m;
    /// <summary>Default markup applied to cost (inc VAT) when suggesting an item price.</summary>
    public decimal DefaultMarginPercent { get; set; } = 20m;
    /// <summary>
    /// Enabled. A disabled supplier is not imported or synced, and its products and listings are hidden in the app
    /// (listings stay on eBay unless their quantity is set to 0 from /supplier-feeds).
    /// </summary>
    public bool IsActive { get; set; } = true;
    /// <summary>eBay category whose fee rate estimates this supplier's products (e.g. 159912 Garden &amp; Patio).</summary>
    public string? FeeCategoryId { get; set; }
    /// <summary>
    /// "Enable background sync": the scheduled (nightly / every few hours) imports and stock checks for this supplier.
    /// Off = no scheduled calls to the supplier; manual imports and publishing still work.
    /// </summary>
    public bool BackgroundSyncEnabled { get; set; } = true;
}
