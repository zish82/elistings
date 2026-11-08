using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>
/// One listing published to one marketplace account (a listing can be on several accounts at once).
/// The listing's own EbayAccountId / EbayItemId / EbayOfferId still hold its first ("primary") publication.
/// </summary>
public class ListingPublication
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    /// <summary>"eBay" today; more marketplaces later.</summary>
    public string Marketplace { get; set; } = MarketplaceNames.Ebay;
    /// <summary>The marketplace account (for eBay: EbayTokenInfo.Id).</summary>
    public int AccountId { get; set; }
    /// <summary>See <see cref="PublicationStatuses"/>.</summary>
    public string Status { get; set; } = PublicationStatuses.Published;
    /// <summary>The marketplace's listing id (eBay item number).</summary>
    public string? ExternalListingId { get; set; }
    public string? OfferId { get; set; }
    public string? Sku { get; set; }
    /// <summary>Why the last publish to this account failed.</summary>
    public string? LastError { get; set; }
    public DateTime? PublishedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
