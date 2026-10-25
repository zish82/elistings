using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>
/// Status/progress of a background job started from a page (bulk extract, bulk publish, imports, ...).
/// Hangfire runs the job; this row is what the page's progress bar polls, so it survives refreshes and restarts.
/// </summary>
public class BulkJobRecord
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;   // extract | publish | enrich | import
    public string? Tag { get; set; }                   // e.g. supplier code for imports
    public int OwnerUserId { get; set; }
    public string Status { get; set; } = "Queued";     // Queued | Running | Completed | Failed
    public string? Message { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public string? ErrorsJson { get; set; }
    public string? HangfireJobId { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
}
