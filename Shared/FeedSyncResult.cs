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
    public bool IsSuccess => string.IsNullOrWhiteSpace(ErrorMessage);
}
