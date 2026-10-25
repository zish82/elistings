using System.Diagnostics;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Server.Data;
using Server.Services;
using Server.Services.Jobs;
using Xunit;

namespace Server.Tests;

/// <summary>Hangfire job enqueueing, progress persistence, queue routing and the per-host request throttle.</summary>
public class BackgroundJobTests
{
    private static (BulkJobService Service, AppDbContext Ctx, List<Job> Enqueued) JobService()
    {
        var ctx = TestHelpers.NewContext();
        var enqueued = new List<Job>();
        var client = new Mock<IBackgroundJobClient>();
        client.Setup(c => c.Create(It.IsAny<Job>(), It.IsAny<IState>()))
              .Callback<Job, IState>((job, _) => enqueued.Add(job))
              .Returns(() => $"hf-{enqueued.Count}");
        return (new BulkJobService(ctx, client.Object), ctx, enqueued);
    }

    [Fact]
    public async Task Enqueue_CreatesQueuedRecord_AndHangfireJob()
    {
        var (service, ctx, enqueued) = JobService();

        var jobId = await service.EnqueueBulkPublishAsync(7, new List<int> { 1, 2 });

        var record = Assert.Single(ctx.BulkJobs);
        Assert.Equal(jobId, record.Id);
        Assert.Equal("publish", record.Type);
        Assert.Equal(7, record.OwnerUserId);
        Assert.Equal(BulkJobStatus.Queued, record.Status);
        Assert.Equal("hf-1", record.HangfireJobId);
        var job = Assert.Single(enqueued);
        Assert.Equal(nameof(BackgroundJobs.BulkPublishAsync), job.Method.Name);
        Assert.Equal(jobId, job.Args[0]);
    }

    [Theory]
    [InlineData("https://edenhorticulture.co.uk/collections/lighting-kits", nameof(BackgroundJobs.BulkExtractEdenAsync))]
    [InlineData("https://www.safelincs.co.uk/heat-alarms/", nameof(BackgroundJobs.BulkExtractAsync))]
    public async Task BulkExtract_EdenUrls_UseTheEdenJob(string url, string expectedMethod)
    {
        var (service, _, enqueued) = JobService();

        await service.EnqueueBulkExtractAsync(1, url);

        Assert.Equal(expectedMethod, Assert.Single(enqueued).Method.Name);
    }

    [Fact]
    public async Task SupplierImport_Eden_UsesEdenJob_AndIsTaggedWithSupplier()
    {
        var (service, ctx, enqueued) = JobService();

        await service.EnqueueSupplierImportAsync(1, EdenCatalogImportService.Code);

        Assert.Equal(nameof(BackgroundJobs.ImportEdenCatalogueAsync), Assert.Single(enqueued).Method.Name);
        Assert.Equal(EdenCatalogImportService.Code, ctx.BulkJobs.Single().Tag);
    }

    [Theory]
    [InlineData(nameof(BackgroundJobs.BulkExtractEdenAsync), BackgroundJobs.EdenQueue)]
    [InlineData(nameof(BackgroundJobs.ImportEdenCatalogueAsync), BackgroundJobs.EdenQueue)]
    [InlineData(nameof(BackgroundJobs.BulkPublishAsync), BackgroundJobs.DefaultQueue)]
    [InlineData(nameof(BackgroundJobs.BulkExtractAsync), BackgroundJobs.DefaultQueue)]
    public void JobMethods_DeclareQueue_AndNoAutomaticRetry(string method, string queue)
    {
        var info = typeof(BackgroundJobs).GetMethod(method)!;

        Assert.Equal(queue, info.GetCustomAttributes(typeof(QueueAttribute), false).Cast<QueueAttribute>().Single().Queue);
        Assert.Equal(0, info.GetCustomAttributes(typeof(AutomaticRetryAttribute), false).Cast<AutomaticRetryAttribute>().Single().Attempts);
    }

    [Fact]
    public void ScheduledEdenJobs_RunOnEdenQueue_WithOneDelayedRetry()
    {
        foreach (var method in new[]
                 {
                     typeof(EdenCatalogImportService).GetMethod(nameof(EdenCatalogImportService.RunScheduledAsync))!,
                     typeof(IEdenSupplierSyncService).GetMethod(nameof(IEdenSupplierSyncService.RunPublishedSyncAsync))!
                 })
        {
            Assert.Equal(BackgroundJobs.EdenQueue, method.GetCustomAttributes(typeof(QueueAttribute), false).Cast<QueueAttribute>().Single().Queue);
            var retry = method.GetCustomAttributes(typeof(AutomaticRetryAttribute), false).Cast<AutomaticRetryAttribute>().Single();
            Assert.Equal(1, retry.Attempts);
            Assert.Equal(new[] { 3600 }, retry.DelaysInSeconds);
        }
    }

    [Fact]
    public async Task FindLatest_And_IsActive()
    {
        var (service, ctx, _) = JobService();
        ctx.BulkJobs.AddRange(
            new BulkJobRecord { Id = "old", Type = "import", Tag = "EdenHorticulture", Status = BulkJobStatus.Completed, CreatedAtUtc = DateTime.UtcNow.AddHours(-2) },
            new BulkJobRecord { Id = "new", Type = "import", Tag = "EdenHorticulture", Status = BulkJobStatus.Running, CreatedAtUtc = DateTime.UtcNow },
            new BulkJobRecord { Id = "other", Type = "import", Tag = "DropShipMall", Status = BulkJobStatus.Queued, CreatedAtUtc = DateTime.UtcNow.AddHours(1) });
        ctx.SaveChanges();

        var latest = await service.FindLatestAsync("import", "EdenHorticulture");

        Assert.Equal("new", latest!.Id);
        Assert.True(BulkJobService.IsActive(latest));
        Assert.False(BulkJobService.IsActive(ctx.BulkJobs.Single(j => j.Id == "old")));
        Assert.False(BulkJobService.IsActive(null));
    }

    [Fact]
    public async Task JobProgress_PersistsCountsErrorsAndFinalStatus()
    {
        var dbName = $"progress-{Guid.NewGuid():N}";
        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BulkJobs.Add(new BulkJobRecord { Id = "job1", Type = "publish", Status = BulkJobStatus.Queued });
            db.SaveChanges();
        }

        await using (var progress = await JobProgress.BeginAsync(services.GetRequiredService<IServiceScopeFactory>(), "job1"))
        {
            progress.Total = 3;
            progress.ItemSucceeded();
            progress.ItemSkipped();
            progress.ItemFailed("#3: no images");
            await progress.CompleteAsync("Published 1, failed 1.");
        }

        using var check = services.CreateScope();
        var record = check.ServiceProvider.GetRequiredService<AppDbContext>().BulkJobs.Single();
        var dto = BulkJobService.ToDto(record);
        Assert.Equal(BulkJobStatus.Completed, dto.Status);
        Assert.Equal((3, 3, 1, 1, 1), (dto.Total, dto.Processed, dto.Succeeded, dto.Skipped, dto.Failed));
        Assert.Equal("#3: no images", Assert.Single(dto.Errors));
        Assert.Equal("Published 1, failed 1.", dto.Message);
        Assert.NotNull(dto.FinishedAtUtc);
        Assert.True(dto.IsFinished);
    }

    [Fact]
    public async Task JobProgress_DisposedWithoutFinishing_MarksJobFailed()
    {
        var dbName = $"progress-{Guid.NewGuid():N}";
        var services = new ServiceCollection().AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName)).BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.BulkJobs.Add(new BulkJobRecord { Id = "job2", Type = "extract" });
            db.SaveChanges();
        }

        await using (await JobProgress.BeginAsync(services.GetRequiredService<IServiceScopeFactory>(), "job2")) { }

        using var check = services.CreateScope();
        Assert.Equal(BulkJobStatus.Failed, check.ServiceProvider.GetRequiredService<AppDbContext>().BulkJobs.Single().Status);
    }

    [Fact]
    public void Throttle_MatchesConfiguredHostsAndSubdomainsOnly()
    {
        var throttle = new HostRequestThrottle(new Dictionary<string, TimeSpan> { ["edenhorticulture.co.uk"] = TimeSpan.FromSeconds(2) });

        Assert.Equal("edenhorticulture.co.uk", throttle.MatchHost(new Uri("https://edenhorticulture.co.uk/products.json")));
        Assert.Equal("edenhorticulture.co.uk", throttle.MatchHost(new Uri("https://www.edenhorticulture.co.uk/x")));
        Assert.Null(throttle.MatchHost(new Uri("https://notedenhorticulture.co.uk/")));
        Assert.Null(throttle.MatchHost(new Uri("https://api.ebay.com/sell")));
        Assert.Null(throttle.MatchHost(null));
    }

    [Fact]
    public async Task Throttle_SpacesConsecutiveRequestsToTheSameHost()
    {
        var gap = TimeSpan.FromMilliseconds(300);
        var throttle = new HostRequestThrottle(new Dictionary<string, TimeSpan> { ["example.com"] = gap });

        var watch = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => throttle.WaitTurnAsync("example.com", CancellationToken.None)));
        watch.Stop();

        // First request goes straight away, the next two wait one gap each.
        Assert.True(watch.Elapsed >= gap * 2 - TimeSpan.FromMilliseconds(30), $"took {watch.Elapsed}");
    }

    [Fact]
    public async Task ThrottlingHandler_DelaysOnlyThrottledHosts()
    {
        var throttle = new HostRequestThrottle(new Dictionary<string, TimeSpan> { ["slow.test"] = TimeSpan.FromMilliseconds(400) });
        using var client = new HttpClient(new HostThrottlingHandler(throttle) { InnerHandler = new OkHandler() });

        await client.GetAsync("https://slow.test/a"); // first request: no wait
        var fast = Stopwatch.StartNew();
        await client.GetAsync("https://fast.test/b");
        fast.Stop();
        var slow = Stopwatch.StartNew();
        await client.GetAsync("https://slow.test/c");
        slow.Stop();

        Assert.True(fast.ElapsedMilliseconds < 200, $"unthrottled host waited {fast.ElapsedMilliseconds}ms");
        Assert.True(slow.ElapsedMilliseconds >= 150, $"throttled host only waited {slow.ElapsedMilliseconds}ms");
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
