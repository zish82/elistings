using Shared;

namespace Server.Services;

public interface IEdenSupplierSyncService
{
    Task RunPublishedSyncAsync();
    Task<SupplierStatusDto> SyncListingAsync(int listingId, string triggeredBy = "manual");
    Task<SupplierStatusDto> CheckSourceAsync(string? sourceUrl, string? supplierSku, string triggeredBy = "manual");
    Task<SupplierStatusDto?> GetLatestSnapshotAsync(int listingId);
    /// <summary>Logs in to the Eden trade portal once and returns a session for further logged-in requests.</summary>
    Task<IEdenTradeSession> OpenTradeSessionAsync();
}

/// <summary>A logged-in Eden Horticulture session (cookies kept between requests).</summary>
public interface IEdenTradeSession
{
    /// <summary>GETs a page or JSON endpoint as the logged-in trade customer (re-logs in once on 401/403).</summary>
    Task<string> GetStringAsync(Uri url, CancellationToken ct = default);
}
