using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>Tag name helpers: normalising (so "Pots, Tanks &amp; Trays" = "Pots Tanks and Trays") and guessing the tag type.</summary>
public static class TagText
{
    /// <summary>Supplier tags that are promotions or internal flags, not what the product is.</summary>
    private static readonly HashSet<string> PromoTags = new(StringComparer.Ordinal)
    {
        "sale", "special offers", "special offer", "offers", "clearance", "brand new", "new", "new arrivals", "best sellers",
        "bestsellers", "best seller", "reward points", "rewards", "b2b", "pos", "point of sale", "featured", "gift ideas"
    };

    public static string Normalize(string? name)
    {
        var text = (name ?? string.Empty).ToLowerInvariant().Replace("&", " and ").Replace("_", " ");
        text = Regex.Replace(text, @"[^\p{L}\p{Nd} ]", " ");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// promo for known promotion tags; brand when the tag is (the start of) a supplier vendor/brand name, e.g. "omega" for
    /// "Omega Lighting"; otherwise type. Only type tags are offered as filters.
    /// </summary>
    public static string Classify(string normalized, IReadOnlyCollection<string> normalizedBrands)
    {
        if (PromoTags.Contains(normalized)) return ProductTagTypes.Promo;
        if (normalizedBrands.Any(b => b == normalized || b.StartsWith(normalized + " ", StringComparison.Ordinal))) return ProductTagTypes.Brand;
        return ProductTagTypes.Type;
    }
}

/// <summary>Stores supplier tags and which products have them.</summary>
public class ProductTagService
{
    private readonly AppDbContext _context;

    public ProductTagService(AppDbContext context) => _context = context;

    /// <summary>
    /// Replaces the tags of the given products (other products keep theirs). New tag names are created and typed
    /// automatically; existing tags keep the type the user chose.
    /// </summary>
    public async Task SyncAsync(string supplier, IReadOnlyDictionary<int, List<string>> tagsByProductId, IEnumerable<string?> brands, CancellationToken ct = default)
    {
        if (tagsByProductId.Count == 0) return;
        var brandSet = brands.Select(TagText.Normalize).Where(b => b.Length > 0).Distinct().ToList();

        var tags = (await _context.ProductTags.Where(t => t.Supplier == supplier).ToListAsync(ct))
            .GroupBy(t => t.NormalizedName).ToDictionary(g => g.Key, g => g.First());

        // New tag names (first spelling seen wins as the display name).
        foreach (var name in tagsByProductId.Values.SelectMany(v => v))
        {
            var normalized = TagText.Normalize(name);
            if (normalized.Length == 0 || tags.ContainsKey(normalized)) continue;
            var tag = new ProductTag { Supplier = supplier, Name = name.Trim(), NormalizedName = normalized, Type = TagText.Classify(normalized, brandSet) };
            _context.ProductTags.Add(tag);
            tags[normalized] = tag;
        }
        await _context.SaveChangesAsync(ct);

        var productIds = tagsByProductId.Keys.ToList();
        var existingLinks = new List<SupplierProductTag>();
        foreach (var chunk in productIds.Chunk(500))
        {
            existingLinks.AddRange(await _context.SupplierProductTags.Where(l => chunk.Contains(l.SupplierProductId)).ToListAsync(ct));
        }
        var linksByProduct = existingLinks.ToLookup(l => l.SupplierProductId);

        foreach (var (productId, names) in tagsByProductId)
        {
            var wanted = names.Select(TagText.Normalize).Where(n => n.Length > 0 && tags.ContainsKey(n)).Select(n => tags[n].Id).ToHashSet();
            var current = linksByProduct[productId].ToList();
            _context.SupplierProductTags.RemoveRange(current.Where(l => !wanted.Contains(l.ProductTagId)));
            foreach (var tagId in wanted.Where(id => current.All(l => l.ProductTagId != id)))
            {
                _context.SupplierProductTags.Add(new SupplierProductTag { SupplierProductId = productId, ProductTagId = tagId });
            }
        }
        await _context.SaveChangesAsync(ct);
    }
}
