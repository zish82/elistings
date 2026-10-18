using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>VAT, suggested price, listing total and default delivery option selection.</summary>
public class PricingAndDeliveryTests
{
    private static SupplierDto Supplier(bool pricesIncludeVat = false, params SupplierDeliveryOptionDto[] options) => new()
    {
        Code = "DropShipMall",
        PricesIncludeVat = pricesIncludeVat,
        VatRatePercent = 20m,
        DefaultMarginPercent = 20m,
        DeliveryOptions = options.ToList()
    };

    private static SupplierDeliveryOptionDto Option(int id, string name, decimal cost, string? categories = null, bool isDefault = false, bool active = true)
        => new() { Id = id, Name = name, Cost = cost, PostageCategories = categories, IsDefault = isDefault, IsActive = active };

    [Theory]
    [InlineData(10.00, false, 12.00)]
    [InlineData(10.00, true, 10.00)]
    [InlineData(4.99, false, 5.99)] // 5.988 rounds to 5.99
    public void AddVat_AddsVatOnlyWhenPriceExcludesIt(decimal amount, bool includesVat, decimal expected)
        => Assert.Equal(expected, PricingMath.AddVat(amount, includesVat, 20m));

    [Fact]
    public void CostIncVat_UsesSupplierVatSetting()
    {
        Assert.Equal(12.00m, PricingMath.CostIncVat(10m, Supplier(pricesIncludeVat: false)));
        Assert.Equal(10.00m, PricingMath.CostIncVat(10m, Supplier(pricesIncludeVat: true)));
        Assert.Null(PricingMath.CostIncVat(null, Supplier()));
    }

    [Theory]
    [InlineData(10.00, 20, 12.99)]  // 12.00 -> next .99
    [InlineData(12.50, 20, 15.99)]  // 15.00 -> 15.99
    [InlineData(8.33, 0, 8.99)]
    public void SuggestItemPrice_AddsMarginAndRoundsUpToNinetyNine(decimal cost, decimal margin, decimal expected)
        => Assert.Equal(expected, PricingMath.SuggestItemPrice(cost, margin));

    [Theory]
    [InlineData(null)]
    [InlineData(0.0)]
    public void SuggestItemPrice_NoCost_ReturnsNull(double? cost)
        => Assert.Null(PricingMath.SuggestItemPrice((decimal?)cost, 20m));

    [Fact]
    public void ListingTotal_IncludesDeliveryOnlyWhenRequested()
    {
        Assert.Equal(15.98m, PricingMath.ListingTotal(9.99m, 5.99m, includeDelivery: true));
        Assert.Equal(9.99m, PricingMath.ListingTotal(9.99m, 5.99m, includeDelivery: false));
        Assert.Equal(9.99m, PricingMath.ListingTotal(9.99m, null, includeDelivery: true));
    }

    [Fact]
    public void DeliveryIncVat_AddsVatToExVatCost()
    {
        var supplier = Supplier();
        Assert.Equal(4.80m, PricingMath.DeliveryIncVat(Option(1, "Parcel", 4m), supplier));
    }

    [Fact]
    public void SuggestDeliveryOption_PicksOptionMatchingPostageCategory()
    {
        var supplier = Supplier(false,
            Option(1, "Standard", 4.99m, "Standard Parcel,Small Parcel", isDefault: true),
            Option(2, "Fragile", 7.50m, "Fragile"));

        Assert.Equal(2, SupplierCatalogService.SuggestDeliveryOption(supplier, "fragile")!.Id);
        Assert.Equal(1, SupplierCatalogService.SuggestDeliveryOption(supplier, " Small Parcel ")!.Id);
    }

    [Fact]
    public void SuggestDeliveryOption_FallsBackToDefault_ThenFirstActive()
    {
        var withDefault = Supplier(false, Option(1, "Express", 9.95m), Option(2, "Standard", 4.99m, isDefault: true));
        Assert.Equal(2, SupplierCatalogService.SuggestDeliveryOption(withDefault, "Unknown class")!.Id);
        Assert.Equal(2, SupplierCatalogService.SuggestDeliveryOption(withDefault, null)!.Id);

        var noDefault = Supplier(false, Option(1, "Old", 3m, active: false), Option(2, "Parcel", 4m));
        Assert.Equal(2, SupplierCatalogService.SuggestDeliveryOption(noDefault, null)!.Id);
    }

    [Fact]
    public void SuggestDeliveryOption_IgnoresInactiveOptions_AndNullSupplier()
    {
        var supplier = Supplier(false, Option(1, "Fragile", 7.50m, "Fragile", active: false), Option(2, "Standard", 4.99m, isDefault: true));
        Assert.Equal(2, SupplierCatalogService.SuggestDeliveryOption(supplier, "Fragile")!.Id);
        Assert.Null(SupplierCatalogService.SuggestDeliveryOption(null, "Fragile"));
    }

    [Fact]
    public async Task FindAsync_MatchesByCodeOrWebsiteHost_AndSkipsInactive()
    {
        using var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx, "EdenHorticulture", host: "edenhorticulture.co.uk", skuPrefix: "EDN");
        var inactive = TestHelpers.AddSupplier(ctx, "OldSupplier", host: "old.example.com", skuPrefix: "OLD");
        inactive.IsActive = false;
        ctx.SaveChanges();
        var catalog = new SupplierCatalogService(ctx);

        Assert.Equal("EdenHorticulture", (await catalog.FindAsync("edenhorticulture", null))!.Code);
        Assert.Equal("EdenHorticulture", (await catalog.FindAsync(null, "https://www.edenhorticulture.co.uk/products/x"))!.Code);
        Assert.Null(await catalog.FindAsync("OldSupplier", "https://old.example.com/p/1"));
    }

    [Fact]
    public async Task GetAllForSettings_ListsPostageCategoriesFromProducts()
    {
        using var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx, "DropShipMall");
        ctx.SupplierProducts.AddRange(
            new SupplierProduct { Supplier = "DropShipMall", Title = "A", PostageCategory = "Fragile" },
            new SupplierProduct { Supplier = "DropShipMall", Title = "B", PostageCategory = "Standard Parcel" },
            new SupplierProduct { Supplier = "DropShipMall", Title = "C", PostageCategory = "Fragile" });
        ctx.SaveChanges();

        var supplier = (await new SupplierCatalogService(ctx).GetAllForSettingsAsync()).Single();

        Assert.Equal(new[] { "Fragile", "Standard Parcel" }, supplier.KnownPostageCategories);
    }
}
