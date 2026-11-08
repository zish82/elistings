using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>Reading where listings are published (for pages and reports).</summary>
public static class PublicationQueries
{
    /// <summary>Listing id → its publications (with account names), newest account last.</summary>
    public static async Task<Dictionary<int, List<ListingPublicationDto>>> ForListingsAsync(AppDbContext context, IReadOnlyCollection<int> listingIds)
    {
        if (listingIds.Count == 0) return new Dictionary<int, List<ListingPublicationDto>>();
        var rows = await context.ListingPublications.AsNoTracking()
            .Where(p => listingIds.Contains(p.ListingId))
            .OrderBy(p => p.Id)
            .Select(p => new
            {
                p.ListingId,
                Dto = new ListingPublicationDto
                {
                    Marketplace = p.Marketplace,
                    AccountId = p.AccountId,
                    AccountName = context.EbayTokens.Where(t => t.Id == p.AccountId).Select(t => t.Name).FirstOrDefault() ?? "(removed account)",
                    Status = p.Status,
                    ExternalListingId = p.ExternalListingId,
                    LastError = p.LastError,
                    PublishedAtUtc = p.PublishedAtUtc
                }
            })
            .ToListAsync();
        return rows.GroupBy(r => r.ListingId).ToDictionary(g => g.Key, g => g.Select(r => r.Dto).ToList());
    }

    /// <summary>
    /// One-time backfill: listings published before multi-account publishing get a publication row for their account.
    /// Safe to run on every start (only adds missing rows).
    /// </summary>
    public static int BackfillFromListings(AppDbContext context)
    {
        var existing = context.ListingPublications.Select(p => new { p.ListingId, p.AccountId }).ToList()
            .Select(p => (p.ListingId, p.AccountId)).ToHashSet();
        var published = context.Listings
            .Where(l => l.EbayAccountId != null && (l.Status == "Published" || (l.Status == "Deleted" && l.EbayItemId != null)))
            .Select(l => new { l.Id, AccountId = l.EbayAccountId!.Value, l.EbayItemId, l.EbayOfferId, l.Sku })
            .ToList();

        var added = 0;
        foreach (var l in published.Where(l => !existing.Contains((l.Id, l.AccountId))))
        {
            context.ListingPublications.Add(new ListingPublication
            {
                ListingId = l.Id,
                Marketplace = MarketplaceNames.Ebay,
                AccountId = l.AccountId,
                Status = PublicationStatuses.Published,
                ExternalListingId = l.EbayItemId,
                OfferId = l.EbayOfferId,
                Sku = l.Sku,
                UpdatedAtUtc = DateTime.UtcNow
            });
            added++;
        }
        return added;
    }
}
