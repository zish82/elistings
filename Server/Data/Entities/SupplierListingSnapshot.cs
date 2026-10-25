using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class SupplierListingSnapshot
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public int? SyncRunId { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string? SupplierSku { get; set; }
    public decimal? SupplierPrice { get; set; }
    public string StockStatus { get; set; } = "Unknown";
    public string? StockText { get; set; }
    public DateTime CheckedAtUtc { get; set; } = DateTime.UtcNow;
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}
