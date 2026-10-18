using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Server.Controllers;
using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>Delivery options on /suppliers (add, update, single default, validation, permissions).</summary>
public class SuppliersControllerTests
{
    private static SuppliersController Controller(AppDbContext ctx, string role = AuthRoles.Admin)
        => new(ctx, new SupplierCatalogService(ctx), TestHelpers.User(role: role));

    private static SupplierDeliveryOptionDto Option(string name = "Standard Parcel", decimal cost = 4m, bool isDefault = false)
        => new() { Name = name, Cost = cost, CostIncludesVat = false, IsDefault = isDefault, IsActive = true, SortOrder = 10 };

    [Fact]
    public async Task AddOption_SavesOption_AndReturnsItWithId()
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx, "EdenHorticulture");

        var result = await Controller(ctx).AddOption(supplier.Id, Option("Eden 48 hour Royal Mail", 4m));

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var dto = Assert.IsType<SupplierDeliveryOptionDto>(ok.Value);
        Assert.True(dto.Id > 0);
        var saved = Assert.Single(ctx.SupplierDeliveryOptions);
        Assert.Equal("Eden 48 hour Royal Mail", saved.Name);
        Assert.Equal(4m, saved.Cost);
        Assert.Equal(supplier.Id, saved.SupplierId);
    }

    [Fact]
    public async Task AddOption_TrimsTextFields_AndStoresBlankAsNull()
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx);
        var option = Option("  Standard Parcel  ");
        option.Description = "   ";
        option.PostageCategories = " Standard Parcel,Small Parcel ";

        await Controller(ctx).AddOption(supplier.Id, option);

        var saved = Assert.Single(ctx.SupplierDeliveryOptions);
        Assert.Equal("Standard Parcel", saved.Name);
        Assert.Null(saved.Description);
        Assert.Equal("Standard Parcel,Small Parcel", saved.PostageCategories);
    }

    [Theory]
    [InlineData("", 4)]
    [InlineData("   ", 4)]
    [InlineData("Parcel", -1)]
    public async Task AddOption_RejectsMissingNameOrNegativeCost(string name, decimal cost)
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx);

        var result = await Controller(ctx).AddOption(supplier.Id, Option(name, cost));

        Assert.IsType<BadRequestObjectResult>(result.Result);
        Assert.Empty(ctx.SupplierDeliveryOptions);
    }

    [Fact]
    public async Task AddOption_UnknownSupplier_ReturnsNotFound()
    {
        using var ctx = TestHelpers.NewContext();

        var result = await Controller(ctx).AddOption(999, Option());

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task AddOption_AsLister_IsForbiddenWithMessage()
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx);

        var result = await Controller(ctx, AuthRoles.Lister).AddOption(supplier.Id, Option());

        var forbidden = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        Assert.Equal(SuppliersController.OnlyAdminsMessage, forbidden.Value);
        Assert.Empty(ctx.SupplierDeliveryOptions);
    }

    [Fact]
    public async Task AddOption_AsDefault_ClearsPreviousDefault()
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx);
        var controller = Controller(ctx);
        await controller.AddOption(supplier.Id, Option("Standard", 4.99m, isDefault: true));

        await controller.AddOption(supplier.Id, Option("Express", 9.95m, isDefault: true));

        var defaults = ctx.SupplierDeliveryOptions.Where(o => o.IsDefault).Select(o => o.Name).ToList();
        Assert.Equal(new[] { "Express" }, defaults);
    }

    [Fact]
    public async Task UpdateOption_ChangesValues()
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx);
        var controller = Controller(ctx);
        var created = (SupplierDeliveryOptionDto)((OkObjectResult)(await controller.AddOption(supplier.Id, Option("Parcel", 8m))).Result!).Value!;

        created.Cost = 4m;
        created.Description = "Portal rate";
        var result = await controller.UpdateOption(created.Id, created);

        Assert.IsType<NoContentResult>(result);
        var saved = Assert.Single(ctx.SupplierDeliveryOptions);
        Assert.Equal(4m, saved.Cost);
        Assert.Equal("Portal rate", saved.Description);
    }

    [Fact]
    public async Task UpdateOption_UnknownId_ReturnsNotFound()
    {
        using var ctx = TestHelpers.NewContext();

        var result = await Controller(ctx).UpdateOption(42, Option());

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task DeleteOption_RemovesIt()
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx);
        var controller = Controller(ctx);
        var created = (SupplierDeliveryOptionDto)((OkObjectResult)(await controller.AddOption(supplier.Id, Option())).Result!).Value!;

        var result = await controller.DeleteOption(created.Id);

        Assert.IsType<NoContentResult>(result);
        Assert.Empty(ctx.SupplierDeliveryOptions);
    }

    [Fact]
    public async Task UpdateSupplier_NormalisesSkuPrefixAndHost()
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx, "EdenHorticulture", pricesIncludeVat: true);
        var dto = SupplierCatalogService.ToDto(supplier, Array.Empty<SupplierDeliveryOption>());
        dto.SkuPrefix = " edn ";
        dto.WebsiteHost = "WWW.EdenHorticulture.co.uk";
        dto.PricesIncludeVat = false;

        var result = await Controller(ctx).Update(supplier.Id, dto);

        Assert.IsType<NoContentResult>(result);
        var saved = ctx.Suppliers.Single();
        Assert.Equal("EDN", saved.SkuPrefix);
        Assert.Equal("edenhorticulture.co.uk", saved.WebsiteHost);
        Assert.False(saved.PricesIncludeVat);
    }
}
