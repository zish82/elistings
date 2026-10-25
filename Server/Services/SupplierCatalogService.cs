using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>
/// Looks up suppliers, their VAT settings and delivery options, and works out the default
/// delivery option / prices for a product or listing. Supplier-agnostic: new suppliers only
/// need a row in Suppliers (+ delivery options), configurable on /suppliers.
/// </summary>
public class SupplierCatalogService
{
    private readonly AppDbContext _context;

    public SupplierCatalogService(AppDbContext context)
    {
        _context = context;
    }

    public async Task<List<SupplierDto>> GetAllAsync(bool includeInactiveOptions = false)
    {
        var suppliers = await _context.Suppliers.OrderBy(s => s.Name).ToListAsync();
        var options = await _context.SupplierDeliveryOptions
            .Where(o => includeInactiveOptions || o.IsActive)
            .OrderBy(o => o.SortOrder).ThenBy(o => o.Name)
            .ToListAsync();

        return suppliers.Select(s => ToDto(s, options.Where(o => o.SupplierId == s.Id))).ToList();
    }

    /// <summary>All suppliers plus the postage classes seen in their products (for the /suppliers page).</summary>
    public async Task<List<SupplierDto>> GetAllForSettingsAsync()
    {
        var suppliers = await GetAllAsync(includeInactiveOptions: true);
        var categories = await _context.SupplierProducts
            .Where(p => p.PostageCategory != null && p.PostageCategory != "")
            .Select(p => new { p.Supplier, p.PostageCategory })
            .Distinct()
            .ToListAsync();

        foreach (var s in suppliers)
        {
            s.KnownPostageCategories = categories
                .Where(c => c.Supplier.Equals(s.Code, StringComparison.OrdinalIgnoreCase))
                .Select(c => c.PostageCategory!)
                .OrderBy(c => c)
                .ToList();
        }
        return suppliers;
    }

    /// <summary>Finds the supplier by product supplier code first, then by the source URL's host.</summary>
    public async Task<SupplierDto?> FindAsync(string? supplierCode, string? sourceUrl)
    {
        var all = (await GetAllAsync()).Where(s => s.IsActive).ToList();
        if (!string.IsNullOrWhiteSpace(supplierCode))
        {
            var byCode = all.FirstOrDefault(s => s.Code.Equals(supplierCode, StringComparison.OrdinalIgnoreCase));
            if (byCode != null) return byCode;
        }

        if (!string.IsNullOrWhiteSpace(sourceUrl) && Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri))
        {
            var host = uri.Host.ToLowerInvariant();
            return all.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.WebsiteHost)
                && (host == s.WebsiteHost!.ToLowerInvariant() || host.EndsWith("." + s.WebsiteHost!.ToLowerInvariant())));
        }

        return null;
    }

    // ------------------------------------------------------------------ enabled / disabled suppliers

    /// <summary>Codes and website hosts of disabled suppliers (IsActive = false).</summary>
    public async Task<List<(string Code, string? Host)>> GetDisabledAsync()
        => (await _context.Suppliers.AsNoTracking().Where(s => !s.IsActive).Select(s => new { s.Code, s.WebsiteHost }).ToListAsync())
            .Select(s => (s.Code, string.IsNullOrWhiteSpace(s.WebsiteHost) ? null : s.WebsiteHost!.ToLowerInvariant()))
            .ToList();

    /// <summary>
    /// Whether scheduled (background) work may call this supplier: the supplier is enabled and its
    /// "Enable background sync" switch is on. Manual imports don't check this.
    /// </summary>
    public async Task<bool> IsBackgroundSyncAllowedAsync(string supplierCode)
        => await _context.Suppliers.AsNoTracking().AnyAsync(s => s.Code == supplierCode && s.IsActive && s.BackgroundSyncEnabled)
           || !await _context.Suppliers.AnyAsync(s => s.Code == supplierCode); // unknown supplier: nothing to switch off

    public async Task<bool> IsDisabledAsync(string supplierCode)
        => await _context.Suppliers.AnyAsync(s => s.Code == supplierCode && !s.IsActive);

    /// <summary>
    /// Listings that come from a supplier: linked to one of its products, or (created from a URL) on its website.
    /// </summary>
    public static IQueryable<Listing> ListingsOf(IQueryable<Listing> listings, AppDbContext context, string supplierCode, string? host)
    {
        var h = string.IsNullOrWhiteSpace(host) ? null : host.ToLowerInvariant();
        return listings.Where(l =>
            context.SupplierProducts.Any(p => p.Id == l.SupplierProductId && p.Supplier == supplierCode)
            || (h != null && l.SupplierProductId == null && l.SourceUrl != null && l.SourceUrl.ToLower().Contains(h)));
    }

    /// <summary>Removes listings of the given (disabled) suppliers from a query.</summary>
    public static IQueryable<Listing> ExcludeSuppliers(IQueryable<Listing> listings, AppDbContext context, IEnumerable<(string Code, string? Host)> suppliers)
    {
        foreach (var (code, host) in suppliers)
        {
            var h = host;
            listings = listings.Where(l =>
                !(context.SupplierProducts.Any(p => p.Id == l.SupplierProductId && p.Supplier == code)
                  || (h != null && l.SupplierProductId == null && l.SourceUrl != null && l.SourceUrl.ToLower().Contains(h))));
        }
        return listings;
    }

    /// <summary>The disabled supplier a listing belongs to, or null when its supplier is enabled / unknown.</summary>
    public async Task<string?> FindDisabledSupplierNameAsync(Listing listing)
    {
        var disabled = await _context.Suppliers.AsNoTracking().Where(s => !s.IsActive).ToListAsync();
        if (disabled.Count == 0) return null;

        if (listing.SupplierProductId.HasValue)
        {
            var code = await _context.SupplierProducts.Where(p => p.Id == listing.SupplierProductId.Value).Select(p => p.Supplier).FirstOrDefaultAsync();
            var match = disabled.FirstOrDefault(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (match != null || code != null) return match?.Name;
        }

        if (!string.IsNullOrWhiteSpace(listing.SourceUrl))
        {
            var url = listing.SourceUrl.ToLowerInvariant();
            return disabled.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.WebsiteHost) && url.Contains(s.WebsiteHost!.ToLowerInvariant()))?.Name;
        }
        return null;
    }

    public async Task<ListingSupplierContextDto> GetContextForListingAsync(Listing listing)
    {
        SupplierProduct? product = null;
        if (listing.SupplierProductId.HasValue)
        {
            product = await _context.SupplierProducts.FirstOrDefaultAsync(p => p.Id == listing.SupplierProductId.Value);
        }

        var supplier = await FindAsync(product?.Supplier, listing.SourceUrl);
        return BuildContext(supplier, product?.PostageCategory);
    }

    public ListingSupplierContextDto BuildContext(SupplierDto? supplier, string? postageCategory)
    {
        return new ListingSupplierContextDto
        {
            Supplier = supplier,
            PostageCategory = postageCategory,
            SuggestedDeliveryOptionId = SuggestDeliveryOption(supplier, postageCategory)?.Id
        };
    }

    /// <summary>Option whose PostageCategories contains the product's category, else the supplier default.</summary>
    public static SupplierDeliveryOptionDto? SuggestDeliveryOption(SupplierDto? supplier, string? postageCategory)
    {
        if (supplier == null) return null;
        var active = supplier.DeliveryOptions.Where(o => o.IsActive).ToList();

        if (!string.IsNullOrWhiteSpace(postageCategory))
        {
            var match = active.FirstOrDefault(o => (o.PostageCategories ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(c => c.Equals(postageCategory.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (match != null) return match;
        }

        return active.FirstOrDefault(o => o.IsDefault) ?? active.FirstOrDefault();
    }

    public static SupplierDto ToDto(Supplier s, IEnumerable<SupplierDeliveryOption> options) => new()
    {
        Id = s.Id,
        Code = s.Code,
        Name = s.Name,
        WebsiteHost = s.WebsiteHost,
        SkuPrefix = s.SkuPrefix,
        PricesIncludeVat = s.PricesIncludeVat,
        VatRatePercent = s.VatRatePercent,
        DefaultMarginPercent = s.DefaultMarginPercent,
        IsActive = s.IsActive,
        FeeCategoryId = s.FeeCategoryId,
        BackgroundSyncEnabled = s.BackgroundSyncEnabled,
        DeliveryOptions = options.Select(ToDto).ToList()
    };

    public static SupplierDeliveryOptionDto ToDto(SupplierDeliveryOption o) => new()
    {
        Id = o.Id,
        SupplierId = o.SupplierId,
        Name = o.Name,
        Description = o.Description,
        PostageCategories = o.PostageCategories,
        Cost = o.Cost,
        CostIncludesVat = o.CostIncludesVat,
        IsDefault = o.IsDefault,
        SortOrder = o.SortOrder,
        IsActive = o.IsActive
    };
}
