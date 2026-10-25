using System.Text.Json;
using Microsoft.Extensions.Options;
using Moq;
using Server.Configuration;
using Server.Controllers;
using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

public class ListingsControllerTests
{
    private static ListingsController Controller(AppDbContext ctx, EbaySettings? settings = null)
        => new(ctx,
               new Mock<IScraperService>(MockBehavior.Strict).Object,
               new Mock<IEbayService>(MockBehavior.Strict).Object,
               new Mock<IEdenSupplierSyncService>(MockBehavior.Strict).Object,
               Options.Create(settings ?? new EbaySettings()),
               TestHelpers.User(role: AuthRoles.Lister),
               new SkuService(ctx, new SupplierCatalogService(ctx)));

    [Fact]
    public async Task CreateListing_AppliesConfiguredDefaults_WhenRequestOmitsPolicies()
    {
        using var ctx = TestHelpers.NewContext();
        var controller = Controller(ctx, new EbaySettings
        {
            DefaultPaymentPolicyId = "pay-123",
            DefaultFulfillmentPolicyId = "ship-456",
            DefaultReturnPolicyId = "ret-789"
        });

        await controller.CreateListing(new CreateListingRequest { Title = "Test", Description = "Desc", Price = 9.99m });

        var saved = Assert.Single(ctx.Listings);
        Assert.Equal("pay-123", saved.PaymentPolicyId);
        Assert.Equal("ship-456", saved.FulfillmentPolicyId);
        Assert.Equal("ret-789", saved.ReturnPolicyId);
        Assert.Equal(1, saved.OwnerUserId);
    }

    [Fact]
    public async Task CreateListing_WithoutSku_GeneratesOne()
    {
        using var ctx = TestHelpers.NewContext();

        await Controller(ctx).CreateListing(new CreateListingRequest { Title = "Test", Price = 9.99m });

        var saved = Assert.Single(ctx.Listings);
        Assert.Equal($"MAN-L{saved.Id}", saved.Sku);
    }

    [Fact]
    public async Task CreateListing_StoresDeliveryAndOriginalTitle()
    {
        using var ctx = TestHelpers.NewContext();

        await Controller(ctx).CreateListing(new CreateListingRequest
        {
            Title = "Optimised title",
            SourceTitle = "Supplier title",
            Price = 9.99m,
            DeliveryOptionName = "Standard",
            DeliveryCost = 5.99m,
            IncludeDeliveryInPrice = false // not supported yet: always forced on
        });

        var saved = Assert.Single(ctx.Listings);
        Assert.Equal("Supplier title", saved.SourceTitle);
        Assert.Equal(5.99m, saved.DeliveryCost);
        Assert.True(saved.IncludeDeliveryInPrice);
    }

    [Fact]
    public async Task CreateListing_DuplicateSku_IsRejected()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.Listings.Add(new Listing { Title = "Existing", OwnerUserId = 1, Sku = "DSM-1" });
        ctx.SaveChanges();

        var result = await Controller(ctx).CreateListing(new CreateListingRequest { Title = "New", Price = 5m, Sku = "dsm-1" });

        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(result.Result);
        Assert.Single(ctx.Listings);
    }

    [Fact]
    public async Task UpdateListing_DoesNotOverwriteOriginalTitle()
    {
        using var ctx = TestHelpers.NewContext();
        var listing = new Listing { Title = "Old", OwnerUserId = 1, SourceTitle = "Supplier title", ImageUrlsJson = JsonSerializer.Serialize(new List<string>()) };
        ctx.Listings.Add(listing);
        ctx.SaveChanges();

        await Controller(ctx).UpdateListing(listing.Id, new CreateListingRequest { Title = "New", SourceTitle = "Something else", Price = 5m });

        Assert.Equal("New", listing.Title);
        Assert.Equal("Supplier title", listing.SourceTitle);
    }
}
