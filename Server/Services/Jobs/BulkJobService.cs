using System.Linq.Expressions;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services.Jobs;

/// <summary>
/// Starts page-triggered jobs on Hangfire and reads their status for the progress bars.
/// Each job gets a BulkJobs row (created here as "Queued", updated by <see cref="JobProgress"/> while it runs).
/// </summary>
public class BulkJobService
{
    private readonly AppDbContext _context;
    private readonly IBackgroundJobClient _hangfire;

    public BulkJobService(AppDbContext context, IBackgroundJobClient hangfire)
    {
        _context = context;
        _hangfire = hangfire;
    }

    public Task<string> EnqueueBulkExtractAsync(int userId, string categoryUrl)
        => EnqueueAsync("extract", userId, tag: null, id => IsEdenUrl(categoryUrl)
            ? Job(j => j.BulkExtractEdenAsync(id, userId, categoryUrl))   // eden queue: one Eden job at a time
            : Job(j => j.BulkExtractAsync(id, userId, categoryUrl)));

    public Task<string> EnqueueBulkPublishAsync(int userId, List<int> listingIds)
        => EnqueueAsync("publish", userId, tag: null, id => Job(j => j.BulkPublishAsync(id, userId, listingIds)));

    public Task<string> EnqueueEnrichAsync(int userId, List<int> listingIds)
        => EnqueueAsync("enrich", userId, tag: null, id => Job(j => j.EnrichListingsAsync(id, userId, listingIds)));

    public Task<string> EnqueueSupplierImportAsync(int userId, string supplierCode)
        => EnqueueAsync("import", userId, tag: supplierCode, id =>
            supplierCode.Equals(EdenCatalogImportService.Code, StringComparison.OrdinalIgnoreCase)
                ? Job(j => j.ImportEdenCatalogueAsync(id, userId))
                : Job(j => j.ImportSupplierCatalogueAsync(id, userId, supplierCode)));

    public async Task<BulkJobDto?> GetAsync(string jobId)
    {
        var record = await _context.BulkJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
        return record == null ? null : ToDto(record);
    }

    /// <summary>Latest job of a type/tag (e.g. the last Eden import) so a page can re-attach after a refresh.</summary>
    public async Task<BulkJobRecord?> FindLatestAsync(string type, string? tag)
        => await _context.BulkJobs.AsNoTracking()
            .Where(j => j.Type == type && (tag == null ? j.Tag == null : j.Tag != null && j.Tag.ToLower() == tag.ToLower()))
            .OrderByDescending(j => j.CreatedAtUtc)
            .FirstOrDefaultAsync();

    public static bool IsActive(BulkJobRecord? job)
        => job != null && (job.Status == BulkJobStatus.Queued || job.Status == BulkJobStatus.Running);

    private async Task<string> EnqueueAsync(string type, int userId, string? tag, Func<string, Expression<Func<BackgroundJobs, Task>>> buildCall)
    {
        var record = new BulkJobRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = type,
            Tag = tag,
            OwnerUserId = userId,
            Status = BulkJobStatus.Queued,
            Message = "Queued – waiting to start...",
            CreatedAtUtc = DateTime.UtcNow
        };
        _context.BulkJobs.Add(record);
        await _context.SaveChangesAsync();

        // The queue (default / eden) and retry policy come from attributes on the BackgroundJobs methods.
        try
        {
            record.HangfireJobId = _hangfire.Enqueue(buildCall(record.Id));
        }
        catch (Exception ex)
        {
            record.Status = BulkJobStatus.Failed;
            record.Message = $"Could not queue the job: {ex.Message}";
            record.FinishedAtUtc = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            throw;
        }
        await _context.SaveChangesAsync();
        return record.Id;
    }

    private static Expression<Func<BackgroundJobs, Task>> Job(Expression<Func<BackgroundJobs, Task>> call) => call;

    private static bool IsEdenUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Host.Equals("edenhorticulture.co.uk", StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith(".edenhorticulture.co.uk", StringComparison.OrdinalIgnoreCase));

    public static BulkJobDto ToDto(BulkJobRecord r) => new()
    {
        Id = r.Id,
        Type = r.Type,
        Status = r.Status,
        Message = r.Message,
        Total = r.Total,
        Processed = r.Processed,
        Succeeded = r.Succeeded,
        Skipped = r.Skipped,
        Failed = r.Failed,
        Errors = string.IsNullOrEmpty(r.ErrorsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(r.ErrorsJson) ?? new List<string>(),
        StartedAtUtc = r.StartedAtUtc ?? r.CreatedAtUtc,
        FinishedAtUtc = r.FinishedAtUtc
    };
}
