using Shared;
using Xunit;

namespace Server.Tests;

public class SuggestedPriceSourceTests
{
    private static SupplierDto Supplier(bool useWebsite) => new() { PricesIncludeVat = false, VatRatePercent = 20m, DefaultMarginPercent = 20m, UseSupplierSuggestedPrice = useWebsite };

    [Fact]
    public void WebsitePrice_UsedWhenOptionOnAndKnown()
    {
        var (price, source) = PricingMath.SuggestPrice(29.35m, 50.99m, Supplier(true));
        Assert.Equal(50.99m, price);
        Assert.Equal(PricingMath.SourceWebsite, source);
    }

    [Fact]
    public void FallsBackToMargin_WhenNoWebsitePrice()
    {
        var (price, source) = PricingMath.SuggestPrice(29.35m, null, Supplier(true));
        Assert.Equal(42.99m, price);
        Assert.Equal(PricingMath.SourceMargin, source);
    }

    [Fact]
    public void OptionOff_AlwaysUsesMargin()
    {
        var (price, source) = PricingMath.SuggestPrice(29.35m, 50.99m, Supplier(false));
        Assert.Equal(42.99m, price);
        Assert.Equal(PricingMath.SourceMargin, source);
    }

    [Fact]
    public void NothingKnown_NoSuggestion()
    {
        var (price, source) = PricingMath.SuggestPrice(null, null, Supplier(true));
        Assert.Null(price);
        Assert.Null(source);
    }
}
