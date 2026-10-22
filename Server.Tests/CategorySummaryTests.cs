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

/// <summary>Products page "Categories" view: counts per category/tag and the "In stock only" filter.</summary>
public class CategorySummaryTests
{
    private static SupplierProductsController Controller(AppDbContext ctx, string role = AuthRoles.Admin)
    {
        var catalog = new SupplierCatalogService(ctx);
        return new SupplierProductsController(
            ctx,
            TestHelpers.User(role: role),
            new Mock<IFeedProductSyncService>().Object,
            Options.Create(new EbaySettings()),
            new ProductEnrichmentService(ctx, Array.Empty<ISupplierProductEnricher>(), catalog),
            new BulkJobService(ctx, new Mock<IBackgroundJobClient>().Object),
            new SkuService(ctx, catalog));
    }

    /// <summary>
    /// Candles: in stock (5), out of stock (0), unknown (null). Ribbon: out of stock only.
    /// Tags: "Pillar" on the in-stock and out-of-stock candle; the unknown candle and ribbon have none.
    /// </summary>
    private static AppDbContext Seed()
    {
        var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx);
        var inStock = new SupplierProduct { Supplier = "DropShipMall", Title = "Candle A", CategoryName = "Candles", StockLevel = 5 };
        var outOfStock = new SupplierProduct { Supplier = "DropShipMall", Title = "Candle B", CategoryName = "Candles", StockLevel = 0 };
        var unknown = new SupplierProduct { Supplier = "DropShipMall", Title = "Candle C", CategoryName = "Candles", StockLevel = null };
        var ribbon = new SupplierProduct { Supplier = "DropShipMall", Title = "Ribbon", CategoryName = "Ribbon", StockLevel = 0 };
        ctx.SupplierProducts.AddRange(inStock, outOfStock, unknown, ribbon);
        var tag = new ProductTag { Supplier = "DropShipMall", Name = "Pillar", NormalizedName = "pillar", Type = ProductTagTypes.Type };
        ctx.ProductTags.Add(tag);
        ctx.SaveChanges();
        ctx.SupplierProductTags.AddRange(
            new SupplierProductTag { SupplierProductId = inStock.Id, ProductTagId = tag.Id },
            new SupplierProductTag { SupplierProductId = outOfStock.Id, ProductTagId = tag.Id });
        ctx.SaveChanges();
        return ctx;
    }

    private static List<SupplierCategorySummaryDto> Rows(ActionResult<List<SupplierCategorySummaryDto>> result)
        => Assert.IsType<List<SupplierCategorySummaryDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task SupplierCategories_CountAllProducts_ByDefault()
    {
        using var ctx = Seed();
        var rows = Rows(await Controller(ctx).GetCategorySummary("DropShipMall", ProductGrouping.SupplierCategory));

        var candles = rows.Single(r => r.Category == "Candles");
        Assert.Equal(3, candles.Products);
        Assert.Equal(1, candles.InStock);
        Assert.Equal(1, rows.Single(r => r.Category == "Ribbon").Products);
    }

    [Fact]
    public async Task SupplierCategories_InStockOnly_CountsStockedProductsAndDropsEmptyCategories()
    {
        using var ctx = Seed();
        var rows = Rows(await Controller(ctx).GetCategorySummary("DropShipMall", ProductGrouping.SupplierCategory, inStockOnly: true));

        var candles = Assert.Single(rows);          // Ribbon has nothing in stock
        Assert.Equal("Candles", candles.Category);
        Assert.Equal(1, candles.Products);          // unknown stock (null) doesn't count as in stock
        Assert.Equal(1, candles.InStock);
    }

    [Fact]
    public async Task Tags_InStockOnly_AppliesToTagsAndToUntagged()
    {
        using var ctx = Seed();

        var all = Rows(await Controller(ctx).GetCategorySummary("DropShipMall", ProductGrouping.Tag));
        Assert.Equal(2, all.Single(r => r.Label == "Pillar").Products);
        Assert.Equal(2, all.Single(r => r.Id == 0).Products); // unknown candle + ribbon

        var stocked = Rows(await Controller(ctx).GetCategorySummary("DropShipMall", ProductGrouping.Tag, inStockOnly: true));
        var pillar = Assert.Single(stocked);        // no in-stock untagged products, so no "(No tag)" row
        Assert.Equal("Pillar", pillar.Label);
        Assert.Equal(1, pillar.Products);
    }

    [Fact]
    public async Task ExpandedCategory_InStockOnly_ListsOnlyStockedProducts()
    {
        using var ctx = Seed();
        var result = await Controller(ctx).GetProducts(new SupplierProductQuery
        {
            Supplier = "DropShipMall", Category = "Candles", InStockOnly = true, Sort = SupplierProductSort.PriceHighToLow
        });

        var products = Assert.IsType<List<SupplierProductListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Candle A", Assert.Single(products).Title);
    }

    [Fact]
    public async Task DisabledSupplier_IsHiddenFromListers_ButNotFromManagersAndAdmins()
    {
        using var ctx = Seed();
        ctx.Suppliers.Single(s => s.Code == "DropShipMall").IsActive = false;
        ctx.UserSupplierAccess.Add(new UserSupplierAccess { UserId = 1, SupplierCode = "DropShipMall" }); // test user 1
        ctx.SaveChanges();

        Assert.Empty(Rows(await Controller(ctx, AuthRoles.Lister).GetCategorySummary("DropShipMall", ProductGrouping.SupplierCategory)));
        Assert.NotEmpty(Rows(await Controller(ctx, AuthRoles.Manager).GetCategorySummary("DropShipMall", ProductGrouping.SupplierCategory)));
        Assert.NotEmpty(Rows(await Controller(ctx, AuthRoles.Admin).GetCategorySummary("DropShipMall", ProductGrouping.SupplierCategory)));
    }
}
