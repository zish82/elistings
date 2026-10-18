using System.Collections.Concurrent;
using System.Security.Claims;
using Shared;

namespace Server.Services;

/// <summary>
/// Runs long bulk operations (extract / publish) in the background so the HTTP request
/// returns immediately and the browser polls for progress. Jobs live in memory only:
/// a server restart loses running jobs (work already saved to the database is kept).
/// </summary>
public class BulkJobRunner
{
    public sealed class BulkJob
    {
        private readonly object _lock = new();
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public string Type { get; init; } = string.Empty;
        public int OwnerUserId { get; init; }
        /// <summary>Optional grouping key, e.g. the supplier code for imports.</summary>
        public string? Tag { get; init; }
        public DateTime StartedAtUtc { get; } = DateTime.UtcNow;
        public DateTime? FinishedAtUtc { get; private set; }
        public string Status { get; private set; } = "Running";
        public string? Message { get; set; }
        public int Total { get; set; }
        public int Processed { get; private set; }
        public int Succeeded { get; private set; }
        public int Skipped { get; private set; }
        public int Failed { get; private set; }
        private readonly List<string> _errors = new();

        public void ItemSucceeded() { lock (_lock) { Processed++; Succeeded++; } }
        public void ItemSkipped() { lock (_lock) { Processed++; Skipped++; } }
        public void ItemFailed(string error) { lock (_lock) { Processed++; Failed++; _errors.Add(error); } }

        public void Finish(string status, string? message = null)
        {
            lock (_lock)
            {
                Status = status;
                if (message != null) Message = message;
                FinishedAtUtc = DateTime.UtcNow;
            }
        }

        public BulkJobDto ToDto()
        {
            lock (_lock)
            {
                return new BulkJobDto
                {
                    Id = Id,
                    Type = Type,
                    Status = Status,
                    Message = Message,
                    Total = Total,
                    Processed = Processed,
                    Succeeded = Succeeded,
                    Skipped = Skipped,
                    Failed = Failed,
                    Errors = _errors.ToList(),
                    StartedAtUtc = StartedAtUtc,
                    FinishedAtUtc = FinishedAtUtc
                };
            }
        }
    }

    private readonly ConcurrentDictionary<string, BulkJob> _jobs = new();
    private readonly IServiceScopeFactory _scopeFactory;

    public BulkJobRunner(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>
    /// Starts <paramref name="work"/> on a background thread inside a fresh DI scope that
    /// impersonates <paramref name="user"/> (so ICurrentUserService / eBay tokens resolve correctly).
    /// </summary>
    public BulkJob Start(string type, ClaimsPrincipal user, int ownerUserId, Func<IServiceProvider, BulkJob, Task> work, string? tag = null)
    {
        CleanupOldJobs();

        var job = new BulkJob { Type = type, OwnerUserId = ownerUserId, Tag = tag };
        _jobs[job.Id] = job;

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<BackgroundUserContext>().User = user;
            try
            {
                await work(scope.ServiceProvider, job);
                job.Finish("Completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BulkJob {job.Id}] {type} failed: {ex}");
                job.Finish("Failed", ex.Message);
            }
        });

        return job;
    }

    public BulkJob? Get(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    /// <summary>Most recent job of a type/tag (e.g. the last Eden import), so a page can re-attach after a refresh.</summary>
    public BulkJob? FindLatest(string type, string tag)
        => _jobs.Values
            .Where(j => j.Type == type && string.Equals(j.Tag, tag, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.StartedAtUtc)
            .FirstOrDefault();

    private void CleanupOldJobs()
    {
        var cutoff = DateTime.UtcNow.AddHours(-12);
        foreach (var kv in _jobs)
        {
            if (kv.Value.FinishedAtUtc.HasValue && kv.Value.FinishedAtUtc < cutoff)
            {
                _jobs.TryRemove(kv.Key, out _);
            }
        }
    }
}
