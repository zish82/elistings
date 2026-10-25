using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class SupplierSyncRun
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAtUtc { get; set; }
    public int ProcessedCount { get; set; }
    public int SuccessCount { get; set; }
    public int FailedCount { get; set; }
    public string TriggeredBy { get; set; } = "scheduler";
}
