namespace Shared;

/// <summary>
/// Marketplace selling-fee settings (eBay UK business seller rate card by default), edited on /suppliers.
/// eBay has no API that returns the final value fee before a sale, so fees are estimated from these.
/// </summary>
public class MarketplaceFeeSettingsDto
{
    public string Marketplace { get; set; } = "eBay";
    /// <summary>Final value fee % of the total sale (item + postage) for categories without an override.</summary>
    public decimal FinalValueFeePercent { get; set; } = 12.9m;
    /// <summary>Per-order fee for orders up to <see cref="PerOrderFeeThreshold"/>.</summary>
    public decimal PerOrderFeeSmall { get; set; } = 0.30m;
    /// <summary>Per-order fee for orders above <see cref="PerOrderFeeThreshold"/>.</summary>
    public decimal PerOrderFeeLarge { get; set; } = 0.40m;
    public decimal PerOrderFeeThreshold { get; set; } = 10m;
    /// <summary>Regulatory operating fee % of the total sale.</summary>
    public decimal RegulatoryFeePercent { get; set; } = 0.35m;
    /// <summary>
    /// VAT-registered: VAT on fees and on supplier costs is reclaimed, and VAT is due on the sale price.
    /// Not registered: VAT on eBay's fees is a cost, and nothing is due on sales.
    /// </summary>
    public bool VatRegistered { get; set; }
    public decimal VatRatePercent { get; set; } = 20m;
    /// <summary>Warn before publishing when the estimated profit per sale is below this.</summary>
    public decimal MinProfit { get; set; } = 1m;
    public DateTime? UpdatedAtUtc { get; set; }
    public List<MarketplaceFeeCategoryRateDto> CategoryRates { get; set; } = new();
    /// <summary>How many eBay categories are downloaded (0 = not yet; then only exact category-ID rates match).</summary>
    public int EbayCategoryCount { get; set; }
    public DateTime? EbayCategoriesUpdatedAtUtc { get; set; }

    /// <summary>
    /// The rate for the most specific category in <paramref name="categoryChain"/> (the listing's eBay category first,
    /// then its parents up to the top level), or null for the default.
    /// </summary>
    public MarketplaceFeeCategoryRateDto? FindRate(IReadOnlyList<string>? categoryChain)
    {
        if (categoryChain == null) return null;
        foreach (var id in categoryChain)
        {
            var rate = CategoryRates.FirstOrDefault(r => r.CategoryId == id?.Trim());
            if (rate != null) return rate;
        }
        return null;
    }

    /// <summary>A reduced per-order fee for small orders set on the category or one of its parents (e.g. 10p in Home, Furniture &amp; DIY).</summary>
    public decimal? FindSmallOrderFee(IReadOnlyList<string>? categoryChain)
        => categoryChain?
            .Select(id => CategoryRates.FirstOrDefault(r => r.CategoryId == id?.Trim()))
            .FirstOrDefault(r => r?.PerOrderFeeSmall != null)?.PerOrderFeeSmall;

    /// <summary>The final value fee % (first tier) for one eBay category ID, or the default.</summary>
    public decimal FinalValueFeePercentFor(string? categoryId)
        => FindRate(string.IsNullOrWhiteSpace(categoryId) ? null : new[] { categoryId })?.FinalValueFeePercent ?? FinalValueFeePercent;
}

/// <summary>
/// Final value fee for one eBay category (and everything below it unless a sub-category has its own rate),
/// e.g. Home, Furniture &amp; DIY: 11.9% up to £500, 7.9% above. Tiers are per item on the total sale.
/// </summary>
public class MarketplaceFeeCategoryRateDto
{
    public int Id { get; set; }
    public string CategoryId { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    /// <summary>% on the total sale up to <see cref="Threshold1"/> (or all of it when there's no threshold).</summary>
    public decimal FinalValueFeePercent { get; set; }
    public decimal? Threshold1 { get; set; }
    /// <summary>% on the portion above <see cref="Threshold1"/> (up to <see cref="Threshold2"/>).</summary>
    public decimal? Percent2 { get; set; }
    public decimal? Threshold2 { get; set; }
    /// <summary>% on the portion above <see cref="Threshold2"/>.</summary>
    public decimal? Percent3 { get; set; }
    /// <summary>Per-order fee for small orders in this category, when lower than the standard one.</summary>
    public decimal? PerOrderFeeSmall { get; set; }

    /// <summary>e.g. "11.9% up to £500, then 7.9%".</summary>
    public string Describe()
    {
        var text = $"{FinalValueFeePercent:0.##}%";
        if (Threshold1.HasValue && Percent2.HasValue)
        {
            text += $" up to £{Threshold1:0.##}, then {Percent2:0.##}%";
            if (Threshold2.HasValue && Percent3.HasValue) text += $" up to £{Threshold2:0.##}, then {Percent3:0.##}%";
        }
        if (PerOrderFeeSmall.HasValue) text += $"; small-order fee £{PerOrderFeeSmall:0.00}";
        return text;
    }

    /// <summary>The final value fee on a sale total, applying the tiers.</summary>
    public decimal FinalValueFeeFor(decimal total)
    {
        if (!Threshold1.HasValue || !Percent2.HasValue) return total * FinalValueFeePercent / 100m;
        var fee = Math.Min(total, Threshold1.Value) * FinalValueFeePercent / 100m;
        if (total <= Threshold1.Value) return fee;

        if (Threshold2.HasValue && Percent3.HasValue && Threshold2.Value > Threshold1.Value)
        {
            fee += (Math.Min(total, Threshold2.Value) - Threshold1.Value) * Percent2.Value / 100m;
            if (total > Threshold2.Value) fee += (total - Threshold2.Value) * Percent3.Value / 100m;
            return fee;
        }
        return fee + (total - Threshold1.Value) * Percent2.Value / 100m;
    }
}

/// <summary>A plain message returned by an action (e.g. "Downloaded 34,512 eBay categories.").</summary>
public class MessageDto
{
    public string Message { get; set; } = string.Empty;
}

/// <summary>Estimated fees and profit for one sale.</summary>
public class FeeEstimate
{
    /// <summary>What the buyer pays (item + delivery).</summary>
    public decimal SaleTotal { get; set; }
    /// <summary>First-tier rate used (see <see cref="RateDescription"/> for tiers).</summary>
    public decimal FinalValueFeePercent { get; set; }
    /// <summary>The category rate applied, e.g. "Garden &amp; Patio: 10.9%"; null = the default rate.</summary>
    public string? RateDescription { get; set; }
    public decimal FinalValueFee { get; set; }
    public decimal PerOrderFee { get; set; }
    public decimal RegulatoryFee { get; set; }
    /// <summary>VAT charged on eBay's fees that you can't reclaim (0 when VAT-registered).</summary>
    public decimal VatOnFees { get; set; }
    public decimal TotalFees => FinalValueFee + PerOrderFee + RegulatoryFee + VatOnFees;
    /// <summary>VAT due on the sale (0 when not VAT-registered).</summary>
    public decimal SalesVat { get; set; }
    /// <summary>Supplier item cost counted as a cost (inc VAT unless VAT-registered).</summary>
    public decimal? ItemCost { get; set; }
    public decimal DeliveryCost { get; set; }
    /// <summary>Null when the item cost is unknown.</summary>
    public decimal? Profit { get; set; }
    /// <summary>Profit as % of the sale total.</summary>
    public decimal? MarginPercent => Profit.HasValue && SaleTotal > 0 ? Math.Round(Profit.Value / SaleTotal * 100m, 1) : null;
}

/// <summary>Fee and profit maths shared by the server (products, dashboard) and the listing editor.</summary>
public static class FeeMath
{
    /// <param name="saleTotal">Item price + delivery charged to the buyer.</param>
    /// <param name="itemCostIncVat">Supplier cost of the item including VAT (null = unknown).</param>
    /// <param name="itemCostExVat">Same without VAT (used when VAT-registered; falls back to inc VAT / (1 + rate)).</param>
    /// <param name="deliveryCostIncVat">What the supplier charges you for delivery, inc VAT.</param>
    public static FeeEstimate Estimate(decimal saleTotal, decimal? itemCostIncVat, decimal? itemCostExVat, decimal? deliveryCostIncVat,
        MarketplaceFeeSettingsDto settings, string? categoryId = null)
        => Estimate(saleTotal, itemCostIncVat, itemCostExVat, deliveryCostIncVat, settings,
            string.IsNullOrWhiteSpace(categoryId) ? null : (IReadOnlyList<string>)new[] { categoryId });

    /// <param name="categoryChain">The eBay category and its parents (most specific first); the first one with a rate is used.</param>
    public static FeeEstimate Estimate(decimal saleTotal, decimal? itemCostIncVat, decimal? itemCostExVat, decimal? deliveryCostIncVat,
        MarketplaceFeeSettingsDto settings, IReadOnlyList<string>? categoryChain)
    {
        var total = Math.Max(0m, saleTotal);
        var vatFactor = 1m + settings.VatRatePercent / 100m;
        var rate = settings.FindRate(categoryChain);
        var smallOrderFee = settings.FindSmallOrderFee(categoryChain) ?? settings.PerOrderFeeSmall;

        var estimate = new FeeEstimate
        {
            SaleTotal = total,
            FinalValueFeePercent = rate?.FinalValueFeePercent ?? settings.FinalValueFeePercent,
            RateDescription = rate == null ? null : $"{rate.CategoryName ?? "#" + rate.CategoryId}: {rate.Describe()}",
            FinalValueFee = Round(rate?.FinalValueFeeFor(total) ?? total * settings.FinalValueFeePercent / 100m),
            PerOrderFee = total <= 0m ? 0m : total <= settings.PerOrderFeeThreshold ? smallOrderFee : settings.PerOrderFeeLarge,
            RegulatoryFee = Round(total * settings.RegulatoryFeePercent / 100m)
        };
        var feesExVat = estimate.FinalValueFee + estimate.PerOrderFee + estimate.RegulatoryFee;

        decimal? itemCost;
        decimal delivery;
        if (settings.VatRegistered)
        {
            // VAT on fees and purchases is reclaimed; VAT on the sale is owed.
            estimate.VatOnFees = 0m;
            estimate.SalesVat = Round(total - total / vatFactor);
            itemCost = itemCostExVat ?? (itemCostIncVat.HasValue ? Round(itemCostIncVat.Value / vatFactor) : null);
            delivery = Round((deliveryCostIncVat ?? 0m) / vatFactor);
        }
        else
        {
            estimate.VatOnFees = Round(feesExVat * settings.VatRatePercent / 100m);
            estimate.SalesVat = 0m;
            itemCost = itemCostIncVat;
            delivery = deliveryCostIncVat ?? 0m;
        }

        estimate.ItemCost = itemCost;
        estimate.DeliveryCost = delivery;
        estimate.Profit = itemCost.HasValue
            ? Round(total - estimate.SalesVat - estimate.TotalFees - itemCost.Value - delivery)
            : null;
        return estimate;
    }

    /// <summary>A sale at item price + delivery, costed from the supplier price and the supplier's VAT setting.</summary>
    /// <param name="categoryChain">The listing's eBay category and its parents; when empty, the supplier's fee category is used.</param>
    public static FeeEstimate EstimateSale(decimal itemPrice, decimal? deliveryCost, bool includeDelivery, decimal? supplierPrice,
        IReadOnlyList<string>? categoryChain, SupplierDto? supplier, MarketplaceFeeSettingsDto settings)
    {
        var total = PricingMath.ListingTotal(itemPrice, deliveryCost, includeDelivery);
        var (incVat, exVat) = SupplierCosts(supplierPrice, supplier);
        // No eBay category, or one without a known rate (e.g. the category tree isn't downloaded): fall back to the
        // supplier's fee category before the default rate.
        if (!string.IsNullOrWhiteSpace(supplier?.FeeCategoryId) && settings.FindRate(categoryChain) == null)
        {
            categoryChain = (categoryChain ?? Array.Empty<string>()).Append(supplier!.FeeCategoryId!).ToList();
        }
        return Estimate(total, incVat, exVat, deliveryCost, settings, categoryChain);
    }

    /// <summary>Supplier price with and without VAT, using the supplier's "prices include VAT" setting.</summary>
    public static (decimal? IncVat, decimal? ExVat) SupplierCosts(decimal? supplierPrice, SupplierDto? supplier)
    {
        if (!supplierPrice.HasValue) return (null, null);
        var incVat = PricingMath.CostIncVat(supplierPrice, supplier);
        var rate = supplier?.VatRatePercent ?? 20m;
        decimal? exVat = supplier is { PricesIncludeVat: false }
            ? supplierPrice
            : Round(supplierPrice.Value / (1 + rate / 100m));
        return (incVat, exVat);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
