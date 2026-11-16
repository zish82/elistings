using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>Loads/saves marketplace fee settings and estimates fees and profit for products and listings.</summary>
public class MarketplaceFeeService
{
    public const string Ebay = "eBay";
    private readonly AppDbContext _context;

    public MarketplaceFeeService(AppDbContext context) => _context = context;

    /// <summary>The marketplace's settings with its category rates (defaults when nothing is saved yet).</summary>
    public async Task<MarketplaceFeeSettingsDto> GetSettingsAsync(string marketplace = Ebay, CancellationToken ct = default)
    {
        var row = await _context.MarketplaceFeeSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Marketplace == marketplace, ct);
        var dto = row == null ? new MarketplaceFeeSettingsDto { Marketplace = marketplace } : ToDto(row);
        dto.CategoryRates = (await _context.MarketplaceFeeCategoryRates.AsNoTracking()
                .Where(r => r.Marketplace == marketplace)
                .ToListAsync(ct))
            .OrderBy(r => r.CategoryName ?? r.CategoryId, StringComparer.OrdinalIgnoreCase)
            .Select(r => new MarketplaceFeeCategoryRateDto
            {
                Id = r.Id, CategoryId = r.CategoryId, CategoryName = r.CategoryName, FinalValueFeePercent = r.FinalValueFeePercent,
                Threshold1 = r.Threshold1, Percent2 = r.Percent2, Threshold2 = r.Threshold2, Percent3 = r.Percent3, PerOrderFeeSmall = r.PerOrderFeeSmall
            })
            .ToList();
        dto.EbayCategoryCount = await _context.EbayCategories.CountAsync(ct);
        dto.EbayCategoriesUpdatedAtUtc = dto.EbayCategoryCount == 0 ? null : await _context.EbayCategories.MaxAsync(c => (DateTime?)c.UpdatedAtUtc, ct);
        return dto;
    }

    /// <summary>Returns an error message, or null when saved.</summary>
    public async Task<string?> SaveSettingsAsync(MarketplaceFeeSettingsDto dto, CancellationToken ct = default)
    {
        var error = Validate(dto);
        if (error != null) return error;

        var row = await _context.MarketplaceFeeSettings.FirstOrDefaultAsync(s => s.Marketplace == dto.Marketplace, ct);
        if (row == null)
        {
            row = new MarketplaceFeeSettings { Marketplace = dto.Marketplace };
            _context.MarketplaceFeeSettings.Add(row);
        }
        row.FinalValueFeePercent = dto.FinalValueFeePercent;
        row.PerOrderFeeSmall = dto.PerOrderFeeSmall;
        row.PerOrderFeeLarge = dto.PerOrderFeeLarge;
        row.PerOrderFeeThreshold = dto.PerOrderFeeThreshold;
        row.RegulatoryFeePercent = dto.RegulatoryFeePercent;
        row.VatRegistered = dto.VatRegistered;
        row.VatRatePercent = dto.VatRatePercent;
        row.MinProfit = dto.MinProfit;
        row.UpdatedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>Adds or updates the final value fee % for one category. Returns an error message, or null.</summary>
    public async Task<string?> SaveCategoryRateAsync(string marketplace, MarketplaceFeeCategoryRateDto dto, CancellationToken ct = default)
    {
        var categoryId = dto.CategoryId?.Trim() ?? string.Empty;
        if (categoryId.Length == 0 || !categoryId.All(char.IsDigit)) return "Enter the eBay category ID (a number, e.g. 46782).";
        if (dto.FinalValueFeePercent is < 0m or > 50m || dto.Percent2 is < 0m or > 50m || dto.Percent3 is < 0m or > 50m)
            return "Final value fees must be between 0% and 50%.";
        if (dto.Threshold1.HasValue != dto.Percent2.HasValue) return "Give both the amount and the % for the second tier (or neither).";
        if (dto.Threshold2.HasValue != dto.Percent3.HasValue) return "Give both the amount and the % for the third tier (or neither).";
        if (dto.Threshold2.HasValue && (!dto.Threshold1.HasValue || dto.Threshold2 <= dto.Threshold1)) return "The third tier must start above the second.";
        if (dto.PerOrderFeeSmall is < 0m) return "The small-order fee can't be negative.";

        var row = await _context.MarketplaceFeeCategoryRates.FirstOrDefaultAsync(r => r.Marketplace == marketplace && r.CategoryId == categoryId, ct);
        if (row == null)
        {
            row = new MarketplaceFeeCategoryRate { Marketplace = marketplace, CategoryId = categoryId };
            _context.MarketplaceFeeCategoryRates.Add(row);
        }
        row.CategoryName = string.IsNullOrWhiteSpace(dto.CategoryName) ? null : dto.CategoryName.Trim();
        row.FinalValueFeePercent = dto.FinalValueFeePercent;
        row.Threshold1 = dto.Threshold1;
        row.Percent2 = dto.Percent2;
        row.Threshold2 = dto.Threshold2;
        row.Percent3 = dto.Percent3;
        row.PerOrderFeeSmall = dto.PerOrderFeeSmall;
        await _context.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Loads eBay's UK business-seller rates (<see cref="EbayUkRateCard"/>): updates those categories, adds missing ones,
    /// and keeps rates you added for other categories. Returns how many were added and updated.
    /// </summary>
    public async Task<(int Added, int Updated)> LoadUkRateCardAsync(CancellationToken ct = default)
    {
        var existing = await _context.MarketplaceFeeCategoryRates.Where(r => r.Marketplace == Ebay).ToListAsync(ct);
        int added = 0, updated = 0;
        foreach (var rate in EbayUkRateCard.Rates())
        {
            var row = existing.FirstOrDefault(r => r.CategoryId == rate.CategoryId);
            if (row == null)
            {
                _context.MarketplaceFeeCategoryRates.Add(rate);
                added++;
                continue;
            }
            row.CategoryName = rate.CategoryName;
            row.FinalValueFeePercent = rate.FinalValueFeePercent;
            row.Threshold1 = rate.Threshold1;
            row.Percent2 = rate.Percent2;
            row.Threshold2 = rate.Threshold2;
            row.Percent3 = rate.Percent3;
            row.PerOrderFeeSmall = rate.PerOrderFeeSmall;
            updated++;
        }
        // Known suppliers without a fee category get their typical one (e.g. Eden → Garden & Patio).
        foreach (var supplier in await _context.Suppliers.Where(s => s.FeeCategoryId == null).ToListAsync(ct))
        {
            if (EbayUkRateCard.SupplierFeeCategories.TryGetValue(supplier.Code, out var categoryId)) supplier.FeeCategoryId = categoryId;
        }
        await _context.SaveChangesAsync(ct);
        return (added, updated);
    }

    public async Task<bool> DeleteCategoryRateAsync(int id, CancellationToken ct = default)
    {
        var row = await _context.MarketplaceFeeCategoryRates.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (row == null) return false;
        _context.MarketplaceFeeCategoryRates.Remove(row);
        await _context.SaveChangesAsync(ct);
        return true;
    }

    // ------------------------------------------------------------------ estimates

    /// <summary>A listing as it stands: its price + delivery, its supplier cost and its eBay category.</summary>
    /// <param name="categoryChain">The listing's eBay category and its parents (<see cref="EbayCategoryService.GetChainAsync"/>).</param>
    public static FeeEstimate EstimateListing(Listing listing, IReadOnlyList<string>? categoryChain, SupplierDto? supplier, MarketplaceFeeSettingsDto settings)
        => EstimateSale(listing.Price, listing.DeliveryCost, listing.IncludeDeliveryInPrice, listing.SourcePrice, categoryChain, supplier, settings);

    public static FeeEstimate EstimateSale(decimal itemPrice, decimal? deliveryCost, bool includeDelivery, decimal? sourcePrice,
        IReadOnlyList<string>? categoryChain, SupplierDto? supplier, MarketplaceFeeSettingsDto settings)
        => FeeMath.EstimateSale(itemPrice, deliveryCost, includeDelivery, sourcePrice, categoryChain, supplier, settings);

    /// <summary>
    /// A supplier product if listed at its suggested price with its default delivery (what "Add to my listings" would create).
    /// Null when there's no suggested price.
    /// </summary>
    public static FeeEstimate? EstimateProduct(SupplierProduct product, SupplierDto? supplier, MarketplaceFeeSettingsDto settings)
    {
        var suggested = Server.Services.SuggestedPricing.For(product, supplier).Price;
        if (!suggested.HasValue) return null;
        var delivery = DefaultDeliveryIncVat(product, supplier);
        var (incVat, exVat) = FeeMath.SupplierCosts(product.SupplierPrice, supplier);
        // Products have no eBay category yet: use the supplier's fee category (e.g. Garden & Patio for Eden).
        return FeeMath.Estimate(suggested.Value + (delivery ?? 0m), incVat, exVat, delivery, settings, supplier?.FeeCategoryId);
    }

    /// <summary>Per-product delivery (ex VAT, e.g. Safelincs) when known, else the supplier's suggested option.</summary>
    public static decimal? DefaultDeliveryIncVat(SupplierProduct product, SupplierDto? supplier)
    {
        if (supplier == null) return null;
        return SupplierCatalogService.ProductDelivery(product, supplier).IncVat;
    }

    private static string? Validate(MarketplaceFeeSettingsDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Marketplace)) return "Marketplace is required.";
        if (dto.FinalValueFeePercent is < 0m or > 50m) return "The final value fee must be between 0% and 50%.";
        if (dto.RegulatoryFeePercent is < 0m or > 10m) return "The regulatory fee must be between 0% and 10%.";
        if (dto.PerOrderFeeSmall < 0m || dto.PerOrderFeeLarge < 0m || dto.PerOrderFeeThreshold < 0m) return "Per-order fees can't be negative.";
        if (dto.VatRatePercent is < 0m or > 50m) return "The VAT rate must be between 0% and 50%.";
        return null;
    }

    private static MarketplaceFeeSettingsDto ToDto(MarketplaceFeeSettings s) => new()
    {
        Marketplace = s.Marketplace,
        FinalValueFeePercent = s.FinalValueFeePercent,
        PerOrderFeeSmall = s.PerOrderFeeSmall,
        PerOrderFeeLarge = s.PerOrderFeeLarge,
        PerOrderFeeThreshold = s.PerOrderFeeThreshold,
        RegulatoryFeePercent = s.RegulatoryFeePercent,
        VatRegistered = s.VatRegistered,
        VatRatePercent = s.VatRatePercent,
        MinProfit = s.MinProfit,
        UpdatedAtUtc = s.UpdatedAtUtc
    };
}
