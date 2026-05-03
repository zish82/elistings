using System.Text.Json;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
    private static ICurrentUserService CreateCurrentUserService(int userId = 1, bool canViewAllListings = false)
    {
        var currentUser = new Mock<ICurrentUserService>();
        currentUser.SetupGet(x => x.IsAuthenticated).Returns(true);
        currentUser.SetupGet(x => x.UserId).Returns(userId);
        currentUser.SetupGet(x => x.Email).Returns("tester@example.com");
        currentUser.SetupGet(x => x.Role).Returns(AuthRoles.Lister);
        currentUser.SetupGet(x => x.CanManageUsers).Returns(false);
        currentUser.SetupGet(x => x.CanViewAllListings).Returns(canViewAllListings);
        currentUser.Setup(x => x.GetCurrentUserDto()).Returns(new CurrentUserDto
        {
            Id = userId,
            Email = "tester@example.com",
            Role = AuthRoles.Lister,
            CanManageUsers = false,
            CanViewAllListings = canViewAllListings
        });
        return currentUser.Object;
    }

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

        var scraperMock = new Mock<IScraperService>(MockBehavior.Strict);
        var ebayMock = new Mock<IEbayService>(MockBehavior.Strict);
        var controller = new ListingsController(ctx, scraperMock.Object, ebayMock.Object, settings, CreateCurrentUserService());

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
        Assert.Equal(1, saved.OwnerUserId);
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
            OwnerUserId = 1,
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

         var scraperMock = new Mock<IScraperService>(MockBehavior.Strict);

        var ebayMock = new Mock<IEbayService>();
        ebayMock.Setup(s => s.CreateListingAsync(It.IsAny<ListingDto>()))
               .ReturnsAsync(new EbayListingResponse { Sku = "SKU1", OfferId = "OF1", ListingId = "L1" });

         var controller = new ListingsController(ctx, scraperMock.Object, ebayMock.Object, settings, CreateCurrentUserService());

        var ids = new List<int> { listing.Id };
        var response = await controller.BulkPublish(ids);

        // After publish, listing should be marked Published and have ebay fields
        var updated = await ctx.Listings.FindAsync(listing.Id);
        Assert.NotNull(updated);
        Assert.Equal("Published", updated.Status);
        Assert.Equal("L1", updated.EbayItemId);
        Assert.Equal("OF1", updated.EbayOfferId);
    }
}
