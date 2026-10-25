using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>What the supplier charges us to deliver an order (e.g. Standard £4.99, Express £9.95).</summary>
public class SupplierDeliveryOption
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    /// <summary>Comma-separated supplier postage categories this option applies to by default (e.g. "Standard Parcel,Small Parcel").</summary>
    public string? PostageCategories { get; set; }
    public decimal Cost { get; set; }
    public bool CostIncludesVat { get; set; }
    /// <summary>Used when no postage-category match is found.</summary>
    public bool IsDefault { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}
