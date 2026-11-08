using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>
/// Accounts overview: the users the current user may see (Admin: all; Manager: self + assigned users; others: self),
/// each with the marketplace accounts they post with and how many of their own listings are on each.
/// </summary>
public static class AccountsOverviewService
{
    public static async Task<List<UserAccountsOverviewDto>> BuildAsync(AppDbContext context, ICurrentUserService currentUser)
    {
        var me = currentUser.UserId;
        if (me == null) return new List<UserAccountsOverviewDto>();
        var visible = await UserScope.VisibleUserIdsAsync(context, currentUser);
        var isAdmin = currentUser.CanManageUsers;

        var users = await context.Users.AsNoTracking()
            .Where(u => visible == null || visible.Contains(u.Id))
            .Select(u => new { u.Id, u.Email, u.Role, u.ManagerUserId })
            .ToListAsync();
        var userIds = users.Select(u => u.Id).ToList();
        var emails = await context.Users.AsNoTracking().ToDictionaryAsync(u => u.Id, u => u.Email);

        var tokens = await context.EbayTokens.AsNoTracking()
            .Select(t => new { t.Id, t.UserId, t.Name, t.IsDefault, t.RefreshToken, t.ExpiryTime, t.RefreshTokenExpiryTime })
            .ToListAsync();
        var shares = await context.AccountUserAccess.AsNoTracking().Where(a => a.Marketplace == MarketplaceNames.Ebay).ToListAsync();

        // This user's listings per account (only their own – users of a shared account don't see each other's listings).
        var counts = await (
                from p in context.ListingPublications.AsNoTracking()
                join l in context.Listings.AsNoTracking() on p.ListingId equals l.Id
                where userIds.Contains(l.OwnerUserId) && l.Status != "Deleted"
                group p by new { l.OwnerUserId, p.AccountId, p.Marketplace, p.Status } into g
                select new { g.Key.OwnerUserId, g.Key.AccountId, g.Key.Marketplace, g.Key.Status, Count = g.Count() })
            .ToListAsync();
        var published = await context.ListingPublications.AsNoTracking()
            .Where(p => p.Status == PublicationStatuses.Published)
            .Select(p => p.ListingId)
            .Distinct()
            .ToListAsync();
        var drafts = await context.Listings.AsNoTracking()
            .Where(l => userIds.Contains(l.OwnerUserId) && l.Status != "Deleted" && !published.Contains(l.Id))
            .GroupBy(l => l.OwnerUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.UserId, x => x.Count);

        var result = new List<UserAccountsOverviewDto>();
        foreach (var u in users.OrderByDescending(u => u.Id == me).ThenBy(u => u.Email))
        {
            var sharedWithUser = shares.Where(s => s.UserId == u.Id).Select(s => s.AccountId).ToHashSet();
            var accounts = tokens.Where(t => t.UserId == u.Id || sharedWithUser.Contains(t.Id))
                .OrderByDescending(t => t.UserId == u.Id).ThenByDescending(t => t.IsDefault).ThenBy(t => t.Name)
                .Select(t => new AccountOverviewDto
                {
                    Marketplace = MarketplaceNames.Ebay,
                    AccountId = t.Id,
                    Name = t.Name,
                    IsDefault = t.IsDefault && t.UserId == u.Id,
                    IsConnected = t.RefreshToken == "MANUAL" ? t.ExpiryTime > DateTime.UtcNow : t.RefreshTokenExpiryTime > DateTime.UtcNow,
                    IsShared = t.UserId != u.Id,
                    OwnerEmail = emails.GetValueOrDefault(t.UserId),
                    SharedWithUserIds = isAdmin && t.UserId == u.Id ? shares.Where(s => s.AccountId == t.Id).Select(s => s.UserId).ToList() : new List<int>(),
                    PublishedCount = counts.Where(c => c.OwnerUserId == u.Id && c.AccountId == t.Id && c.Status == PublicationStatuses.Published).Sum(c => c.Count),
                    FailedCount = counts.Where(c => c.OwnerUserId == u.Id && c.AccountId == t.Id && c.Status == PublicationStatuses.Failed).Sum(c => c.Count)
                })
                .ToList();

            result.Add(new UserAccountsOverviewDto
            {
                UserId = u.Id,
                Email = u.Email,
                Role = u.Role,
                IsMe = u.Id == me,
                ManagerUserId = u.ManagerUserId,
                ManagerEmail = u.ManagerUserId.HasValue ? emails.GetValueOrDefault(u.ManagerUserId.Value) : null,
                Accounts = accounts,
                DraftCount = drafts.GetValueOrDefault(u.Id)
            });
        }
        return result;
    }
}
