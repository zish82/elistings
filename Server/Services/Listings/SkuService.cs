using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Server.Data;

namespace Server.Services;

/// <summary>
/// SKU rules (shared by every marketplace):
///  - Format: {SupplierPrefix}-{SupplierSku}, e.g. DSM-S16301. Duplicates get -2, -3, ...
///  - No supplier code: {SupplierPrefix}-L{ListingId}; no supplier at all: MAN-L{ListingId}.
///  - Upper case, letters/digits/hyphen/underscore only, max 50 characters (eBay limit), unique.
///  - Generated when a listing is created; editable until published, then fixed
///    (the eBay inventory item is keyed by SKU).
/// </summary>
public class SkuService
{
    public const int MaxLength = 50;
    public const string ManualPrefix = "MAN";
    private static readonly Regex InvalidChars = new("[^A-Z0-9_-]+", RegexOptions.Compiled);
    private static readonly Regex RepeatedHyphens = new("-{2,}", RegexOptions.Compiled);

    private readonly AppDbContext _context;
    private readonly SupplierCatalogService _catalog;

    public SkuService(AppDbContext context, SupplierCatalogService catalog)
    {
        _context = context;
        _catalog = catalog;
    }

    /// <summary>Upper-cases and strips characters eBay doesn't accept. Returns null for blank input.</summary>
    public static string? Normalize(string? sku)
    {
        if (string.IsNullOrWhiteSpace(sku)) return null;
        var cleaned = RepeatedHyphens.Replace(InvalidChars.Replace(sku.Trim().ToUpperInvariant(), "-"), "-").Trim('-');
        return cleaned.Length == 0 ? null : cleaned;
    }

    public Task<bool> IsInUseAsync(string sku, int? excludeListingId)
        => _context.Listings.AnyAsync(l => l.Sku == sku && (!excludeListingId.HasValue || l.Id != excludeListingId.Value));

    /// <summary>
    /// Gives the listing a SKU if it doesn't have one. The listing must already be saved
    /// (the fallback format uses its Id). Does not call SaveChanges.
    /// </summary>
    public async Task EnsureSkuAsync(Listing listing)
    {
        if (!string.IsNullOrWhiteSpace(listing.Sku)) return;
        if (listing.Id <= 0) throw new InvalidOperationException("Save the listing before generating its SKU.");

        SupplierProduct? product = null;
        if (listing.SupplierProductId.HasValue)
        {
            product = await _context.SupplierProducts.FirstOrDefaultAsync(p => p.Id == listing.SupplierProductId.Value);
        }

        var supplier = await _catalog.FindAsync(product?.Supplier, listing.SourceUrl);
        var prefix = Normalize(supplier?.SkuPrefix) ?? ManualPrefix;
        var supplierSku = Normalize(product?.SupplierSku ?? listing.SourceProductCode);

        var baseSku = supplier != null && supplierSku != null
            ? $"{prefix}-{supplierSku}"
            : $"{prefix}-L{listing.Id}";

        listing.Sku = await MakeUniqueAsync(baseSku, listing.Id);
    }

    private async Task<string> MakeUniqueAsync(string baseSku, int listingId)
    {
        var candidate = Truncate(baseSku, MaxLength);
        for (var n = 2; await IsInUseAsync(candidate, listingId) || IsTrackedElsewhere(candidate, listingId); n++)
        {
            var suffix = $"-{n}";
            candidate = Truncate(baseSku, MaxLength - suffix.Length) + suffix;
        }
        return candidate;
    }

    // Listings added in the same unit of work (e.g. bulk add) aren't in the database yet.
    private bool IsTrackedElsewhere(string sku, int listingId)
        => _context.ChangeTracker.Entries<Listing>().Any(e => e.Entity.Id != listingId && e.Entity.Sku == sku);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd('-');
}
