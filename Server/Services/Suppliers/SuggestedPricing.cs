using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>A product's suggested price (and where it comes from), worked out on demand from its cost, website price and the supplier's settings. Nothing is stored.</summary>
public static class SuggestedPricing
{
    public static (decimal? Price, string? Source) For(SupplierProduct product, SupplierDto? supplier)
        => PricingMath.SuggestPrice(product.SupplierPrice, product.RrpPrice, supplier);
}
