using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;

namespace Server.Services.Jobs;

/// <summary>
/// Progress reporter for one running job. Counters are kept in memory and written to the
/// BulkJobs row about once a second (and on finish) through its own DbContext, so the job's
/// own DbContext (which may call ChangeTracker.Clear()) never affects progress tracking.
/// </summary>
public sealed class JobProgress : IAsyncDisposable
{
    private const int MaxStoredErrors = 500;
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly IServiceScope _scope;
    private readonly AppDbContext _db;
    private readonly BulkJobRecord _record;
    private readonly List<string> _errors = new();
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _flushLoop;
    private bool _dirty;
    private bool _finished;

    private int _total, _processed, _succeeded, _skipped, _failed;
    private string? _message;

    private JobProgress(IServiceScope scope, AppDbContext db, BulkJobRecord record)
    {
        _scope = scope;
        _db = db;
        _record = record;
        _flushLoop = Task.Run(FlushLoopAsync);
    }

    /// <summary>Marks the job Running (resetting counters, e.g. when Hangfire re-runs it after a restart).</summary>
    public static async Task<JobProgress> BeginAsync(IServiceScopeFactory scopeFactory, string jobId)
    {
        var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var record = await db.BulkJobs.FirstOrDefaultAsync(j => j.Id == jobId)
                     ?? throw new InvalidOperationException($"Job record {jobId} not found.");

        record.Status = BulkJobStatus.Running;
        record.StartedAtUtc = DateTime.UtcNow;
        record.FinishedAtUtc = null;
        record.Total = record.Processed = record.Succeeded = record.Skipped = record.Failed = 0;
        record.ErrorsJson = null;
        record.Message = "Starting...";
        await db.SaveChangesAsync();
        return new JobProgress(scope, db, record);
    }

    public string JobId => _record.Id;
    public int OwnerUserId => _record.OwnerUserId;

    public int Total { get { lock (_lock) return _total; } set { lock (_lock) { _total = value; _dirty = true; } } }
    public string? Message { get { lock (_lock) return _message; } set { lock (_lock) { _message = value; _dirty = true; } } }
    public int Succeeded { get { lock (_lock) return _succeeded; } }
    public int Failed { get { lock (_lock) return _failed; } }

    public void ItemSucceeded() { lock (_lock) { _processed++; _succeeded++; _dirty = true; } }
    public void ItemSkipped() { lock (_lock) { _processed++; _skipped++; _dirty = true; } }

    /// <summary>Counts as succeeded, but the note (e.g. pictures left out) is listed with the errors.</summary>
    public void ItemSucceededWithWarning(string warning)
    {
        lock (_lock)
        {
            _processed++;
            _succeeded++;
            if (_errors.Count < MaxStoredErrors) _errors.Add("Warning – " + warning);
            _dirty = true;
        }
    }
    public void ItemFailed(string error)
    {
        lock (_lock)
        {
            _processed++;
            _failed++;
            if (_errors.Count < MaxStoredErrors) _errors.Add(error);
            _dirty = true;
        }
    }

    public Task CompleteAsync(string? message = null) => FinishAsync(BulkJobStatus.Completed, message);
    public Task FailAsync(string message) => FinishAsync(BulkJobStatus.Failed, message);

    private async Task FinishAsync(string status, string? message)
    {
        lock (_lock)
        {
            if (_finished) return;
            _finished = true;
            if (message != null) _message = message;
            _dirty = true;
        }
        _stop.Cancel();
        await FlushAsync(status, DateTime.UtcNow);
    }

    private async Task FlushLoopAsync()
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                await FlushAsync(null, null);
            }
        }
        catch (OperationCanceledException)
        {
            // finished
        }
    }

    private async Task FlushAsync(string? status, DateTime? finishedAt)
    {
        await _flushLock.WaitAsync();
        try
        {
            lock (_lock)
            {
                if (!_dirty && status == null) return;
                _record.Total = _total;
                _record.Processed = _processed;
                _record.Succeeded = _succeeded;
                _record.Skipped = _skipped;
                _record.Failed = _failed;
                _record.Message = _message;
                _record.ErrorsJson = _errors.Count == 0 ? null : JsonSerializer.Serialize(_errors);
                if (status != null) _record.Status = status;
                if (finishedAt != null) _record.FinishedAtUtc = finishedAt;
                _dirty = false;
            }
            await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // Progress is best-effort: never fail the job because the status row couldn't be written.
            Console.WriteLine($"[Jobs] Could not save progress for {_record.Id}: {ex.Message}");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (!_finished) await FailAsync("The job stopped unexpectedly.");
        try { await _flushLoop; } catch { /* ignore */ }
        _stop.Dispose();
        _flushLock.Dispose();
        _scope.Dispose();
    }
}

public static class BulkJobStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
}
