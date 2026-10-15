using Shared;

namespace Server.Services;

public interface IFeedProductSyncService
{
    Task RunCatalogueImportAsync();
    Task RunStockRefreshAsync();
    Task<FeedSyncResult> SyncFeedAsync(int feedId, string triggeredBy = "manual");
    Task<FeedSyncResult> ImportCatalogueAsync(string triggeredBy = "manual");
    Task<FeedSyncResult> RefreshStockAsync(string triggeredBy = "manual");
}
