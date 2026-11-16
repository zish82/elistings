using Moq;
using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

public class ListingVariantTests
{
    private static ListingVariantDto V(string size, decimal price = 9.99m, int qty = 5, string? sku = null)
        => new() { Sku = sku, Options = new() { ["Size"] = size }, Price = price, Quantity = qty };

    [Fact]
    public void Validate_AcceptsNoneOrTwoPlus_RejectsOne()
    {
        Assert.Null(ListingVariants.Validate(new List<ListingVariantDto>()));
        Assert.Null(ListingVariants.Validate(new List<ListingVariantDto> { V("50"), V("100") }));
        Assert.NotNull(ListingVariants.Validate(new List<ListingVariantDto> { V("50") }));
    }

    [Fact]
    public void Validate_RejectsDuplicateOrMissingValues()
    {
        Assert.NotNull(ListingVariants.Validate(new List<ListingVariantDto> { V("50"), V("50") }));
        Assert.NotNull(ListingVariants.Validate(new List<ListingVariantDto> { V("50"), V(" ") }));
        Assert.NotNull(ListingVariants.Validate(new List<ListingVariantDto> { V("50"), new ListingVariantDto { Options = new() { ["Colour"] = "Red" }, Price = 1m } }));
    }

    [Fact]
    public async Task Prepare_CarriesVariantsWithDeliveryFolded_AndPublishStoresOfferIds()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.Users.Add(new AppUser { Id = 3, Email = "l@x.com", Role = AuthRoles.Lister });
        ctx.EbayTokens.Add(new EbayTokenInfo { Id = 10, UserId = 3, Name = "A", IsDefault = true, AccessToken = "t", RefreshToken = "r", ExpiryTime = DateTime.UtcNow.AddHours(1), RefreshTokenExpiryTime = DateTime.UtcNow.AddMonths(6) });
        var listing = new Listing
        {
            OwnerUserId = 3, EbayAccountId = 10, Title = "Lights", Price = 0.99m, Sku = "LGT", CategoryId = "1", DeliveryCost = 2m, IncludeDeliveryInPrice = true,
            PaymentPolicyId = "p", FulfillmentPolicyId = "f", ReturnPolicyId = "r", Marketplace = "eBay", ImageUrlsJson = "[\"https://x/a.jpg\"]"
        };
        ctx.Listings.Add(listing);
        ctx.SaveChanges();
        ctx.ListingVariants.AddRange(
            new ListingVariant { ListingId = listing.Id, Sku = "LGT-V1", OptionsJson = "{\"Size\":\"50\"}", Price = 5m, Quantity = 3 },
            new ListingVariant { ListingId = listing.Id, Sku = "LGT-V2", OptionsJson = "{\"Size\":\"100\"}", Price = 8m, Quantity = 4 });
        ctx.SaveChanges();

        ListingDto? sent = null;
        var ebay = new Mock<IEbayService>();
        ebay.Setup(e => e.CreateListingAsync(It.IsAny<ListingDto>())).Callback<ListingDto>(d => sent = d)
            .ReturnsAsync(new EbayListingResponse { Sku = "LGT", OfferId = "o1", ListingId = "item1", VariantOfferIds = new() { ["LGT-V1"] = "o1", ["LGT-V2"] = "o2" } });
        var catalog = new SupplierCatalogService(ctx);
        var publisher = new ListingPublisher(ctx, ebay.Object, new Server.Configuration.EbaySettings(), new SkuService(ctx, catalog), null);

        await publisher.PublishAsync(listing);

        Assert.NotNull(sent);
        Assert.Equal(new[] { 7m, 10m }, sent!.Variants.Select(v => v.Price)); // delivery £2 folded into each price
        Assert.Equal("o2", ctx.ListingVariants.Single(v => v.Sku == "LGT-V2").OfferId);
    }

    [Fact]
    public async Task OneVariant_CannotBePublished()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.Users.Add(new AppUser { Id = 3, Email = "l@x.com", Role = AuthRoles.Lister });
        ctx.EbayTokens.Add(new EbayTokenInfo { Id = 10, UserId = 3, Name = "A", IsDefault = true, AccessToken = "t", RefreshToken = "r", ExpiryTime = DateTime.UtcNow.AddHours(1), RefreshTokenExpiryTime = DateTime.UtcNow.AddMonths(6) });
        var listing = new Listing
        {
            OwnerUserId = 3, EbayAccountId = 10, Title = "Lights", Price = 5m, Sku = "LGT", CategoryId = "1",
            PaymentPolicyId = "p", FulfillmentPolicyId = "f", ReturnPolicyId = "r", Marketplace = "eBay", ImageUrlsJson = "[\"https://x/a.jpg\"]"
        };
        ctx.Listings.Add(listing);
        ctx.SaveChanges();
        ctx.ListingVariants.Add(new ListingVariant { ListingId = listing.Id, Sku = "LGT-V1", OptionsJson = "{\"Size\":\"50\"}", Price = 5m });
        ctx.SaveChanges();
        var publisher = new ListingPublisher(ctx, new Mock<IEbayService>().Object, new Server.Configuration.EbaySettings(), new SkuService(ctx, new SupplierCatalogService(ctx)), null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.PublishAsync(listing));
    }
}

public class VariationFromProductsTests
{
    private static (AppDbContext Ctx, Server.Controllers.SupplierProductsController Controller) Setup()
    {
        var ctx = TestHelpers.NewContext();
        ctx.Users.Add(new AppUser { Id = 1, Email = "a@x.com", Role = AuthRoles.Admin });
        TestHelpers.AddSupplier(ctx);
        ctx.SaveChanges();
        var catalog = new SupplierCatalogService(ctx);
        var controller = new Server.Controllers.SupplierProductsController(ctx, TestHelpers.User(), new Mock<IFeedProductSyncService>().Object,
            Microsoft.Extensions.Options.Options.Create(new Server.Configuration.EbaySettings()),
            new ProductEnrichmentService(ctx, Array.Empty<ISupplierProductEnricher>(), catalog),
            new Server.Services.Jobs.BulkJobService(ctx, new Mock<Hangfire.IBackgroundJobClient>().Object), new SkuService(ctx, catalog));
        return (ctx, controller);
    }

    private static SupplierProduct Product(string sku, string variant, decimal price, int stock = 5) => new()
    {
        Supplier = "DropShipMall", Title = "LED Lights", SupplierSku = sku, VariantName = variant, SupplierPrice = price, StockLevel = stock,
        GroupKey = "led", VariantOptionsJson = "{\"Size\":\"" + variant + "\"}", IsActive = true
    };

    [Fact]
    public async Task SelectedProducts_BecomeOneListingWithAVariationEach_AndShowAsListed()
    {
        var (ctx, controller) = Setup();
        using var _ = ctx;
        var a = Product("A1", "50 LEDs", 3m); var b = Product("A2", "100 LEDs", 5m);
        ctx.SupplierProducts.AddRange(a, b);
        ctx.SaveChanges();

        var result = await controller.CreateVariationListing(new CreateVariationListingRequest { ProductIds = new() { a.Id, b.Id } });

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(result.Result);
        var added = Assert.IsType<AddSupplierProductsResult>(ok.Value);
        Assert.Single(added.AddedListingIds);
        var variants = ctx.ListingVariants.OrderBy(v => v.SortOrder).ToList();
        Assert.Equal(2, variants.Count);
        Assert.Equal(new[] { a.Id, b.Id }, variants.Select(v => v.SupplierProductId!.Value));
        Assert.Contains("50 LEDs", variants[0].OptionsJson);
        Assert.Equal(4.99m, variants[0].Price); // 3 + VAT = 3.60, +20% = 4.32 → 4.99

        // Both products now show as part of the variation listing.
        var list = await controller.GetProducts(new SupplierProductQuery { Supplier = "DropShipMall" });
        var items = Assert.IsType<List<SupplierProductListItemDto>>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(list.Result).Value);
        Assert.All(items, i => { Assert.True(i.AlreadyAdded); Assert.True(i.InVariationListing); Assert.Equal(added.AddedListingIds[0], i.ExistingListingId); });

        var detail = await controller.GetProduct(b.Id);
        var dto = Assert.IsType<SupplierProductDetailDto>(Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(detail.Result).Value);
        Assert.Equal(2, dto.VariationListing!.Variations.Count);
    }

    [Fact]
    public async Task NeedsTwoProducts_AndRefusesOnesAlreadyListed()
    {
        var (ctx, controller) = Setup();
        using var _ = ctx;
        var a = Product("A1", "50", 3m); var b = Product("A2", "100", 5m);
        ctx.SupplierProducts.AddRange(a, b);
        ctx.SaveChanges();

        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>((await controller.CreateVariationListing(new CreateVariationListingRequest { ProductIds = new() { a.Id } })).Result);

        Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>((await controller.CreateVariationListing(new CreateVariationListingRequest { ProductIds = new() { a.Id, b.Id } })).Result);
        Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>((await controller.CreateVariationListing(new CreateVariationListingRequest { ProductIds = new() { a.Id, b.Id } })).Result);
    }
}

public class MarketplaceLinkTests
{
    [Theory]
    [InlineData("eBay", "1234567890", false, "https://www.ebay.co.uk/itm/1234567890")]
    [InlineData("eBay", "1234567890", true, "https://www.sandbox.ebay.co.uk/itm/1234567890")]
    [InlineData("eBay", null, false, null)]
    [InlineData("eBay", "LISTING-SUCCESS-o1", false, null)]
    [InlineData("Etsy", "1", false, null)]
    public void ListingUrl_OnlyForRealEbayItemNumbers(string marketplace, string? id, bool sandbox, string? expected)
        => Assert.Equal(expected, MarketplaceLinks.ListingUrl(marketplace, id, sandbox));
}
