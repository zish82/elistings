using System.Text.Json;
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

/// <summary>
/// Who sees what (Admin / Manager with assigned users / Lister), the accounts overview, posting one listing to several
/// accounts, shared accounts, and the "already posted on this account" warning.
///
/// Users: 1 Admin · 2 Manager (manages 3) · 3 Lister (managed by 2) · 4 Lister (no manager)
/// Accounts: 10 = user 3 "Shop A" (default) · 11 = user 3 "Shop B" · 20 = user 4 "Shop C", shared with user 3
/// </summary>
public class AccountsAndVisibilityTests
{
    private static AppDbContext Seed()
    {
        var ctx = TestHelpers.NewContext();
        ctx.Users.AddRange(
            new AppUser { Id = 1, Email = "admin@x.com", Role = AuthRoles.Admin },
            new AppUser { Id = 2, Email = "manager@x.com", Role = AuthRoles.Manager },
            new AppUser { Id = 3, Email = "lister3@x.com", Role = AuthRoles.Lister, ManagerUserId = 2 },
            new AppUser { Id = 4, Email = "lister4@x.com", Role = AuthRoles.Lister });
        ctx.EbayTokens.AddRange(
            Token(10, 3, "Shop A", isDefault: true),
            Token(11, 3, "Shop B"),
            Token(20, 4, "Shop C", isDefault: true));
        ctx.AccountUserAccess.Add(new AccountUserAccess { AccountId = 20, UserId = 3, Marketplace = MarketplaceNames.Ebay });
        ctx.SaveChanges();
        return ctx;
    }

    private static EbayTokenInfo Token(int id, int userId, string name, bool isDefault = false) => new()
    {
        Id = id, UserId = userId, Name = name, IsDefault = isDefault, AccessToken = "t", RefreshToken = "r",
        ExpiryTime = DateTime.UtcNow.AddHours(1), RefreshTokenExpiryTime = DateTime.UtcNow.AddMonths(6)
    };

    private static Listing NewListing(int owner, string title, int? accountId = null, int? productId = null) => new()
    {
        OwnerUserId = owner,
        EbayAccountId = accountId,
        SupplierProductId = productId,
        Title = title,
        Price = 19.99m,
        Quantity = 3,
        Sku = "SKU-" + title.Replace(" ", ""),
        CategoryId = "46782",
        PaymentPolicyId = "pay-A", FulfillmentPolicyId = "ship-A", ReturnPolicyId = "ret-A",
        Marketplace = "eBay",
        ImageUrlsJson = JsonSerializer.Serialize(new[] { "https://x/a.jpg" })
    };

    private static ListingsController Listings(AppDbContext ctx, int userId, string role)
        => new(ctx, new Mock<IScraperService>(MockBehavior.Strict).Object, new Mock<IEbayService>(MockBehavior.Strict).Object,
            new Mock<IEdenSupplierSyncService>(MockBehavior.Strict).Object, Options.Create(new EbaySettings()),
            TestHelpers.User(userId, role), new SkuService(ctx, new SupplierCatalogService(ctx)));

    private static async Task<List<string>> VisibleTitles(AppDbContext ctx, int userId, string role)
    {
        var result = await Listings(ctx, userId, role).GetListings();
        return Assert.IsType<List<ListingDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Select(l => l.Title).OrderBy(t => t).ToList();
    }

    private static void AddListingsForEveryone(AppDbContext ctx)
    {
        ctx.Listings.AddRange(
            NewListing(1, "admin item"), NewListing(2, "manager item"),
            NewListing(3, "lister3 item", 10), NewListing(4, "lister4 item", 20));
        ctx.SaveChanges();
    }

    // ------------------------------------------------------------------ who sees which listings

    [Fact]
    public async Task UserScope_AdminAll_ManagerSelfAndAssigned_ListerSelf()
    {
        using var ctx = Seed();
        Assert.Null(await UserScope.VisibleUserIdsAsync(ctx, TestHelpers.User(1, AuthRoles.Admin)));
        Assert.Equal(new[] { 2, 3 }, (await UserScope.VisibleUserIdsAsync(ctx, TestHelpers.User(2, AuthRoles.Manager)))!.OrderBy(i => i));
        Assert.Equal(new[] { 4 }, await UserScope.VisibleUserIdsAsync(ctx, TestHelpers.User(4, AuthRoles.Lister)));
    }

    [Fact]
    public async Task Admin_SeesEveryUsersListings()
    {
        using var ctx = Seed();
        AddListingsForEveryone(ctx);
        Assert.Equal(4, (await VisibleTitles(ctx, 1, AuthRoles.Admin)).Count);
    }

    [Fact]
    public async Task Manager_SeesOwnAndAssignedUsersListings_Only()
    {
        using var ctx = Seed();
        AddListingsForEveryone(ctx);
        Assert.Equal(new[] { "lister3 item", "manager item" }, await VisibleTitles(ctx, 2, AuthRoles.Manager));

        var other = ctx.Listings.Single(l => l.Title == "lister4 item");
        Assert.IsType<NotFoundResult>((await Listings(ctx, 2, AuthRoles.Manager).GetListing(other.Id)).Result);
    }

    [Fact]
    public async Task Lister_SeesOnlyOwnListings_EvenOnASharedAccount()
    {
        using var ctx = Seed();
        AddListingsForEveryone(ctx);
        // User 3 can post with user 4's account 20, but still doesn't see user 4's listing on it.
        Assert.Equal(new[] { "lister3 item" }, await VisibleTitles(ctx, 3, AuthRoles.Lister));
        Assert.Equal(new[] { "lister4 item" }, await VisibleTitles(ctx, 4, AuthRoles.Lister));
    }

    [Fact]
    public async Task Listings_CanBeFilteredByAccount()
    {
        using var ctx = Seed();
        var onB = NewListing(3, "on shop B", 10);
        ctx.Listings.AddRange(onB, NewListing(3, "only shop A", 10));
        ctx.SaveChanges();
        ctx.ListingPublications.Add(new ListingPublication { ListingId = onB.Id, AccountId = 11, Status = PublicationStatuses.Published });
        ctx.SaveChanges();

        var result = await Listings(ctx, 3, AuthRoles.Lister).GetListings(accountId: 11);
        var titles = Assert.IsType<List<ListingDto>>(Assert.IsType<OkObjectResult>(result.Result).Value).Select(l => l.Title);
        Assert.Equal(new[] { "on shop B" }, titles);
    }

    [Fact]
    public void Sales_FollowTheSameRules()
    {
        using var ctx = Seed();
        ctx.EbayOrders.AddRange(
            new EbayOrder { OrderId = "o2", OwnerUserId = 2, EbayAccountId = 10, Currency = "GBP" },
            new EbayOrder { OrderId = "o3", OwnerUserId = 3, EbayAccountId = 10, Currency = "GBP" },
            new EbayOrder { OrderId = "o4", OwnerUserId = 4, EbayAccountId = 20, Currency = "GBP" });
        ctx.SaveChanges();

        Assert.Equal(3, ctx.EbayOrders.VisibleTo(null).Count());
        Assert.Equal(new[] { "o2", "o3" }, ctx.EbayOrders.VisibleTo(new List<int> { 2, 3 }).Select(o => o.OrderId).OrderBy(o => o));
        Assert.Equal(new[] { "o4" }, ctx.EbayOrders.VisibleTo(new List<int> { 4 }).Select(o => o.OrderId));
    }

    // ------------------------------------------------------------------ assigning users to a manager

    private static UsersController Users(AppDbContext ctx) => new(ctx, new PasswordHasher(), TestHelpers.User(1, AuthRoles.Admin));

    private static UpdateUserRequest Update(AppUser u, int? managerId) => new()
        { Email = u.Email, Role = u.Role, IsActive = true, ManagerUserId = managerId };

    [Fact]
    public async Task Admin_AssignsAUserToAManager()
    {
        using var ctx = Seed();
        var user4 = ctx.Users.Single(u => u.Id == 4);

        Assert.IsType<OkObjectResult>((await Users(ctx).UpdateUser(4, Update(user4, 2))).Result);

        Assert.Equal(2, ctx.Users.Single(u => u.Id == 4).ManagerUserId);
        Assert.Contains(4, (await UserScope.VisibleUserIdsAsync(ctx, TestHelpers.User(2, AuthRoles.Manager)))!);
    }

    [Fact]
    public async Task ManagerMustHaveTheManagerRole_AndNotBeTheUserThemselves()
    {
        using var ctx = Seed();
        var user4 = ctx.Users.Single(u => u.Id == 4);

        Assert.IsType<BadRequestObjectResult>((await Users(ctx).UpdateUser(4, Update(user4, 3))).Result); // 3 is a Lister
        Assert.IsType<BadRequestObjectResult>((await Users(ctx).UpdateUser(4, Update(user4, 4))).Result); // self
        Assert.Null(ctx.Users.Single(u => u.Id == 4).ManagerUserId);
    }

    // ------------------------------------------------------------------ accounts overview

    private static void AddPublications(AppDbContext ctx)
    {
        var l3a = NewListing(3, "l3 on A", 10);
        var l3c = NewListing(3, "l3 on C", 20);
        var l4c = NewListing(4, "l4 on C", 20);
        var l3draft = NewListing(3, "l3 draft", 10);
        ctx.Listings.AddRange(l3a, l3c, l4c, l3draft);
        ctx.SaveChanges();
        ctx.ListingPublications.AddRange(
            new ListingPublication { ListingId = l3a.Id, AccountId = 10, Status = PublicationStatuses.Published },
            new ListingPublication { ListingId = l3a.Id, AccountId = 11, Status = PublicationStatuses.Failed, LastError = "x" },
            new ListingPublication { ListingId = l3c.Id, AccountId = 20, Status = PublicationStatuses.Published },
            new ListingPublication { ListingId = l4c.Id, AccountId = 20, Status = PublicationStatuses.Published });
        ctx.SaveChanges();
    }

    [Fact]
    public async Task Overview_User_SeesOwnAndSharedAccounts_WithOnlyTheirOwnCounts()
    {
        using var ctx = Seed();
        AddPublications(ctx);

        var overview = await AccountsOverviewService.BuildAsync(ctx, TestHelpers.User(3, AuthRoles.Lister));

        var me = Assert.Single(overview);
        Assert.True(me.IsMe);
        Assert.Equal(new[] { "Shop A", "Shop B", "Shop C" }, me.Accounts.Select(a => a.Name).OrderBy(n => n));
        Assert.Equal(1, me.Accounts.Single(a => a.AccountId == 10).PublishedCount);
        Assert.Equal(1, me.Accounts.Single(a => a.AccountId == 11).FailedCount);
        var shared = me.Accounts.Single(a => a.AccountId == 20);
        Assert.True(shared.IsShared);
        Assert.Equal(1, shared.PublishedCount); // not user 4's listing on the same account
        Assert.Equal(1, me.DraftCount);
    }

    [Fact]
    public async Task Overview_Manager_SeesThemselvesAndTheirUsers()
    {
        using var ctx = Seed();
        AddPublications(ctx);
        var overview = await AccountsOverviewService.BuildAsync(ctx, TestHelpers.User(2, AuthRoles.Manager));
        Assert.Equal(new[] { 2, 3 }, overview.Select(u => u.UserId).OrderBy(i => i));
        Assert.Equal("manager@x.com", overview.Single(u => u.UserId == 3).ManagerEmail);
    }

    [Fact]
    public async Task Overview_Admin_SeesEveryone_AndWhoAccountsAreSharedWith()
    {
        using var ctx = Seed();
        AddPublications(ctx);
        var overview = await AccountsOverviewService.BuildAsync(ctx, TestHelpers.User(1, AuthRoles.Admin));

        Assert.Equal(4, overview.Count);
        var shopC = overview.Single(u => u.UserId == 4).Accounts.Single(a => a.AccountId == 20);
        Assert.Equal(new[] { 3 }, shopC.SharedWithUserIds);
        Assert.Equal(1, shopC.PublishedCount);
    }

    [Fact]
    public async Task Admin_SharesAnAccount_OwnerIsNeverStoredAsShared()
    {
        using var ctx = Seed();
        await AccountAccess.SetSharedUsersAsync(ctx, 10, new[] { 3, 4, 2 }); // 3 owns it

        Assert.Equal(new[] { 2, 4 }, ctx.AccountUserAccess.Where(a => a.AccountId == 10).Select(a => a.UserId).OrderBy(i => i));
        Assert.True(await AccountAccess.CanUseAsync(ctx, 4, 10));

        await AccountAccess.SetSharedUsersAsync(ctx, 10, Array.Empty<int>());
        Assert.False(await AccountAccess.CanUseAsync(ctx, 4, 10));
        Assert.True(await AccountAccess.CanUseAsync(ctx, 3, 10)); // owner keeps access
    }

    // ------------------------------------------------------------------ posting to several accounts

    private static Mock<IEbayService> EbayMock(params int[] failingAccounts)
    {
        var ebay = new Mock<IEbayService>();
        ebay.Setup(e => e.CreateListingAsync(It.IsAny<ListingDto>()))
            .ReturnsAsync((ListingDto d) =>
            {
                if (failingAccounts.Contains(d.EbayAccountId ?? 0)) throw new Exception("eBay said no");
                return new EbayListingResponse { Sku = d.Sku!, OfferId = $"offer-{d.EbayAccountId}", ListingId = $"item-{d.EbayAccountId}" };
            });
        // Account 10 has "pay-A"; accounts 11 and 20 have their own policies, one named like account 10's.
        ebay.Setup(e => e.GetPaymentPoliciesAsync(It.IsAny<int?>())).ReturnsAsync((int? a) => a == 10
            ? new List<EbayPolicyDto> { new() { PolicyId = "pay-A", Name = "Standard payment" } }
            : new List<EbayPolicyDto> { new() { PolicyId = $"pay-other-{a}", Name = "Other" }, new() { PolicyId = $"pay-std-{a}", Name = "Standard payment" } });
        ebay.Setup(e => e.GetFulfillmentPoliciesAsync(It.IsAny<int?>())).ReturnsAsync((int? a) => new List<EbayPolicyDto> { new() { PolicyId = $"ship-{a}", Name = "Ship" } });
        ebay.Setup(e => e.GetReturnPoliciesAsync(It.IsAny<int?>())).ReturnsAsync((int? a) => new List<EbayPolicyDto> { new() { PolicyId = $"ret-{a}", Name = "Returns" } });
        return ebay;
    }

    private static ListingPublisher Publisher(AppDbContext ctx, IEbayService ebay, int userId = 3, string role = AuthRoles.Lister)
        => new(ctx, ebay, new EbaySettings(), new SkuService(ctx, new SupplierCatalogService(ctx)), TestHelpers.User(userId, role));

    [Fact]
    public async Task User_PostsOneListingToSeveralAccounts()
    {
        using var ctx = Seed();
        var listing = NewListing(3, "multi", 10);
        ctx.Listings.Add(listing);
        ctx.SaveChanges();
        var ebay = EbayMock();

        var outcome = await Publisher(ctx, ebay.Object).PublishAsync(listing, new[] { 10, 11, 20 });

        Assert.All(outcome.Results, r => Assert.True(r.Success));
        Assert.Empty(outcome.Warnings());
        ebay.Verify(e => e.CreateListingAsync(It.Is<ListingDto>(d => d.EbayAccountId == 10 && d.PaymentPolicyId == "pay-A")), Times.Once);
        // Other accounts: same-named policy, else the account's first policy. Same price and quantity everywhere.
        ebay.Verify(e => e.CreateListingAsync(It.Is<ListingDto>(d => d.EbayAccountId == 11 && d.PaymentPolicyId == "pay-std-11" && d.FulfillmentPolicyId == "ship-11")), Times.Once);
        ebay.Verify(e => e.CreateListingAsync(It.Is<ListingDto>(d => d.EbayAccountId == 20 && d.Quantity == 3 && d.Price == 19.99m)), Times.Once);

        var pubs = ctx.ListingPublications.Where(p => p.ListingId == listing.Id).OrderBy(p => p.AccountId).ToList();
        Assert.Equal(new[] { 10, 11, 20 }, pubs.Select(p => p.AccountId));
        Assert.All(pubs, p => Assert.Equal(PublicationStatuses.Published, p.Status));
        Assert.Equal("Published", listing.Status);
        Assert.Equal(10, listing.EbayAccountId);   // the main account stays the listing's own
        Assert.Equal("item-10", listing.EbayItemId);
    }

    [Fact]
    public async Task Resync_GoesToEveryAccountTheListingIsOn()
    {
        using var ctx = Seed();
        var listing = NewListing(3, "resync", 10);
        ctx.Listings.Add(listing);
        ctx.SaveChanges();
        await Publisher(ctx, EbayMock().Object).PublishAsync(listing, new[] { 10, 11 });

        var ebay = EbayMock();
        await Publisher(ctx, ebay.Object).PublishAsync(listing); // no accounts given

        ebay.Verify(e => e.CreateListingAsync(It.Is<ListingDto>(d => d.EbayAccountId == 10 && d.EbayOfferId == "offer-10")), Times.Once);
        ebay.Verify(e => e.CreateListingAsync(It.Is<ListingDto>(d => d.EbayAccountId == 11 && d.EbayOfferId == "offer-11")), Times.Once);
    }

    [Fact]
    public async Task OneAccountFailing_StillPublishesTheOthers_WithAWarning()
    {
        using var ctx = Seed();
        var listing = NewListing(3, "partial", 10);
        ctx.Listings.Add(listing);
        ctx.SaveChanges();

        var outcome = await Publisher(ctx, EbayMock(failingAccounts: 11).Object).PublishAsync(listing, new[] { 10, 11 });

        Assert.Contains(outcome.Warnings(), w => w.Contains("Shop B") && w.Contains("eBay said no"));
        var failed = ctx.ListingPublications.Single(p => p.ListingId == listing.Id && p.AccountId == 11);
        Assert.Equal(PublicationStatuses.Failed, failed.Status);
        Assert.Equal("eBay said no", failed.LastError);
        Assert.Equal("Published", listing.Status);
    }

    [Fact]
    public async Task EveryAccountFailing_Throws()
    {
        using var ctx = Seed();
        var listing = NewListing(3, "nothing", 10);
        ctx.Listings.Add(listing);
        ctx.SaveChanges();

        await Assert.ThrowsAsync<Exception>(() => Publisher(ctx, EbayMock(10, 11).Object).PublishAsync(listing, new[] { 10, 11 }));
        Assert.NotEqual("Published", listing.Status);
    }

    [Fact]
    public async Task AnAccountTheOwnerCantUse_IsRefused()
    {
        using var ctx = Seed();
        var listing = NewListing(4, "not mine", 20); // user 4 has no access to user 3's account 10
        ctx.Listings.Add(listing);
        ctx.SaveChanges();
        var ebay = EbayMock();

        var outcome = await Publisher(ctx, ebay.Object, userId: 4).PublishAsync(listing, new[] { 20, 10 });

        Assert.False(outcome.Results.Single(r => r.AccountId == 10).Success);
        ebay.Verify(e => e.CreateListingAsync(It.Is<ListingDto>(d => d.EbayAccountId == 10)), Times.Never);
    }

    [Fact]
    public async Task SharedAccount_CanBePostedTo_ByTheUserItWasSharedWith()
    {
        using var ctx = Seed();
        var listing = NewListing(3, "on shared", 20); // account 20 belongs to user 4, shared with user 3
        ctx.Listings.Add(listing);
        ctx.SaveChanges();

        var outcome = await Publisher(ctx, EbayMock().Object).PublishAsync(listing);

        Assert.True(Assert.Single(outcome.Results).Success);
        Assert.Equal(20, ctx.ListingPublications.Single(p => p.ListingId == listing.Id).AccountId);
    }

    [Fact]
    public void ListingsPublishedBeforeThisFeature_GetAPublicationRow_Once()
    {
        using var ctx = Seed();
        var old = NewListing(3, "old", 10);
        old.Status = "Published";
        old.EbayItemId = "item-old";
        old.EbayOfferId = "offer-old";
        ctx.Listings.AddRange(old, NewListing(3, "draft", 10));
        ctx.SaveChanges();

        Assert.Equal(1, PublicationQueries.BackfillFromListings(ctx));
        ctx.SaveChanges();
        Assert.Equal(0, PublicationQueries.BackfillFromListings(ctx));

        var pub = ctx.ListingPublications.Single();
        Assert.Equal((old.Id, 10, "item-old", "offer-old"), (pub.ListingId, pub.AccountId, pub.ExternalListingId, pub.OfferId));
    }

    // ------------------------------------------------------------------ "already posted on this account" warning

    private static SupplierProductsController Products(AppDbContext ctx, int userId)
    {
        var catalog = new SupplierCatalogService(ctx);
        return new SupplierProductsController(ctx, TestHelpers.User(userId, AuthRoles.Lister), new Mock<IFeedProductSyncService>().Object,
            Options.Create(new EbaySettings()), new ProductEnrichmentService(ctx, Array.Empty<ISupplierProductEnricher>(), catalog),
            new BulkJobService(ctx, new Mock<IBackgroundJobClient>().Object), new SkuService(ctx, catalog));
    }

    /// <summary>User 4 published product P on account 20, which is shared with user 3.</summary>
    private static SupplierProduct SeedPostedProduct(AppDbContext ctx)
    {
        TestHelpers.AddSupplier(ctx);
        var product = new SupplierProduct { Supplier = "DropShipMall", Title = "Pillar candle", SupplierSku = "C1", SupplierPrice = 5m, IsActive = true };
        ctx.SupplierProducts.Add(product);
        foreach (var user in new[] { 2, 3, 4 }) ctx.UserSupplierAccess.Add(new UserSupplierAccess { UserId = user, SupplierCode = "DropShipMall" });
        ctx.SaveChanges();
        var listing = NewListing(4, "user 4 candle", 20, product.Id);
        listing.Status = "Published";
        ctx.Listings.Add(listing);
        ctx.SaveChanges();
        ctx.ListingPublications.Add(new ListingPublication { ListingId = listing.Id, AccountId = 20, Status = PublicationStatuses.Published });
        ctx.SaveChanges();
        return product;
    }

    private static AddSupplierProductsResult AddResult(ActionResult<AddSupplierProductsResult> result)
        => Assert.IsType<AddSupplierProductsResult>(Assert.IsType<OkObjectResult>(result.Result).Value);

    [Fact]
    public async Task AddingAProductAlreadyPostedOnASharedAccount_WarnsFirst()
    {
        using var ctx = Seed();
        var product = SeedPostedProduct(ctx);

        var result = AddResult(await Products(ctx, 3).AddToMyListings(new AddSupplierProductsRequest { ProductIds = { product.Id } }));

        Assert.Equal(0, result.Added);
        var conflict = Assert.Single(result.SharedAccountConflicts);
        Assert.Equal("Shop C", conflict.AccountName);
        Assert.Equal(MarketplaceNames.Ebay, conflict.Marketplace);
        Assert.Contains("You are about to post this listing which is already posted on this account and on this marketplace", conflict.Message);
        Assert.False(ctx.Listings.Any(l => l.OwnerUserId == 3));
    }

    [Fact]
    public async Task AddAnyway_AddsIt()
    {
        using var ctx = Seed();
        var product = SeedPostedProduct(ctx);

        var result = AddResult(await Products(ctx, 3).AddToMyListings(new AddSupplierProductsRequest
            { ProductIds = { product.Id }, AllowSharedAccountDuplicates = true }));

        Assert.Equal(1, result.Added);
        Assert.Empty(result.SharedAccountConflicts);
        Assert.True(ctx.Listings.Any(l => l.OwnerUserId == 3 && l.SupplierProductId == product.Id));
    }

    [Fact]
    public async Task NoWarning_WhenTheUserDoesntPostToThatAccount()
    {
        using var ctx = Seed();
        var product = SeedPostedProduct(ctx);

        // User 2 (manager) has no access to account 20.
        var result = AddResult(await Products(ctx, 2).AddToMyListings(new AddSupplierProductsRequest { ProductIds = { product.Id } }));

        Assert.Equal(1, result.Added);
        Assert.Empty(result.SharedAccountConflicts);
    }
}
