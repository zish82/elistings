using Hangfire;
using Shared;

namespace Server.Services;

public interface IFeedProductSyncService
{
    // Scheduled (Hangfire). Attributes must be on the interface: jobs are registered via the interface.
    [AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 600 })]
    Task RunCatalogueImportAsync();
    [AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 600 })]
    Task RunStockRefreshAsync();
    Task<FeedSyncResult> SyncFeedAsync(int feedId, string triggeredBy = "manual");
    Task<FeedSyncResult> ImportCatalogueAsync(string triggeredBy = "manual");
    Task<FeedSyncResult> RefreshStockAsync(string triggeredBy = "manual");
}
