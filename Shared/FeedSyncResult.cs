namespace Shared;

public class FeedSyncResult
{
    public string Supplier { get; set; } = "DropShipMall";
    public string FeedType { get; set; } = string.Empty;
    public int ProcessedRows { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Skipped { get; set; }
    public int LinkedListingsUpdated { get; set; }
    public string? ErrorMessage { get; set; }
    /// <summary>Set when the import continues as a background job (poll api/listings/bulk-jobs/{id}).</summary>
    public string? JobId { get; set; }
    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);
}

public class SupplierImportStatusDto
{
    /// <summary>Latest background import job for the supplier since the server started (null if none).</summary>
    public string? JobId { get; set; }
    public bool IsRunning { get; set; }
    /// <summary>When any of the supplier's products was last updated (i.e. last import/sync).</summary>
    public DateTime? LastUpdatedAtUtc { get; set; }
}
