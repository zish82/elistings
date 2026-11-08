using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>Which marketplace accounts a user may post with: the ones they connected plus the ones an Admin shared with them.</summary>
public static class AccountAccess
{
    public static async Task<List<int>> AccountIdsForUserAsync(AppDbContext context, int userId)
    {
        var owned = await context.EbayTokens.Where(t => t.UserId == userId).Select(t => t.Id).ToListAsync();
        var shared = await context.AccountUserAccess
            .Where(a => a.UserId == userId && a.Marketplace == MarketplaceNames.Ebay)
            .Select(a => a.AccountId)
            .ToListAsync();
        return owned.Concat(shared).Distinct().ToList();
    }

    public static async Task<bool> CanUseAsync(AppDbContext context, int userId, int accountId)
        => await context.EbayTokens.AnyAsync(t => t.Id == accountId && t.UserId == userId)
           || await context.AccountUserAccess.AnyAsync(a => a.AccountId == accountId && a.UserId == userId && a.Marketplace == MarketplaceNames.Ebay);

    /// <summary>Synchronous variant for code that is already synchronous.</summary>
    public static bool CanUse(AppDbContext context, int userId, int accountId)
        => context.EbayTokens.Any(t => t.Id == accountId && t.UserId == userId)
           || context.AccountUserAccess.Any(a => a.AccountId == accountId && a.UserId == userId && a.Marketplace == MarketplaceNames.Ebay);

    /// <summary>The user's default account: their own default, else their first own account, else the first shared one.</summary>
    public static int? DefaultAccountId(AppDbContext context, int userId)
        => context.EbayTokens.Where(t => t.UserId == userId).OrderByDescending(t => t.IsDefault).ThenBy(t => t.Id).Select(t => (int?)t.Id).FirstOrDefault()
           ?? context.AccountUserAccess.Where(a => a.UserId == userId && a.Marketplace == MarketplaceNames.Ebay).OrderBy(a => a.AccountId).Select(a => (int?)a.AccountId).FirstOrDefault();

    /// <summary>Replaces the users an account is shared with (the account's own user is never stored here).</summary>
    public static async Task SetSharedUsersAsync(AppDbContext context, int accountId, IEnumerable<int> userIds)
    {
        var ownerId = await context.EbayTokens.Where(t => t.Id == accountId).Select(t => (int?)t.UserId).FirstOrDefaultAsync()
                      ?? throw new InvalidOperationException("Account not found.");
        var wanted = userIds.Where(id => id != ownerId).Distinct().ToList();
        var validUsers = await context.Users.Where(u => wanted.Contains(u.Id)).Select(u => u.Id).ToListAsync();

        var current = await context.AccountUserAccess.Where(a => a.AccountId == accountId && a.Marketplace == MarketplaceNames.Ebay).ToListAsync();
        context.AccountUserAccess.RemoveRange(current.Where(a => !validUsers.Contains(a.UserId)));
        foreach (var id in validUsers.Where(id => current.All(a => a.UserId != id)))
        {
            context.AccountUserAccess.Add(new AccountUserAccess { AccountId = accountId, UserId = id, Marketplace = MarketplaceNames.Ebay });
        }
        await context.SaveChangesAsync();
    }
}
