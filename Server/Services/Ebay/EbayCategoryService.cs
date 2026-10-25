using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Server.Data;

namespace Server.Services;

/// <summary>
/// The downloaded eBay UK category tree: lets fee estimates find a listing's top-level category (fee rates are set on
/// top-level categories and a few sub-categories, while listings use leaf categories).
/// </summary>
public class EbayCategoryService
{
    private const string CacheKey = "ebay-category-parents";
    private readonly AppDbContext _context;
    private readonly IMemoryCache _cache;

    public EbayCategoryService(AppDbContext context, IMemoryCache cache)
    {
        _context = context;
        _cache = cache;
    }

    /// <summary>Downloads the tree from eBay and replaces the stored copy. Returns the number of categories.</summary>
    public async Task<int> RefreshAsync(IEbayService ebay, CancellationToken ct = default)
    {
        var nodes = await ebay.GetCategoryTreeAsync();
        if (nodes.Count == 0) throw new InvalidOperationException("eBay returned an empty category tree.");

        var now = DateTime.UtcNow;
        await using (var tx = await _context.Database.BeginTransactionAsync(ct))
        {
            await _context.EbayCategories.ExecuteDeleteAsync(ct);
            foreach (var chunk in nodes.DistinctBy(n => n.CategoryId).Chunk(2000))
            {
                _context.EbayCategories.AddRange(chunk.Select(n => new EbayCategory
                {
                    CategoryId = n.CategoryId, ParentCategoryId = n.ParentCategoryId, Name = n.Name, Level = n.Level, UpdatedAtUtc = now
                }));
                await _context.SaveChangesAsync(ct);
                _context.ChangeTracker.Clear();
            }
            await tx.CommitAsync(ct);
        }
        _cache.Remove(CacheKey);
        return nodes.Count;
    }

    public async Task<(int Count, DateTime? UpdatedAtUtc)> GetStatusAsync(CancellationToken ct = default)
    {
        var count = await _context.EbayCategories.CountAsync(ct);
        var updated = count == 0 ? null : await _context.EbayCategories.MaxAsync(c => (DateTime?)c.UpdatedAtUtc, ct);
        return (count, updated);
    }

    /// <summary>
    /// The category and its parents, most specific first (e.g. Candles → Home Fragrances → Home Décor → Home, Furniture &amp; DIY).
    /// Just the category itself when the tree hasn't been downloaded (or doesn't know it). Empty for no category.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetChainAsync(string? categoryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(categoryId)) return Array.Empty<string>();
        var parents = await GetParentsAsync(ct);
        return Chain(categoryId.Trim(), parents);
    }

    public static IReadOnlyList<string> Chain(string categoryId, IReadOnlyDictionary<string, string?> parents)
    {
        var chain = new List<string> { categoryId };
        var seen = new HashSet<string> { categoryId };
        var current = categoryId;
        while (parents.TryGetValue(current, out var parent) && parent != null && seen.Add(parent))
        {
            chain.Add(parent);
            current = parent;
        }
        return chain;
    }

    /// <summary>Category → parent for the whole tree, cached (tens of thousands of entries, small).</summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetParentsAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyDictionary<string, string?>? cached) && cached != null) return cached;
        var parents = await _context.EbayCategories.AsNoTracking()
            .Select(c => new { c.CategoryId, c.ParentCategoryId })
            .ToDictionaryAsync(c => c.CategoryId, c => c.ParentCategoryId, ct);
        _cache.Set(CacheKey, (IReadOnlyDictionary<string, string?>)parents, TimeSpan.FromHours(6));
        return parents;
    }
}
