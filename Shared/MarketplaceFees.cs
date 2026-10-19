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

    /// <summary>The final value fee % for an eBay category (override when set, else the default).</summary>
    public decimal FinalValueFeePercentFor(string? categoryId)
        => string.IsNullOrWhiteSpace(categoryId)
            ? FinalValueFeePercent
            : CategoryRates.FirstOrDefault(r => r.CategoryId == categoryId.Trim())?.FinalValueFeePercent ?? FinalValueFeePercent;
}

/// <summary>Final value fee % for one eBay category, e.g. 12.5% for Business, Office &amp; Industrial.</summary>
public class MarketplaceFeeCategoryRateDto
{
    public int Id { get; set; }
    public string CategoryId { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    public decimal FinalValueFeePercent { get; set; }
}

/// <summary>Estimated fees and profit for one sale.</summary>
public class FeeEstimate
{
    /// <summary>What the buyer pays (item + delivery).</summary>
    public decimal SaleTotal { get; set; }
    public decimal FinalValueFeePercent { get; set; }
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
    {
        var total = Math.Max(0m, saleTotal);
        var vatFactor = 1m + settings.VatRatePercent / 100m;
        var percent = settings.FinalValueFeePercentFor(categoryId);

        var estimate = new FeeEstimate
        {
            SaleTotal = total,
            FinalValueFeePercent = percent,
            FinalValueFee = Round(total * percent / 100m),
            PerOrderFee = total <= 0m ? 0m : total <= settings.PerOrderFeeThreshold ? settings.PerOrderFeeSmall : settings.PerOrderFeeLarge,
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
    public static FeeEstimate EstimateSale(decimal itemPrice, decimal? deliveryCost, bool includeDelivery, decimal? supplierPrice,
        string? categoryId, SupplierDto? supplier, MarketplaceFeeSettingsDto settings)
    {
        var total = PricingMath.ListingTotal(itemPrice, deliveryCost, includeDelivery);
        var (incVat, exVat) = SupplierCosts(supplierPrice, supplier);
        return Estimate(total, incVat, exVat, deliveryCost, settings, categoryId);
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
