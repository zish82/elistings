using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Server.Configuration;
using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>Who may publish whose listing, and with which eBay account.</summary>
public class PublishOwnershipTests
{
    private static Listing Listing(int ownerUserId, int? accountId = null) => new()
    {
        OwnerUserId = ownerUserId,
        EbayAccountId = accountId,
        Title = "Ivory pillar candle 15cm",
        Price = 9.99m,
        CategoryId = "46782",
        PaymentPolicyId = "p", FulfillmentPolicyId = "f", ReturnPolicyId = "r",
        Marketplace = "eBay",
        ImageUrlsJson = JsonSerializer.Serialize(new[] { "https://x/a.jpg" })
    };

    private static Task<ListingDto> Prepare(AppDbContext ctx, Listing listing, ICurrentUserService publisher)
        => ListingPublishHelper.PrepareAsync(listing, ctx, new Mock<IEbayService>().Object, new EbaySettings(),
            new SkuService(ctx, new SupplierCatalogService(ctx)), publisher);

    private static AppDbContext Seed()
    {
        var ctx = TestHelpers.NewContext();
        ctx.EbayTokens.AddRange(
            new EbayTokenInfo { Id = 10, UserId = 2, Name = "Owner shop", IsDefault = true, AccessToken = "owner-token", ExpiryTime = DateTime.UtcNow.AddHours(1) },
            new EbayTokenInfo { Id = 20, UserId = 1, Name = "Admin shop", IsDefault = true, AccessToken = "admin-token", ExpiryTime = DateTime.UtcNow.AddHours(1) });
        ctx.SaveChanges();
        return ctx;
    }

    [Fact]
    public async Task Admin_PublishesAnotherUsersListing_WithTheOwnersAccount()
    {
        using var ctx = Seed();
        var listing = Listing(ownerUserId: 2);
        ctx.Listings.Add(listing);
        ctx.SaveChanges();

        var dto = await Prepare(ctx, listing, TestHelpers.User(userId: 1, role: AuthRoles.Admin));

        Assert.Equal(10, dto.EbayAccountId); // the owner's default account, not the admin's
    }

    [Theory]
    [InlineData(AuthRoles.Manager)]
    [InlineData(AuthRoles.Lister)]
    public async Task OthersCantPublishSomeoneElsesListing(string role)
    {
        using var ctx = Seed();
        var listing = Listing(ownerUserId: 2, accountId: 10);
        ctx.Listings.Add(listing);
        ctx.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(ctx, listing, TestHelpers.User(userId: 3, role: role)));
        Assert.Contains("owner or an Admin", ex.Message);
    }

    [Fact]
    public async Task Owner_PublishesTheirOwnListing()
    {
        using var ctx = Seed();
        var listing = Listing(ownerUserId: 2);
        ctx.Listings.Add(listing);
        ctx.SaveChanges();

        var dto = await Prepare(ctx, listing, TestHelpers.User(userId: 2, role: AuthRoles.Lister));
        Assert.Equal(10, dto.EbayAccountId);
    }

    [Fact]
    public async Task AccountOfAnotherUser_IsRefused()
    {
        using var ctx = Seed();
        var listing = Listing(ownerUserId: 2, accountId: 20); // the admin's account on the owner's listing
        ctx.Listings.Add(listing);
        ctx.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => Prepare(ctx, listing, TestHelpers.User(userId: 1, role: AuthRoles.Admin)));
        Assert.Contains("doesn't belong to the listing's owner", ex.Message);
    }

    // ------------------------------------------------------------------ token lookup

    private static EbayService Ebay(string dbName, ICurrentUserService user)
    {
        var services = new ServiceCollection()
            .AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName))
            .BuildServiceProvider();
        using (var scope = services.CreateScope())
        {
            var ctx = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            ctx.EbayTokens.Add(new EbayTokenInfo { Id = 10, UserId = 2, Name = "Owner shop", IsDefault = true, AccessToken = "owner-token", ExpiryTime = DateTime.UtcNow.AddHours(1) });
            ctx.SaveChanges();
        }
        return new EbayService(Options.Create(new EbaySettings()), new HttpClient(), services, user, new MemoryCache(new MemoryCacheOptions()));
    }

    [Fact]
    public async Task Admin_CanUseAnotherUsersAccountToken()
    {
        var ebay = Ebay($"tokens-{Guid.NewGuid():N}", TestHelpers.User(userId: 1, role: AuthRoles.Admin));
        Assert.Equal("owner-token", await ebay.GetOAuthTokenAsync(10));
    }

    [Theory]
    [InlineData(AuthRoles.Manager)]
    [InlineData(AuthRoles.Lister)]
    public async Task NonAdmins_CantUseAnotherUsersAccountToken(string role)
    {
        var ebay = Ebay($"tokens-{Guid.NewGuid():N}", TestHelpers.User(userId: 3, role: role));
        await Assert.ThrowsAsync<Exception>(() => ebay.GetOAuthTokenAsync(10));
    }
}
