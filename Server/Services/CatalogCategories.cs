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
    /// "Omega Lighting"; otherwise type. The user can change it on My Categories.
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

/// <summary>
/// "My categories": the tree, supplier → my category mappings (and working out each product's category from them),
/// building the tree from a supplier's categories, and my category → sales channel categories.
/// </summary>
public class CatalogCategoryService
{
    public const string PathSeparator = " › ";
    private readonly AppDbContext _context;

    public CatalogCategoryService(AppDbContext context) => _context = context;

    /// <summary>The supplier category a product is mapped by: its full path when known, else its category name.</summary>
    public static string? CategoryKey(string? categoryPath, string? categoryName)
    {
        var key = string.IsNullOrWhiteSpace(categoryPath) ? categoryName : categoryPath;
        return string.IsNullOrWhiteSpace(key) ? null : key.Trim();
    }

    // ------------------------------------------------------------------ tree

    public sealed record TreeNode(CatalogCategory Category, string Path, int Depth);

    /// <summary>Path and depth of every category (cycle-safe).</summary>
    public static Dictionary<int, TreeNode> BuildTree(IReadOnlyCollection<CatalogCategory> categories)
    {
        var byId = categories.ToDictionary(c => c.Id);
        var result = new Dictionary<int, TreeNode>();
        foreach (var category in categories)
        {
            var names = new List<string>();
            var seen = new HashSet<int>();
            for (var c = category; c != null && seen.Add(c.Id); c = c.ParentId.HasValue && byId.TryGetValue(c.ParentId.Value, out var parent) ? parent : null)
            {
                names.Insert(0, c.Name);
            }
            result[category.Id] = new TreeNode(category, string.Join(PathSeparator, names), names.Count - 1);
        }
        return result;
    }

    public async Task<List<CatalogCategoryDto>> GetAllAsync(CancellationToken ct = default)
    {
        var categories = await _context.CatalogCategories.AsNoTracking().ToListAsync(ct);
        var tree = BuildTree(categories);
        var counts = await _context.SupplierProducts
            .Where(p => p.CatalogCategoryId != null && p.IsActive && p.RemovedFromSupplierAtUtc == null)
            .GroupBy(p => p.CatalogCategoryId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var ebay = await _context.ChannelCategoryMappings.AsNoTracking().Where(m => m.Channel == SalesChannels.Ebay)
            .ToDictionaryAsync(m => m.CatalogCategoryId, ct);

        return DepthFirst(categories).Select(c => tree[c.Id])
            .Select(n => new CatalogCategoryDto
            {
                Id = n.Category.Id,
                Name = n.Category.Name,
                ParentId = n.Category.ParentId,
                SortOrder = n.Category.SortOrder,
                Path = n.Path,
                Depth = n.Depth,
                ProductCount = counts.GetValueOrDefault(n.Category.Id),
                EbayCategoryId = ebay.TryGetValue(n.Category.Id, out var m) ? m.ExternalId : null,
                EbayCategoryName = ebay.TryGetValue(n.Category.Id, out var m2) ? m2.ExternalName : null
            })
            .ToList();
    }

    /// <summary>Tree order: each category followed by its sub-categories, siblings by SortOrder then name.</summary>
    public static List<CatalogCategory> DepthFirst(IReadOnlyCollection<CatalogCategory> categories)
    {
        var ids = categories.Select(c => c.Id).ToHashSet();
        var children = categories
            .GroupBy(c => c.ParentId.HasValue && ids.Contains(c.ParentId.Value) && c.ParentId != c.Id ? c.ParentId : null)
            .ToDictionary(g => g.Key ?? 0, g => g.OrderBy(c => c.SortOrder).ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList());
        var result = new List<CatalogCategory>();
        var visited = new HashSet<int>();
        void Visit(int parentKey)
        {
            if (!children.TryGetValue(parentKey, out var list)) return;
            foreach (var c in list)
            {
                if (!visited.Add(c.Id)) continue;
                result.Add(c);
                Visit(c.Id);
            }
        }
        Visit(0);
        // Categories caught in a parent loop are never reached from the top: list them at the end.
        result.AddRange(categories.Where(c => !visited.Contains(c.Id)).OrderBy(c => c.Name));
        return result;
    }

    /// <summary>Ids of a category and everything below it.</summary>
    public static HashSet<int> WithDescendants(IReadOnlyCollection<CatalogCategory> categories, int id)
    {
        var result = new HashSet<int> { id };
        bool added;
        do
        {
            added = false;
            foreach (var c in categories)
            {
                if (c.ParentId.HasValue && result.Contains(c.ParentId.Value) && result.Add(c.Id)) added = true;
            }
        } while (added);
        return result;
    }

    public async Task<HashSet<int>> WithDescendantsAsync(int id, CancellationToken ct = default)
        => WithDescendants(await _context.CatalogCategories.AsNoTracking().ToListAsync(ct), id);

    /// <summary>Checks a parent change wouldn't create a loop (a category under itself or its own child).</summary>
    public async Task<string?> ValidateParentAsync(int? categoryId, int? parentId, CancellationToken ct = default)
    {
        if (!parentId.HasValue) return null;
        if (!await _context.CatalogCategories.AnyAsync(c => c.Id == parentId.Value, ct)) return "The parent category doesn't exist.";
        if (categoryId.HasValue && (await WithDescendantsAsync(categoryId.Value, ct)).Contains(parentId.Value))
            return "A category can't be moved under itself or one of its sub-categories.";
        return null;
    }

    /// <summary>Deletes a category: its sub-categories move up to its parent; mappings to it are removed and products re-sorted.</summary>
    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        var category = await _context.CatalogCategories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category == null) return;

        var children = await _context.CatalogCategories.Where(c => c.ParentId == id).ToListAsync(ct);
        foreach (var child in children) child.ParentId = category.ParentId;
        var mappings = await _context.SupplierCategoryMappings.Where(m => m.CatalogCategoryId == id).ToListAsync(ct);
        // Moved sub-categories change depth, which can change any supplier's result.
        var suppliers = children.Count > 0
            ? await _context.SupplierCategoryMappings.Select(m => m.Supplier).Distinct().ToListAsync(ct)
            : mappings.Select(m => m.Supplier).Distinct().ToList();
        _context.SupplierCategoryMappings.RemoveRange(mappings);
        _context.ChannelCategoryMappings.RemoveRange(await _context.ChannelCategoryMappings.Where(m => m.CatalogCategoryId == id).ToListAsync(ct));
        _context.CatalogCategories.Remove(category);
        foreach (var listing in await _context.Listings.Where(l => l.CatalogCategoryId == id).ToListAsync(ct))
        {
            listing.CatalogCategoryId = null;
            listing.CatalogCategoryIsManual = false; // re-sorted from its product below
        }
        foreach (var product in await _context.SupplierProducts.Where(p => p.CatalogCategoryId == id).ToListAsync(ct)) product.CatalogCategoryId = null;
        await _context.SaveChangesAsync(ct);

        foreach (var supplier in suppliers) await RecomputeAsync(supplier, ct);
    }

    // ------------------------------------------------------------------ channel categories

    /// <summary>The channel category for a catalog category, inherited from the nearest parent that has one.</summary>
    public async Task<ChannelCategoryMapping?> FindChannelCategoryAsync(int? catalogCategoryId, string channel, CancellationToken ct = default)
    {
        if (!catalogCategoryId.HasValue) return null;
        var mappings = await _context.ChannelCategoryMappings.AsNoTracking().Where(m => m.Channel == channel).ToDictionaryAsync(m => m.CatalogCategoryId, ct);
        if (mappings.Count == 0) return null;
        var parents = await _context.CatalogCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);

        var seen = new HashSet<int>();
        for (int? id = catalogCategoryId; id.HasValue && seen.Add(id.Value); id = parents.GetValueOrDefault(id.Value))
        {
            if (mappings.TryGetValue(id.Value, out var mapping)) return mapping;
        }
        return null;
    }

    // ------------------------------------------------------------------ supplier → my category

    /// <summary>
    /// Picks a product's category from the mappings of its supplier category and type tags: the most specific
    /// (deepest) mapped category wins. When the supplier category is mapped, a tag can only refine it (its category
    /// must be inside that category's branch), so a tag used across categories can't pull products into another branch.
    /// </summary>
    public static int? Resolve(string? categoryKey, IEnumerable<int> tagIds,
        IReadOnlyDictionary<string, int> categoryMappings, IReadOnlyDictionary<int, int> tagMappings,
        IReadOnlyDictionary<int, int> depths, IReadOnlyDictionary<int, int?> parents)
    {
        int? anchor = null;
        int? best = null;
        var bestDepth = -1;
        if (categoryKey != null && categoryMappings.TryGetValue(categoryKey, out var byCategory) && depths.TryGetValue(byCategory, out var d0))
        {
            anchor = best = byCategory;
            bestDepth = d0;
        }
        foreach (var tagId in tagIds)
        {
            if (!tagMappings.TryGetValue(tagId, out var byTag) || !depths.TryGetValue(byTag, out var d) || d <= bestDepth) continue;
            if (anchor.HasValue && !IsInBranch(byTag, anchor.Value, parents)) continue;
            best = byTag;
            bestDepth = d;
        }
        return best;
    }

    /// <summary>True when <paramref name="id"/> is <paramref name="branch"/> or below it.</summary>
    public static bool IsInBranch(int id, int branch, IReadOnlyDictionary<int, int?> parents)
    {
        var seen = new HashSet<int>();
        for (int? current = id; current.HasValue && seen.Add(current.Value); current = parents.GetValueOrDefault(current.Value))
        {
            if (current.Value == branch) return true;
        }
        return false;
    }

    /// <summary>Re-sorts every product of a supplier into my categories, and their listings (unless chosen by hand).</summary>
    public async Task<int> RecomputeAsync(string supplier, CancellationToken ct = default)
    {
        var allCategories = await _context.CatalogCategories.AsNoTracking().ToListAsync(ct);
        var tree = BuildTree(allCategories);
        var depths = tree.ToDictionary(t => t.Key, t => t.Value.Depth);
        var parents = allCategories.ToDictionary(c => c.Id, c => c.ParentId);
        var mappings = await _context.SupplierCategoryMappings.AsNoTracking().Where(m => m.Supplier == supplier).ToListAsync(ct);
        var tags = await _context.ProductTags.AsNoTracking().Where(t => t.Supplier == supplier).ToListAsync(ct);

        var categoryMappings = mappings.Where(m => m.SourceType == CategorySourceTypes.Category)
            .GroupBy(m => m.SourceValue, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().CatalogCategoryId, StringComparer.OrdinalIgnoreCase);
        var tagByNormalized = tags.Where(t => t.Type == ProductTagTypes.Type).ToDictionary(t => t.NormalizedName, t => t.Id);
        var tagMappings = mappings.Where(m => m.SourceType == CategorySourceTypes.Tag && tagByNormalized.ContainsKey(m.SourceValue))
            .GroupBy(m => tagByNormalized[m.SourceValue])
            .ToDictionary(g => g.Key, g => g.First().CatalogCategoryId);

        var products = await _context.SupplierProducts.AsNoTracking()
            .Where(p => p.Supplier == supplier)
            .Select(p => new { p.Id, p.CategoryPath, p.CategoryName, p.CatalogCategoryId })
            .ToListAsync(ct);
        var links = tagMappings.Count == 0
            ? Array.Empty<SupplierProductTag>().ToLookup(l => l.SupplierProductId, l => l.ProductTagId)
            : (await (from l in _context.SupplierProductTags
                      join t in _context.ProductTags on l.ProductTagId equals t.Id
                      where t.Supplier == supplier
                      select l).AsNoTracking().ToListAsync(ct)).ToLookup(l => l.SupplierProductId, l => l.ProductTagId);

        var newCategory = products.ToDictionary(p => p.Id,
            p => Resolve(CategoryKey(p.CategoryPath, p.CategoryName), links[p.Id], categoryMappings, tagMappings, depths, parents));

        // Only rows that change are written (bulk update per category).
        foreach (var group in products.Where(p => p.CatalogCategoryId != newCategory[p.Id]).GroupBy(p => newCategory[p.Id]))
        {
            foreach (var chunk in group.Select(p => p.Id).Chunk(500))
            {
                await _context.SupplierProducts.Where(p => chunk.Contains(p.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.CatalogCategoryId, group.Key), ct);
            }
        }

        // Listings follow their product, unless the user picked the category.
        foreach (var group in newCategory.GroupBy(kv => kv.Value))
        {
            foreach (var chunk in group.Select(kv => kv.Key).Chunk(500))
            {
                await _context.Listings
                    .Where(l => l.SupplierProductId != null && chunk.Contains(l.SupplierProductId.Value) && !l.CatalogCategoryIsManual && l.CatalogCategoryId != group.Key)
                    .ExecuteUpdateAsync(s => s.SetProperty(l => l.CatalogCategoryId, group.Key), ct);
            }
        }

        return newCategory.Count(kv => kv.Value.HasValue);
    }

    /// <summary>Mapping screen rows: the supplier's categories and tags, with product counts and current mapping.</summary>
    public async Task<List<SupplierCategorySourceDto>> GetSourcesAsync(string supplier, CancellationToken ct = default)
    {
        var mappings = await _context.SupplierCategoryMappings.AsNoTracking().Where(m => m.Supplier == supplier).ToListAsync(ct);
        string MapKey(string type, string value) => type + "|" + value.ToLowerInvariant();
        var mapped = mappings.GroupBy(m => MapKey(m.SourceType, m.SourceValue)).ToDictionary(g => g.Key, g => g.First().CatalogCategoryId);

        var visible = _context.SupplierProducts.Where(p => p.Supplier == supplier && p.IsActive && p.RemovedFromSupplierAtUtc == null);
        // Same key as RecomputeAsync (CategoryKey), worked out in memory.
        var categories = (await visible
                .GroupBy(p => new { p.CategoryPath, p.CategoryName })
                .Select(g => new { g.Key.CategoryPath, g.Key.CategoryName, Count = g.Count() })
                .ToListAsync(ct))
            .Select(c => new { Key = CategoryKey(c.CategoryPath, c.CategoryName), c.Count })
            .Where(c => c.Key != null)
            .GroupBy(c => c.Key!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SupplierCategorySourceDto
            {
                SourceType = CategorySourceTypes.Category,
                Value = g.Key,
                Label = g.Key,
                ProductCount = g.Sum(x => x.Count),
                CatalogCategoryId = mapped.TryGetValue(MapKey(CategorySourceTypes.Category, g.Key), out var id) ? id : null
            });

        var tagCounts = await (from l in _context.SupplierProductTags
                               join p in visible on l.SupplierProductId equals p.Id
                               group l by l.ProductTagId into g
                               select new { TagId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TagId, x => x.Count, ct);
        var tags = (await _context.ProductTags.AsNoTracking().Where(t => t.Supplier == supplier).ToListAsync(ct))
            .Select(t => new SupplierCategorySourceDto
            {
                SourceType = CategorySourceTypes.Tag,
                Value = t.NormalizedName,
                Label = t.Name,
                TagId = t.Id,
                TagType = t.Type,
                ProductCount = tagCounts.GetValueOrDefault(t.Id),
                CatalogCategoryId = mapped.TryGetValue(MapKey(CategorySourceTypes.Tag, t.NormalizedName), out var id) ? id : null
            })
            .Where(t => t.ProductCount > 0);

        return categories.OrderBy(c => c.Label, StringComparer.OrdinalIgnoreCase)
            .Concat(tags.OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase))
            .ToList();
    }

    public async Task SaveMappingAsync(SaveCategoryMappingRequest request, CancellationToken ct = default)
    {
        request.SourceValue = request.SourceType == CategorySourceTypes.Tag ? TagText.Normalize(request.SourceValue) : request.SourceValue.Trim();
        // Case-insensitive match (PostgreSQL compares text case-sensitively), done in memory: a supplier has few mappings.
        var existing = (await _context.SupplierCategoryMappings
                .Where(m => m.Supplier == request.Supplier && m.SourceType == request.SourceType).ToListAsync(ct))
            .FirstOrDefault(m => m.SourceValue.Equals(request.SourceValue, StringComparison.OrdinalIgnoreCase));
        if (request.CatalogCategoryId == null)
        {
            if (existing != null) _context.SupplierCategoryMappings.Remove(existing);
        }
        else if (existing != null)
        {
            existing.CatalogCategoryId = request.CatalogCategoryId.Value;
        }
        else
        {
            _context.SupplierCategoryMappings.Add(new SupplierCategoryMapping
            {
                Supplier = request.Supplier, SourceType = request.SourceType, SourceValue = request.SourceValue, CatalogCategoryId = request.CatalogCategoryId.Value
            });
        }
        await _context.SaveChangesAsync(ct);
        await RecomputeAsync(request.Supplier, ct);
    }

    // ------------------------------------------------------------------ build my categories from a supplier

    /// <summary>
    /// Creates my categories from a supplier's category paths (e.g. "Extinguishers &amp; Equipment › Fire Extinguisher ID Signs"
    /// becomes two levels) and, optionally, sub-categories from its type tags, placed under the category most of the tag's
    /// products are in (Eden "Carbon Filters" → "Ventilation › Carbon Filters"). Existing categories with the same name are
    /// reused and existing mappings are kept.
    /// </summary>
    public async Task<CategoryImportResultDto> ImportFromSupplierAsync(ImportSupplierCategoriesRequest request, CancellationToken ct = default)
    {
        var result = new CategoryImportResultDto();
        var supplier = request.Supplier;
        var categories = await _context.CatalogCategories.ToListAsync(ct);
        var mappings = await _context.SupplierCategoryMappings.Where(m => m.Supplier == supplier).ToListAsync(ct);

        async Task<CatalogCategory> EnsureChildAsync(int? parentId, string name)
        {
            var existing = categories.FirstOrDefault(c => c.ParentId == parentId && TagText.Normalize(c.Name) == TagText.Normalize(name));
            if (existing != null) return existing;
            var created = new CatalogCategory { Name = name.Trim(), ParentId = parentId };
            _context.CatalogCategories.Add(created);
            await _context.SaveChangesAsync(ct);
            categories.Add(created);
            result.CategoriesCreated++;
            return created;
        }

        int? MappedCategory(string type, string value)
            => mappings.FirstOrDefault(m => m.SourceType == type && m.SourceValue.Equals(value, StringComparison.OrdinalIgnoreCase))?.CatalogCategoryId;

        void AddMapping(string type, string value, int categoryId)
        {
            var mapping = new SupplierCategoryMapping { Supplier = supplier, SourceType = type, SourceValue = value, CatalogCategoryId = categoryId };
            _context.SupplierCategoryMappings.Add(mapping);
            mappings.Add(mapping);
            result.MappingsCreated++;
        }

        // 1. Category paths → category levels.
        var products = await _context.SupplierProducts.AsNoTracking()
            .Where(p => p.Supplier == supplier && p.IsActive && p.RemovedFromSupplierAtUtc == null)
            .Select(p => new { p.Id, p.CategoryPath, p.CategoryName })
            .ToListAsync(ct);
        var keyByProduct = products.ToDictionary(p => p.Id, p => CategoryKey(p.CategoryPath, p.CategoryName));

        foreach (var key in keyByProduct.Values.Where(k => k != null).Select(k => k!).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(k => k))
        {
            if (MappedCategory(CategorySourceTypes.Category, key).HasValue) continue;
            var parentId = request.ParentId;
            CatalogCategory? leaf = null;
            foreach (var segment in key.Split('›', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                leaf = await EnsureChildAsync(parentId, segment);
                parentId = leaf.Id;
            }
            if (leaf != null) AddMapping(CategorySourceTypes.Category, key, leaf.Id);
        }
        await _context.SaveChangesAsync(ct);

        // 2. Type tags → sub-categories under the category most of their products are in.
        if (request.IncludeTags)
        {
            var typeTags = await _context.ProductTags.AsNoTracking().Where(t => t.Supplier == supplier && t.Type == ProductTagTypes.Type).ToListAsync(ct);
            var tagIds = typeTags.Select(t => t.Id).ToList();
            var links = (await _context.SupplierProductTags.AsNoTracking().Where(l => tagIds.Contains(l.ProductTagId)).ToListAsync(ct))
                .Where(l => keyByProduct.ContainsKey(l.SupplierProductId))
                .ToLookup(l => l.ProductTagId, l => l.SupplierProductId);
            var byId = categories.ToDictionary(c => c.Id);

            foreach (var tag in typeTags.OrderBy(t => t.Name))
            {
                if (MappedCategory(CategorySourceTypes.Tag, tag.NormalizedName).HasValue || !links[tag.Id].Any()) continue;

                var dominantKey = links[tag.Id].Select(id => keyByProduct[id]).Where(k => k != null)
                    .GroupBy(k => k!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
                var parentId = dominantKey == null ? request.ParentId : MappedCategory(CategorySourceTypes.Category, dominantKey) ?? request.ParentId;

                // The tag names its own parent or an ancestor (Eden tags "Ventilation" on Ventilation products): map there.
                CatalogCategory? same = null;
                var seen = new HashSet<int>();
                var cursor = parentId;
                while (cursor.HasValue && seen.Add(cursor.Value) && byId.TryGetValue(cursor.Value, out var current))
                {
                    if (TagText.Normalize(current.Name) == tag.NormalizedName) { same = current; break; }
                    cursor = current.ParentId;
                }

                var target = same ?? await EnsureChildAsync(parentId, tag.Name);
                byId[target.Id] = target;
                AddMapping(CategorySourceTypes.Tag, tag.NormalizedName, target.Id);
            }
            await _context.SaveChangesAsync(ct);
        }

        await RecomputeAsync(supplier, ct);
        result.ProductsCategorised = await _context.SupplierProducts
            .CountAsync(p => p.Supplier == supplier && p.IsActive && p.RemovedFromSupplierAtUtc == null && p.CatalogCategoryId != null, ct);
        return result;
    }
}

public static class SalesChannels
{
    public const string Ebay = "eBay";
    public const string NopCommerce = "NopCommerce";
    public const string Amazon = "Amazon";
}
