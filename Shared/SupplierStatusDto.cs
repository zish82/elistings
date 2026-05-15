namespace Shared;

public class SupplierStatusDto
{
    public int ListingId { get; set; }
    public string Supplier { get; set; } = "edenhorticulture";
    public string? SupplierSku { get; set; }
    public decimal? SupplierPrice { get; set; }
    public string StockStatus { get; set; } = "Unknown";
    public string? StockText { get; set; }
    public DateTime? LastCheckedUtc { get; set; }
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
}
