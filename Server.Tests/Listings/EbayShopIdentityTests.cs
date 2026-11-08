using Server.Data;
using Server.Services;
using Xunit;

namespace Server.Tests;

/// <summary>The same eListings user can't connect the same eBay shop twice; different users may.</summary>
public class EbayShopIdentityTests
{
    [Fact]
    public void ReadsTheUserIdFromGetUser()
    {
        const string xml = @"<GetUserResponse xmlns=""urn:ebay:apis:eBLBaseComponents""><Ack>Success</Ack><User><UserID>candle-shop-uk</UserID></User></GetUserResponse>";
        Assert.Equal("candle-shop-uk", EbayShopIdentity.ParseUserId(xml));
        Assert.Null(EbayShopIdentity.ParseUserId("not xml"));
        Assert.Null(EbayShopIdentity.ParseUserId(@"<GetUserResponse xmlns=""urn:ebay:apis:eBLBaseComponents""><Ack>Failure</Ack></GetUserResponse>"));
    }

    private static AppDbContext Seed()
    {
        var ctx = TestHelpers.NewContext();
        ctx.EbayTokens.AddRange(
            new EbayTokenInfo { Id = 1, UserId = 7, Name = "Main", EbayUserId = "candle-shop-uk", ExpiryTime = DateTime.UtcNow.AddHours(1) },
            new EbayTokenInfo { Id = 2, UserId = 8, Name = "Bob's", EbayUserId = "candle-shop-uk", ExpiryTime = DateTime.UtcNow.AddHours(1) });
        ctx.SaveChanges();
        return ctx;
    }

    [Fact]
    public async Task SameUser_SameShop_IsFound()
    {
        using var ctx = Seed();
        var same = await EbayShopIdentity.FindSameShopAsync(ctx, new HttpClient(), sandbox: false, userId: 7, "Candle-Shop-UK", exceptAccountId: null);
        Assert.Equal("Main", same?.Name);
    }

    [Fact]
    public async Task ReconnectingTheSameAccount_IsAllowed()
    {
        using var ctx = Seed();
        Assert.Null(await EbayShopIdentity.FindSameShopAsync(ctx, new HttpClient(), false, 7, "candle-shop-uk", exceptAccountId: 1));
    }

    [Fact]
    public async Task AnotherUser_MayConnectTheSameShop()
    {
        using var ctx = Seed();
        Assert.Null(await EbayShopIdentity.FindSameShopAsync(ctx, new HttpClient(), false, 9, "candle-shop-uk", null));
    }
}
