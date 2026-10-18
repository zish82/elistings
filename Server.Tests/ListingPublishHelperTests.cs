using System.Text.Json;
using Moq;
using Server.Configuration;
using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>Pre-publish checks and the price/category/specifics handed to eBay.</summary>
public class ListingPublishHelperTests
{
    private static Listing ValidListing() => new()
    {
        Id = 1,
        Title = "Ivory Church Pillar Candle 15cm x 8cm",
        Price = 9.99m,
        DeliveryCost = 4.80m,
        IncludeDeliveryInPrice = true,
        OwnerUserId = 1,
        CategoryId = "46782",
        PaymentPolicyId = "pay",
        FulfillmentPolicyId = "ship",
        ReturnPolicyId = "ret",
        ImageUrlsJson = JsonSerializer.Serialize(new List<string> { "https://x/a.jpg" }),
        ItemSpecificsJson = "{\"Material\":\"Wax\"}",
        Marketplace = "eBay"
    };

    private static async Task<ListingDto> Prepare(Listing listing, Mock<IEbayService>? ebay = null)
    {
        using var ctx = TestHelpers.NewContext();
        ctx.Listings.Add(listing);
        ctx.SaveChanges();
        return await ListingPublishHelper.PrepareAsync(listing, ctx, (ebay ?? new Mock<IEbayService>()).Object, new EbaySettings(),
            new SkuService(ctx, new SupplierCatalogService(ctx)));
    }

    [Fact]
    public async Task Prepare_SendsItemPlusDeliveryAsMarketplacePrice()
    {
        var dto = await Prepare(ValidListing());

        Assert.Equal(14.79m, dto.Price);
        Assert.Null(dto.DeliveryCost);
        Assert.Equal("Wax", dto.ItemSpecifics["Material"]);
    }

    [Fact]
    public async Task Prepare_GeneratesSkuForDraftsWithout()
    {
        var listing = ValidListing();
        await Prepare(listing);
        Assert.Equal($"MAN-L{listing.Id}", listing.Sku);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("longtitle")]
    [InlineData("price")]
    [InlineData("images")]
    [InlineData("policy")]
    [InlineData("marketplace")]
    public async Task Prepare_RejectsListingsThatEbayWouldReject(string problem)
    {
        var listing = ValidListing();
        switch (problem)
        {
            case "title": listing.Title = " "; break;
            case "longtitle": listing.Title = new string('a', 81); break;
            case "price": listing.Price = 0; break;
            case "images": listing.ImageUrlsJson = "[]"; break;
            case "policy": listing.PaymentPolicyId = null; break;
            case "marketplace": listing.Marketplace = "Amazon"; break;
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(listing));
        Assert.StartsWith("Cannot publish", ex.Message);
    }

    [Fact]
    public async Task Prepare_MissingCategory_UsesEbaysTopSuggestion()
    {
        var listing = ValidListing();
        listing.CategoryId = null;
        var ebay = new Mock<IEbayService>();
        ebay.Setup(e => e.GetCategorySuggestionsAsync(listing.Title, It.IsAny<int?>()))
            .ReturnsAsync(new List<CategorySuggestionDto> { new() { CategoryId = "11700", CategoryName = "Candles" } });

        var dto = await Prepare(listing, ebay);

        Assert.Equal("11700", dto.CategoryId);
        Assert.Equal("Candles", listing.CategoryName);
    }
}
