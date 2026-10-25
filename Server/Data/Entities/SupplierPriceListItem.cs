using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>
/// One row of a supplier's trade price list (e.g. the Safelincs Excel file uploaded on /supplier-feeds).
/// Uploading a new list replaces the supplier's rows. Matched to products by <see cref="SupplierProduct.SupplierSku"/>.
/// </summary>
public class SupplierPriceListItem
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string ProductCode { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Category { get; set; }
    /// <summary>Trade price, ex VAT.</summary>
    public decimal Price { get; set; }
    /// <summary>Supplier's delivery charge for this product, ex VAT (when the list has one).</summary>
    public decimal? DeliveryCost { get; set; }
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
}
