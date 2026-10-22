using Hangfire;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
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

/// <summary>Per-user supplier access: Admins see all; Managers and Listers only their ticked suppliers.</summary>
public class SupplierAccessTests
{
    private static AppDbContext Seed()
    {
        var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx, "DropShipMall", skuPrefix: "DSM", host: "dropshipmall.co.uk");
        TestHelpers.AddSupplier(ctx, "EdenHorticulture", skuPrefix: "EDN", host: "edenhorticulture.co.uk");
        ctx.SupplierProducts.AddRange(
            new SupplierProduct { Supplier = "DropShipMall", Title = "Candle", CategoryName = "Candles" },
            new SupplierProduct { Supplier = "EdenHorticulture", Title = "Nutrient", CategoryName = "Nutrients" });
        ctx.SupplierFeeds.AddRange(
            new SupplierFeed { Supplier = "DropShipMall", FeedType = "Stock Feed", Url = "https://dsm/stock" },
            new SupplierFeed { Supplier = "EdenHorticulture", FeedType = "Shopify Catalogue", Url = "https://eden/products.json" });
        // User 1 may only see DropShipMall.
        ctx.UserSupplierAccess.Add(new UserSupplierAccess { UserId = 1, SupplierCode = "DropShipMall" });
        ctx.SaveChanges();
        return ctx;
    }

    private static SupplierProductsController Products(AppDbContext ctx, string role)
    {
        var catalog = new SupplierCatalogService(ctx);
        return new SupplierProductsController(ctx, TestHelpers.User(userId: 1, role: role), new Mock<IFeedProductSyncService>().Object,
            Options.Create(new EbaySettings()), new ProductEnrichmentService(ctx, Array.Empty<ISupplierProductEnricher>(), catalog),
            new BulkJobService(ctx, new Mock<IBackgroundJobClient>().Object), new SkuService(ctx, catalog));
    }

    private static List<SupplierProductListItemDto> ProductList(ActionResult<List<SupplierProductListItemDto>> result)
        => Assert.IsType<List<SupplierProductListItemDto>>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Theory]
    [InlineData(AuthRoles.Manager)]
    [InlineData(AuthRoles.Lister)]
    public async Task ManagersAndListers_OnlySeeTheirSuppliersProducts(string role)
    {
        using var ctx = Seed();
        var all = ProductList(await Products(ctx, role).GetProducts(new SupplierProductQuery()));
        Assert.Equal(new[] { "DropShipMall" }, all.Select(p => p.Supplier).Distinct());
        Assert.Empty(ProductList(await Products(ctx, role).GetProducts(new SupplierProductQuery { Supplier = "EdenHorticulture" })));
    }

    [Fact]
    public async Task Admin_SeesEverySupplier()
    {
        using var ctx = Seed();
        var all = ProductList(await Products(ctx, AuthRoles.Admin).GetProducts(new SupplierProductQuery()));
        Assert.Equal(2, all.Select(p => p.Supplier).Distinct().Count());
    }

    [Fact]
    public async Task Feeds_AreFilteredToTheUsersSuppliers()
    {
        using var ctx = Seed();
        var controller = new SupplierFeedsController(ctx, TestHelpers.User(userId: 1, role: AuthRoles.Manager));
        var feeds = Assert.IsType<List<SupplierFeedDto>>(Assert.IsType<OkObjectResult>((await controller.GetAll()).Result).Value);
        Assert.Equal(new[] { "DropShipMall" }, feeds.Select(f => f.Supplier));
    }

    [Fact]
    public async Task Manager_CantChangeASupplierTheyCantSee()
    {
        using var ctx = Seed();
        var eden = ctx.Suppliers.Single(s => s.Code == "EdenHorticulture");
        var controller = new SuppliersController(ctx, new SupplierCatalogService(ctx), TestHelpers.User(userId: 1, role: AuthRoles.Manager));

        var result = await controller.Update(eden.Id, new SupplierDto { Name = "Renamed", VatRatePercent = 20m });

        Assert.IsType<NotFoundResult>(result);
        var suppliers = Assert.IsType<List<SupplierDto>>(Assert.IsType<OkObjectResult>((await controller.GetAll(includeInactive: true)).Result).Value);
        Assert.Equal(new[] { "DropShipMall" }, suppliers.Select(s => s.Code));
    }

    [Fact]
    public async Task ExtractingFromASupplierWebsite_NeedsAccess()
    {
        using var ctx = Seed();
        var access = new SupplierAccessService(ctx, TestHelpers.User(userId: 1, role: AuthRoles.Lister));

        Assert.Equal("EdenHorticulture", await access.SupplierCodeForUrlAsync("https://www.edenhorticulture.co.uk/products/x"));
        Assert.False(await access.IsAllowedAsync("EdenHorticulture"));
        Assert.True(await access.IsAllowedAsync("DropShipMall"));
        Assert.True(await access.IsAllowedAsync(null)); // not a supplier
    }

    [Fact]
    public async Task SetForUser_ReplacesTheList_AndIgnoresUnknownSuppliers()
    {
        using var ctx = Seed();
        ctx.Users.Add(new AppUser { Id = 7, Email = "lister@example.com", Role = AuthRoles.Lister });
        ctx.SaveChanges();
        var access = new SupplierAccessService(ctx, TestHelpers.User());

        await access.SetForUserAsync(7, new[] { "edenhorticulture", "Nope" });

        Assert.Equal(new[] { "EdenHorticulture" }, ctx.UserSupplierAccess.Where(a => a.UserId == 7).Select(a => a.SupplierCode));
        Assert.True(ctx.Users.Single(u => u.Id == 7).SupplierAccessConfigured);
    }

    [Fact]
    public void ExistingUsers_GetEverySupplierOnce()
    {
        using var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx, "DropShipMall");
        ctx.Users.AddRange(
            new AppUser { Id = 2, Email = "manager@example.com", Role = AuthRoles.Manager },
            new AppUser { Id = 3, Email = "admin@example.com", Role = AuthRoles.Admin });
        ctx.SaveChanges();
        var config = new ConfigurationBuilder().Build();

        SeedData.Ensure(ctx, config, hangfireIsPersistent: true);

        var managerSuppliers = ctx.UserSupplierAccess.Where(a => a.UserId == 2).Select(a => a.SupplierCode).ToList();
        Assert.Contains("DropShipMall", managerSuppliers);
        Assert.Empty(ctx.UserSupplierAccess.Where(a => a.UserId == 3)); // Admins don't need rows

        // Once done, removing a supplier from the manager sticks (no re-grant on the next start).
        ctx.UserSupplierAccess.RemoveRange(ctx.UserSupplierAccess.Where(a => a.UserId == 2));
        ctx.SaveChanges();
        SeedData.Ensure(ctx, config, hangfireIsPersistent: true);
        Assert.Empty(ctx.UserSupplierAccess.Where(a => a.UserId == 2));
    }
}
