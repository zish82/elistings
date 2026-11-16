using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;
using Server.Configuration;
using Server.Controllers;
using Server.Data;
using Server.Services;
using Server.Services.Jobs;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>eBay fee and profit estimates (FeeMath), fee settings, and where the estimate is shown.</summary>
public class FeeEstimateTests
{
    private static MarketplaceFeeSettingsDto Defaults() => new(); // 12.9%, £0.30/£0.40 at £10, 0.35%, not VAT-registered, 20% VAT

    [Fact]
    public void NotVatRegistered_FeesIncludeVat_AndNoVatOnSale()
    {
        // £20 sale, item cost £10 inc VAT, delivery £4.80.
        var e = FeeMath.Estimate(20m, 10m, 8.33m, 4.80m, Defaults());

        Assert.Equal(2.58m, e.FinalValueFee);   // 12.9% of 20
        Assert.Equal(0.40m, e.PerOrderFee);     // over £10
        Assert.Equal(0.07m, e.RegulatoryFee);   // 0.35% of 20
        Assert.Equal(0.61m, e.VatOnFees);       // 20% of 3.05
        Assert.Equal(3.66m, e.TotalFees);
        Assert.Equal(0m, e.SalesVat);
        Assert.Equal(10m, e.ItemCost);          // inc VAT is the real cost
        Assert.Equal(1.54m, e.Profit);          // 20 - 3.66 - 10 - 4.80
        Assert.Equal(7.7m, e.MarginPercent);
    }

    [Fact]
    public void VatRegistered_ReclaimsVat_AndPaysVatOnSale()
    {
        var settings = Defaults();
        settings.VatRegistered = true;

        var e = FeeMath.Estimate(24m, 12m, 10m, 6m, settings);

        Assert.Equal(0m, e.VatOnFees);
        Assert.Equal(4m, e.SalesVat);           // 24 - 24/1.2
        Assert.Equal(10m, e.ItemCost);          // ex VAT
        Assert.Equal(5m, e.DeliveryCost);       // 6 / 1.2
        // fees: 3.10 + 0.40 + 0.08 = 3.58 → 24 - 4 - 3.58 - 10 - 5
        Assert.Equal(1.42m, e.Profit);
    }

    [Theory]
    [InlineData(10.00, 0.30)]   // up to £10: small fee
    [InlineData(10.01, 0.40)]
    [InlineData(0.0, 0.0)]
    public void PerOrderFee_DependsOnTheOrderTotal(decimal total, decimal expected)
        => Assert.Equal(expected, FeeMath.Estimate(total, null, null, null, Defaults()).PerOrderFee);

    [Fact]
    public void CategoryRate_OverridesTheDefault()
    {
        var settings = Defaults();
        settings.CategoryRates.Add(new MarketplaceFeeCategoryRateDto { CategoryId = "12576", FinalValueFeePercent = 12.5m });

        Assert.Equal(12.5m, FeeMath.Estimate(100m, null, null, null, settings, "12576").FinalValueFeePercent);
        Assert.Equal(12.9m, FeeMath.Estimate(100m, null, null, null, settings, "99999").FinalValueFeePercent);
        Assert.Equal(12.9m, FeeMath.Estimate(100m, null, null, null, settings).FinalValueFeePercent);
    }

    [Fact]
    public void UnknownCost_GivesFeesButNoProfit()
    {
        var e = FeeMath.Estimate(20m, null, null, null, Defaults());
        Assert.Null(e.Profit);
        Assert.True(e.TotalFees > 0);
    }

    [Fact]
    public void SupplierCosts_UseTheSuppliersVatSetting()
    {
        var exVatSupplier = new SupplierDto { PricesIncludeVat = false, VatRatePercent = 20m };
        var incVatSupplier = new SupplierDto { PricesIncludeVat = true, VatRatePercent = 20m };

        Assert.Equal(((decimal?)12m, (decimal?)10m), FeeMath.SupplierCosts(10m, exVatSupplier));
        Assert.Equal(((decimal?)12m, (decimal?)10m), FeeMath.SupplierCosts(12m, incVatSupplier));
        Assert.Equal(((decimal?)null, (decimal?)null), FeeMath.SupplierCosts(null, exVatSupplier));
    }

    // ------------------------------------------------------------------ settings

    [Fact]
    public async Task Settings_DefaultsUntilSaved_ThenSavedValues()
    {
        using var ctx = TestHelpers.NewContext();
        var service = new MarketplaceFeeService(ctx);

        var defaults = await service.GetSettingsAsync();
        Assert.Equal(12.9m, defaults.FinalValueFeePercent);
        Assert.False(defaults.VatRegistered);

        defaults.FinalValueFeePercent = 13.5m;
        defaults.MinProfit = 2m;
        Assert.Null(await service.SaveSettingsAsync(defaults));
        Assert.Null(await service.SaveCategoryRateAsync("eBay", new MarketplaceFeeCategoryRateDto { CategoryId = "46782", FinalValueFeePercent = 11m }));

        var saved = await service.GetSettingsAsync();
        Assert.Equal(13.5m, saved.FinalValueFeePercent);
        Assert.Equal(2m, saved.MinProfit);
        Assert.Equal(11m, Assert.Single(saved.CategoryRates).FinalValueFeePercent);
    }

    [Theory]
    [InlineData(-1.0, 0.35, "final value")]
    [InlineData(60.0, 0.35, "final value")]
    [InlineData(12.9, 20.0, "regulatory")]
    public async Task Settings_RejectsSillyValues(decimal fvf, decimal regulatory, string expected)
    {
        using var ctx = TestHelpers.NewContext();
        var error = await new MarketplaceFeeService(ctx).SaveSettingsAsync(new MarketplaceFeeSettingsDto { FinalValueFeePercent = fvf, RegulatoryFeePercent = regulatory });
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CategoryRate_NeedsANumericCategoryId()
    {
        using var ctx = TestHelpers.NewContext();
        var error = await new MarketplaceFeeService(ctx).SaveCategoryRateAsync("eBay", new MarketplaceFeeCategoryRateDto { CategoryId = "Candles", FinalValueFeePercent = 12m });
        Assert.NotNull(error);
    }

    // ------------------------------------------------------------------ products / listings

    [Fact]
    public async Task Products_ShowEstimatedProfitAfterFeesAndDelivery()
    {
        using var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx); // DropShipMall, prices ex VAT
        ctx.SupplierDeliveryOptions.Add(new SupplierDeliveryOption { SupplierId = ctx.Suppliers.Single().Id, Name = "Standard", Cost = 4m, IsDefault = true, IsActive = true });
        ctx.SupplierProducts.Add(new SupplierProduct { Supplier = "DropShipMall", Title = "Vase", SupplierPrice = 10m }); // suggested: 10 + VAT = 12, +20% = 14.40 → 14.99
        ctx.SaveChanges();
        var catalog = new SupplierCatalogService(ctx);
        var controller = new SupplierProductsController(ctx, TestHelpers.User(), new Mock<IFeedProductSyncService>().Object,
            Options.Create(new EbaySettings()), new ProductEnrichmentService(ctx, Array.Empty<ISupplierProductEnricher>(), catalog),
            new BulkJobService(ctx, new Mock<IBackgroundJobClient>().Object), new SkuService(ctx, catalog));

        var result = await controller.GetProducts(new SupplierProductQuery { Supplier = "DropShipMall" });
        var product = Assert.Single(Assert.IsType<List<SupplierProductListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value));

        // Sale 14.99 + 4.80 delivery = 19.79; fees 2.55 + 0.40 + 0.07 + VAT 0.60 = 3.62; cost 12 + 4.80.
        Assert.Equal(3.62m, product.EstimatedFees);
        Assert.Equal(-0.63m, product.EstimatedProfit); // the default 20% margin doesn't cover eBay fees here
    }

    [Fact]
    public async Task Dashboard_FlagsListingsBelowMinimumProfit()
    {
        using var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx, host: "dropshipmall.co.uk");
        ctx.Listings.AddRange(
            new Listing { OwnerUserId = 1, Title = "Cheap", Price = 12m, SourcePrice = 10m, SourceUrl = "https://www.dropshipmall.co.uk/a" },
            new Listing { OwnerUserId = 1, Title = "Good", Price = 30m, SourcePrice = 10m, SourceUrl = "https://www.dropshipmall.co.uk/b" },
            new Listing { OwnerUserId = 1, Title = "No cost", Price = 30m });
        ctx.SaveChanges();
        var controller = new ListingsController(ctx,
            new Mock<IScraperService>(MockBehavior.Strict).Object, new Mock<IEbayService>(MockBehavior.Strict).Object,
            new Mock<IEdenSupplierSyncService>(MockBehavior.Strict).Object, Options.Create(new EbaySettings()),
            TestHelpers.User(role: AuthRoles.Lister), new SkuService(ctx, new SupplierCatalogService(ctx)));

        var listings = Assert.IsType<List<ListingDto>>(Assert.IsType<OkObjectResult>((await controller.GetListings()).Result).Value);

        Assert.True(listings.Single(l => l.Title == "Cheap").BelowMinProfit);   // 12 - fees - 12 cost < £1
        Assert.False(listings.Single(l => l.Title == "Good").BelowMinProfit);
        var noCost = listings.Single(l => l.Title == "No cost");
        Assert.Null(noCost.EstimatedProfit);
        Assert.False(noCost.BelowMinProfit);
    }
}

/// <summary>eBay UK rate card: tiers, category-tree lookup, small-order fees, loading the rates.</summary>
public class FeeRateCardTests
{
    private static MarketplaceFeeSettingsDto WithRateCard()
    {
        var settings = new MarketplaceFeeSettingsDto();
        settings.CategoryRates.AddRange(EbayUkRateCard.Rates().Select(r => new MarketplaceFeeCategoryRateDto
        {
            CategoryId = r.CategoryId, CategoryName = r.CategoryName, FinalValueFeePercent = r.FinalValueFeePercent,
            Threshold1 = r.Threshold1, Percent2 = r.Percent2, Threshold2 = r.Threshold2, Percent3 = r.Percent3, PerOrderFeeSmall = r.PerOrderFeeSmall
        }));
        return settings;
    }

    [Theory]
    [InlineData(400.0, 47.60)]    // all at 11.9%
    [InlineData(600.0, 67.40)]    // 500 × 11.9% + 100 × 7.9%
    public void HomeFurnitureDiy_IsTiered(decimal total, decimal expectedFvf)
        => Assert.Equal(expectedFvf, FeeMath.Estimate(total, null, null, null, WithRateCard(), "11700").FinalValueFee);

    [Fact]
    public void Furniture_HasThreeTiers()
    {
        // 500 × 10.9% + 500 × 7.9% + 500 × 3%
        Assert.Equal(109.00m, FeeMath.Estimate(1500m, null, null, null, WithRateCard(), "3197").FinalValueFee);
    }

    [Fact]
    public void SubCategory_UsesTheNearestParentWithARate()
    {
        // e.g. Candles (46782) → Home Fragrance (…) → Home Décor (10033) → Home, Furniture & DIY (11700)
        var parents = new Dictionary<string, string?> { ["46782"] = "1000", ["1000"] = "10033", ["10033"] = "11700", ["11700"] = null };
        var chain = Server.Services.EbayCategoryService.Chain("46782", parents);
        Assert.Equal(new[] { "46782", "1000", "10033", "11700" }, chain);

        var e = FeeMath.Estimate(20m, null, null, null, WithRateCard(), chain);
        Assert.Equal(11.9m, e.FinalValueFeePercent);
        Assert.StartsWith("Home, Furniture & DIY", e.RateDescription);
    }

    [Fact]
    public void SmallOrderFee_IsInheritedFromTheParent()
    {
        // Furniture has its own rate but sits under Home, Furniture & DIY, whose small orders pay 10p.
        var chain = new[] { "3197", "11700" };
        Assert.Equal(0.10m, FeeMath.Estimate(8m, null, null, null, WithRateCard(), chain).PerOrderFee);
        Assert.Equal(0.40m, FeeMath.Estimate(12m, null, null, null, WithRateCard(), chain).PerOrderFee);
        Assert.Equal(0.30m, FeeMath.Estimate(8m, null, null, null, WithRateCard(), "159912").PerOrderFee); // Garden & Patio
    }

    [Fact]
    public void Supplier_FeeCategory_IsUsedWhenTheListingHasNoCategory()
    {
        var eden = new SupplierDto { PricesIncludeVat = false, VatRatePercent = 20m, FeeCategoryId = "159912" };
        var e = FeeMath.EstimateSale(20m, null, true, 10m, null, eden, WithRateCard());
        Assert.Equal(10.9m, e.FinalValueFeePercent);
    }

    [Fact]
    public void Chain_StopsOnLoops_AndUnknownCategories()
    {
        var parents = new Dictionary<string, string?> { ["a"] = "b", ["b"] = "a" };
        Assert.Equal(new[] { "a", "b" }, Server.Services.EbayCategoryService.Chain("a", parents));
        Assert.Equal(new[] { "x" }, Server.Services.EbayCategoryService.Chain("x", parents));
    }

    [Fact]
    public async Task LoadRateCard_UpdatesEbayRates_AndKeepsYourOwn()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.MarketplaceFeeCategoryRates.Add(new MarketplaceFeeCategoryRate { Marketplace = "eBay", CategoryId = "159912", CategoryName = "Garden", FinalValueFeePercent = 99m });
        ctx.MarketplaceFeeCategoryRates.Add(new MarketplaceFeeCategoryRate { Marketplace = "eBay", CategoryId = "123456", CategoryName = "Mine", FinalValueFeePercent = 8m });
        ctx.SaveChanges();
        var service = new MarketplaceFeeService(ctx);

        var (added, updated) = await service.LoadUkRateCardAsync();

        Assert.Equal(1, updated);
        Assert.Equal(EbayUkRateCard.Rates().Count - 1, added);
        Assert.Equal(10.9m, ctx.MarketplaceFeeCategoryRates.Single(r => r.CategoryId == "159912").FinalValueFeePercent);
        Assert.Equal(8m, ctx.MarketplaceFeeCategoryRates.Single(r => r.CategoryId == "123456").FinalValueFeePercent);
    }

    [Fact]
    public async Task CategoryRate_TiersMustBeComplete()
    {
        using var ctx = TestHelpers.NewContext();
        var error = await new MarketplaceFeeService(ctx).SaveCategoryRateAsync("eBay",
            new MarketplaceFeeCategoryRateDto { CategoryId = "11700", FinalValueFeePercent = 11.9m, Threshold1 = 500m });
        Assert.NotNull(error);
    }

    [Fact]
    public void RateCard_HasUniqueCategoryIds()
    {
        var ids = EbayUkRateCard.Rates().Select(r => r.CategoryId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
