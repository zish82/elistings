using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>
/// Whose listings, marketplace accounts and sales the current user may see:
/// Admin – everyone; Manager – themselves and the users assigned to them (Users page); everyone else – themselves.
/// </summary>
public static class UserScope
{
    /// <summary>The visible user ids, or null for "everyone" (Admin). Empty when not signed in.</summary>
    public static async Task<List<int>?> VisibleUserIdsAsync(AppDbContext context, ICurrentUserService user)
    {
        var me = user.UserId;
        if (me == null) return new List<int>();
        if (IsRole(user, AuthRoles.Admin)) return null;
        if (IsRole(user, AuthRoles.Manager))
        {
            var managed = await context.Users.Where(u => u.ManagerUserId == me.Value).Select(u => u.Id).ToListAsync();
            managed.Add(me.Value);
            return managed;
        }
        return new List<int> { me.Value };
    }

    public static async Task<bool> CanSeeUserAsync(AppDbContext context, ICurrentUserService user, int ownerUserId)
    {
        var visible = await VisibleUserIdsAsync(context, user);
        return visible == null || visible.Contains(ownerUserId);
    }

    public static IQueryable<Listing> VisibleTo(this IQueryable<Listing> listings, List<int>? userIds)
        => userIds == null ? listings : listings.Where(l => userIds.Contains(l.OwnerUserId));

    public static IQueryable<EbayOrder> VisibleTo(this IQueryable<EbayOrder> orders, List<int>? userIds)
        => userIds == null ? orders : orders.Where(o => userIds.Contains(o.OwnerUserId));

    private static bool IsRole(ICurrentUserService user, string role) => string.Equals(user.Role, role, StringComparison.OrdinalIgnoreCase);
}
