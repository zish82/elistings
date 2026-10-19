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
            .Select(r => new MarketplaceFeeCategoryRateDto { Id = r.Id, CategoryId = r.CategoryId, CategoryName = r.CategoryName, FinalValueFeePercent = r.FinalValueFeePercent })
            .ToList();
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
        if (dto.FinalValueFeePercent is < 0m or > 50m) return "The final value fee must be between 0% and 50%.";

        var row = await _context.MarketplaceFeeCategoryRates.FirstOrDefaultAsync(r => r.Marketplace == marketplace && r.CategoryId == categoryId, ct);
        if (row == null)
        {
            row = new MarketplaceFeeCategoryRate { Marketplace = marketplace, CategoryId = categoryId };
            _context.MarketplaceFeeCategoryRates.Add(row);
        }
        row.CategoryName = string.IsNullOrWhiteSpace(dto.CategoryName) ? null : dto.CategoryName.Trim();
        row.FinalValueFeePercent = dto.FinalValueFeePercent;
        await _context.SaveChangesAsync(ct);
        return null;
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
    public static FeeEstimate EstimateListing(Listing listing, SupplierDto? supplier, MarketplaceFeeSettingsDto settings)
        => EstimateSale(listing.Price, listing.DeliveryCost, listing.IncludeDeliveryInPrice, listing.SourcePrice, listing.CategoryId, supplier, settings);

    public static FeeEstimate EstimateSale(decimal itemPrice, decimal? deliveryCost, bool includeDelivery, decimal? sourcePrice,
        string? categoryId, SupplierDto? supplier, MarketplaceFeeSettingsDto settings)
        => FeeMath.EstimateSale(itemPrice, deliveryCost, includeDelivery, sourcePrice, categoryId, supplier, settings);

    /// <summary>
    /// A supplier product if listed at its suggested price with its default delivery (what "Add to my listings" would create).
    /// Null when there's no suggested price.
    /// </summary>
    public static FeeEstimate? EstimateProduct(SupplierProduct product, SupplierDto? supplier, MarketplaceFeeSettingsDto settings)
    {
        if (!product.SuggestedPrice.HasValue) return null;
        var delivery = DefaultDeliveryIncVat(product, supplier);
        var (incVat, exVat) = FeeMath.SupplierCosts(product.SupplierPrice, supplier);
        return FeeMath.Estimate(product.SuggestedPrice.Value + (delivery ?? 0m), incVat, exVat, delivery, settings);
    }

    /// <summary>Per-product delivery (ex VAT, e.g. Safelincs) when known, else the supplier's suggested option.</summary>
    public static decimal? DefaultDeliveryIncVat(SupplierProduct product, SupplierDto? supplier)
    {
        if (supplier == null) return null;
        if (product.DeliveryCost.HasValue) return PricingMath.AddVat(product.DeliveryCost.Value, includesVat: false, supplier.VatRatePercent);
        var option = SupplierCatalogService.SuggestDeliveryOption(supplier, product.PostageCategory);
        return option == null ? null : PricingMath.DeliveryIncVat(option, supplier);
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
