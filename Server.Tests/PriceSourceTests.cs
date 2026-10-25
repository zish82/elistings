using Server.Controllers;
using Server.Data;
using Xunit;

namespace Server.Tests;

/// <summary>The "where did this price come from" text on the Products pages.</summary>
public class PriceSourceTests
{
    [Fact]
    public void Safelincs_TradePriceList_OrWebsiteEstimate()
    {
        var fromList = new SupplierProduct { Supplier = "Safelincs", PriceIsEstimate = false, TradePriceFetchedAtUtc = new DateTime(2026, 10, 23) };
        var fromWebsite = new SupplierProduct { Supplier = "Safelincs", PriceIsEstimate = true };

        Assert.Equal("Safelincs trade price list (23 Oct 2026)", SupplierProductsController.PriceSource(fromList));
        Assert.Contains("not in the trade price list", SupplierProductsController.PriceSource(fromWebsite));
    }

    [Fact]
    public void Eden_TradePrice_OrRrp()
    {
        Assert.StartsWith("Eden trade price", SupplierProductsController.PriceSource(new SupplierProduct { Supplier = "EdenHorticulture" }));
        Assert.StartsWith("Eden RRP", SupplierProductsController.PriceSource(new SupplierProduct { Supplier = "EdenHorticulture", PriceIsEstimate = true }));
    }
}
