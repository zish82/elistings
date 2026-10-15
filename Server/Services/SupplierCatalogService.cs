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
