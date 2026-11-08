using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class EbayTokenInfo
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public string Name { get; set; } = "eBay Account";
    public bool IsDefault { get; set; }
    public string AccessToken { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiryTime { get; set; }
    public DateTime RefreshTokenExpiryTime { get; set; }
    /// <summary>OAuth scopes granted when connected (null = the original listing-only set).</summary>
    public string? Scopes { get; set; }
    /// <summary>Orders have been imported from this date (sales report history); null = never.</summary>
    public DateTime? OrdersSyncedFromUtc { get; set; }
    public DateTime? OrdersLastSyncedAtUtc { get; set; }
    /// <summary>The eBay shop's user ID (from GetUser), so the same user can't connect the same shop twice. Null = unknown.</summary>
    public string? EbayUserId { get; set; }
}
