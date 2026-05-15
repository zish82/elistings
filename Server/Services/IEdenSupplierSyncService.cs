using Shared;

namespace Server.Services;

public interface IEdenSupplierSyncService
{
    Task RunPublishedSyncAsync();
    Task<SupplierStatusDto> SyncListingAsync(int listingId, string triggeredBy = "manual");
    Task<SupplierStatusDto> CheckSourceAsync(string? sourceUrl, string? supplierSku, string triggeredBy = "manual");
    Task<SupplierStatusDto?> GetLatestSnapshotAsync(int listingId);
}
