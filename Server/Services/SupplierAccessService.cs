using Microsoft.EntityFrameworkCore;
using Server.Data;

namespace Server.Services;

/// <summary>
/// Which suppliers the current user may see and work with (products, feeds, imports, supplier settings).
/// Admins see every supplier; Managers and Listers only those ticked for them on /users.
/// </summary>
public class SupplierAccessService
{
    private readonly AppDbContext _context;
    private readonly ICurrentUserService _user;

    public SupplierAccessService(AppDbContext context, ICurrentUserService user)
    {
        _context = context;
        _user = user;
    }

    public bool SeesAllSuppliers => _user.CanManageUsers;

    /// <summary>Supplier codes the user may use; null = all (Admin).</summary>
    public async Task<HashSet<string>?> AllowedCodesAsync(CancellationToken ct = default)
    {
        if (SeesAllSuppliers) return null;
        var userId = _user.UserId ?? -1;
        return (await _context.UserSupplierAccess.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.SupplierCode).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public async Task<bool> IsAllowedAsync(string? supplierCode, CancellationToken ct = default)
    {
        if (SeesAllSuppliers) return true;
        if (string.IsNullOrWhiteSpace(supplierCode)) return true; // not a supplier (e.g. a manual listing)
        return (await AllowedCodesAsync(ct))!.Contains(supplierCode);
    }

    /// <summary>Filters a query of supplier products to the user's suppliers (as a sub-query, so it stays in SQL).</summary>
    public IQueryable<SupplierProduct> Filter(IQueryable<SupplierProduct> products)
    {
        if (SeesAllSuppliers) return products;
        var userId = _user.UserId ?? -1;
        // Case-insensitive (PostgreSQL compares text case-sensitively; supplier codes are matched loosely elsewhere).
        var allowed = _context.UserSupplierAccess.Where(a => a.UserId == userId).Select(a => a.SupplierCode.ToLower());
        return products.Where(p => allowed.Contains(p.Supplier.ToLower()));
    }

    /// <summary>The supplier a URL belongs to (by website host), or null.</summary>
    public async Task<string?> SupplierCodeForUrlAsync(string? url, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        var host = uri.Host.ToLowerInvariant();
        var suppliers = await _context.Suppliers.AsNoTracking().Where(s => s.WebsiteHost != null).Select(s => new { s.Code, s.WebsiteHost }).ToListAsync(ct);
        return suppliers.FirstOrDefault(s => host == s.WebsiteHost!.ToLowerInvariant() || host.EndsWith("." + s.WebsiteHost!.ToLowerInvariant()))?.Code;
    }

    /// <summary>Replaces a user's supplier access.</summary>
    public async Task SetForUserAsync(int userId, IEnumerable<string> supplierCodes, CancellationToken ct = default)
    {
        var known = await _context.Suppliers.Select(s => s.Code).ToListAsync(ct);
        var wanted = supplierCodes.Where(c => known.Contains(c, StringComparer.OrdinalIgnoreCase))
            .Select(c => known.First(k => k.Equals(c, StringComparison.OrdinalIgnoreCase)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = await _context.UserSupplierAccess.Where(a => a.UserId == userId).ToListAsync(ct);
        _context.UserSupplierAccess.RemoveRange(current.Where(a => !wanted.Contains(a.SupplierCode)));
        foreach (var code in wanted.Where(c => current.All(a => !a.SupplierCode.Equals(c, StringComparison.OrdinalIgnoreCase))))
            _context.UserSupplierAccess.Add(new UserSupplierAccess { UserId = userId, SupplierCode = code });
        var user = await _context.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user != null) user.SupplierAccessConfigured = true;
        await _context.SaveChangesAsync(ct);
    }
}
