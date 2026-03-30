using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using Server.Controllers;
using Server.Configuration;
using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

public class ListingsControllerTests
{
    private AppDbContext CreateContext(string dbName)
    {
        var opts = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        return new AppDbContext(opts);
    }

    [Fact]
    public async Task CreateListing_AppliesConfiguredDefaults_WhenRequestOmitsPolicies()
    {
        var ctx = CreateContext("create_defaults_db");

        var settings = Options.Create(new EbaySettings
        {
            DefaultPaymentPolicyId = "pay-123",
            DefaultFulfillmentPolicyId = "ship-456",
            DefaultReturnPolicyId = "ret-789"
        });

        var scraperMock = new Mock<ScraperService>(MockBehavior.Strict, (object)null!);
        var controller = new ListingsController(ctx, scraperMock.Object, settings);

        var req = new CreateListingRequest
        {
            Title = "Test",
            Description = "Desc",
            Price = 9.99m,
            ImageUrls = new List<string>()
        };

        var result = await controller.CreateListing(req);

        // Validate persisted listing used defaults
        var saved = ctx.Listings.FirstOrDefault();
        Assert.NotNull(saved);
        Assert.Equal("pay-123", saved.PaymentPolicyId);
        Assert.Equal("ship-456", saved.FulfillmentPolicyId);
        Assert.Equal("ret-789", saved.ReturnPolicyId);
    }

    [Fact]
    public async Task BulkPublish_UsesDefaultsAndPublishesListings()
    {
        var ctx = CreateContext("bulk_publish_db");

        // Seed a draft listing without policy IDs
        var listing = new Listing
        {
            Title = "BulkItem",
            Description = "D",
            Price = 5.00m,
            Status = "Draft",
            ImageUrlsJson = JsonSerializer.Serialize(new List<string>()),
        };
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        var settings = Options.Create(new EbaySettings
        {
            DefaultPaymentPolicyId = "pay-abc",
            DefaultFulfillmentPolicyId = "ship-def",
            DefaultReturnPolicyId = "ret-ghi"
        });

        var scraperMock = new Mock<ScraperService>(MockBehavior.Strict, (object)null!);

        var ebayMock = new Mock<IEbayService>();
        ebayMock.Setup(s => s.CreateListingAsync(It.IsAny<ListingDto>()))
               .ReturnsAsync(new EbayListingResponse { Sku = "SKU1", OfferId = "OF1", ListingId = "L1" });

        var controller = new ListingsController(ctx, scraperMock.Object, ebayMock.Object, settings);

        var ids = new List<int> { listing.Id };
        var response = await controller.BulkPublish(ids);

        // After publish, listing should be marked Published and have ebay fields
        var updated = await ctx.Listings.FindAsync(listing.Id);
        Assert.Equal("Published", updated.Status);
        Assert.Equal("L1", updated.EbayItemId);
        Assert.Equal("OF1", updated.EbayOfferId);
    }
}
