using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>My categories: tag normalising/typing, tag sync, tree paths, resolving a product's category, channel lookup.</summary>
public class CatalogCategoryTests
{
    [Theory]
    [InlineData("Pots, Tanks & Trays", "pots tanks and trays")]
    [InlineData("Pots Tanks and Trays", "pots tanks and trays")]
    [InlineData("  reward_points ", "reward points")]
    [InlineData("pH & EC Management", "ph and ec management")]
    public void TagText_Normalizes(string name, string expected) => Assert.Equal(expected, TagText.Normalize(name));

    [Theory]
    [InlineData("SALE", "promo")]
    [InlineData("Special offers", "promo")]
    [InlineData("brand new", "promo")]
    [InlineData("omega", "brand")]           // start of vendor "Omega Lighting"
    [InlineData("Plagron", "brand")]
    [InlineData("Carbon Filters", "type")]
    public void TagText_Classifies(string name, string expected)
    {
        var brands = new[] { "Omega Lighting", "Plagron", "Carbon Garden" }.Select(TagText.Normalize).ToList();
        Assert.Equal(expected, TagText.Classify(TagText.Normalize(name), brands));
    }

    [Fact]
    public async Task TagSync_CreatesTagsMergesSpellingsAndReplacesLinks()
    {
        using var ctx = TestHelpers.NewContext();
        var a = new SupplierProduct { Supplier = "EdenHorticulture", Title = "Filter" };
        var b = new SupplierProduct { Supplier = "EdenHorticulture", Title = "Pot" };
        ctx.SupplierProducts.AddRange(a, b);
        await ctx.SaveChangesAsync();
        var service = new ProductTagService(ctx);

        await service.SyncAsync("EdenHorticulture", new Dictionary<int, List<string>>
        {
            [a.Id] = new() { "Carbon Filters", "SALE", "Ventilation" },
            [b.Id] = new() { "Pots, Tanks & Trays", "Pots Tanks and Trays" }
        }, new[] { "Carbon Garden" });

        Assert.Equal(4, ctx.ProductTags.Count()); // the two pot spellings are one tag
        Assert.Equal("promo", ctx.ProductTags.Single(t => t.NormalizedName == "sale").Type);
        Assert.Equal(1, ctx.SupplierProductTags.Count(l => l.SupplierProductId == b.Id));

        // Re-import: SALE dropped, user's tag type kept.
        ctx.ProductTags.Single(t => t.NormalizedName == "ventilation").Type = ProductTagTypes.Ignore;
        await ctx.SaveChangesAsync();
        await service.SyncAsync("EdenHorticulture", new Dictionary<int, List<string>> { [a.Id] = new() { "Carbon Filters", "Ventilation" } }, Array.Empty<string>());

        Assert.Equal(2, ctx.SupplierProductTags.Count(l => l.SupplierProductId == a.Id));
        Assert.Equal(ProductTagTypes.Ignore, ctx.ProductTags.Single(t => t.NormalizedName == "ventilation").Type);
        Assert.Equal(1, ctx.SupplierProductTags.Count(l => l.SupplierProductId == b.Id)); // untouched
    }

    [Fact]
    public void Tree_BuildsPathsAndDepths_AndSurvivesLoops()
    {
        var garden = new CatalogCategory { Id = 1, Name = "Garden" };
        var vent = new CatalogCategory { Id = 2, Name = "Ventilation", ParentId = 1 };
        var filters = new CatalogCategory { Id = 3, Name = "Carbon Filters", ParentId = 2 };
        var loopA = new CatalogCategory { Id = 4, Name = "A", ParentId = 5 };
        var loopB = new CatalogCategory { Id = 5, Name = "B", ParentId = 4 };

        var tree = CatalogCategoryService.BuildTree(new[] { garden, vent, filters, loopA, loopB });

        Assert.Equal("Garden › Ventilation › Carbon Filters", tree[3].Path);
        Assert.Equal(2, tree[3].Depth);
        Assert.Equal(0, tree[1].Depth);
        Assert.NotNull(tree[4].Path); // no infinite loop
        Assert.Equal(new HashSet<int> { 1, 2, 3 }, CatalogCategoryService.WithDescendants(new[] { garden, vent, filters }, 1));

        // Tree order: parent then its children, even when another top-level name sorts in between.
        var tools = new CatalogCategory { Id = 6, Name = "Garden Tools" };
        var order = CatalogCategoryService.DepthFirst(new[] { tools, filters, garden, vent }).Select(c => c.Id).ToList();
        Assert.Equal(new[] { 1, 2, 3, 6 }, order);
    }

    [Fact]
    public void Resolve_PicksTheMostSpecificMapping()
    {
        // 10 Garden › 20 Ventilation › 30 Carbon Filters;  40 Lighting › 50 Lighting Accessories
        var depths = new Dictionary<int, int> { [10] = 0, [20] = 1, [30] = 2, [40] = 0, [50] = 1, [60] = 2 };
        var parents = new Dictionary<int, int?> { [10] = null, [20] = 10, [30] = 20, [40] = null, [50] = 40, [60] = 50 };
        var categoryMappings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["Ventilation"] = 20 };
        var tagMappings = new Dictionary<int, int> { [100] = 30, [101] = 10, [102] = 60 };

        // Tag "Carbon Filters" (deeper, inside Ventilation) refines the supplier category.
        Assert.Equal(30, CatalogCategoryService.Resolve("ventilation", new[] { 101, 100 }, categoryMappings, tagMappings, depths, parents));
        // A shallower tag doesn't override the category.
        Assert.Equal(20, CatalogCategoryService.Resolve("Ventilation", new[] { 101 }, categoryMappings, tagMappings, depths, parents));
        // A deeper tag from another branch can't pull the product out of its category.
        Assert.Equal(20, CatalogCategoryService.Resolve("Ventilation", new[] { 102 }, categoryMappings, tagMappings, depths, parents));
        // Only tag mappings: the deepest.
        Assert.Equal(60, CatalogCategoryService.Resolve(null, new[] { 101, 102 }, categoryMappings, tagMappings, depths, parents));
        // Mapping to a deleted category is ignored.
        Assert.Null(CatalogCategoryService.Resolve("Ventilation", Array.Empty<int>(), categoryMappings, tagMappings, new Dictionary<int, int>(), parents));
    }

    [Theory]
    [InlineData("A › B", "Leaf", "A › B")]
    [InlineData(null, "Leaf", "Leaf")]
    [InlineData("  ", " Leaf ", "Leaf")]
    [InlineData(null, null, null)]
    public void CategoryKey_PrefersFullPath(string? path, string? name, string? expected)
        => Assert.Equal(expected, CatalogCategoryService.CategoryKey(path, name));

    [Fact]
    public async Task ChannelCategory_IsInheritedFromTheNearestParent()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.CatalogCategories.AddRange(
            new CatalogCategory { Id = 1, Name = "Garden" },
            new CatalogCategory { Id = 2, Name = "Ventilation", ParentId = 1 },
            new CatalogCategory { Id = 3, Name = "Carbon Filters", ParentId = 2 });
        ctx.ChannelCategoryMappings.Add(new ChannelCategoryMapping { CatalogCategoryId = 2, Channel = SalesChannels.Ebay, ExternalId = "42", ExternalName = "Fans" });
        await ctx.SaveChangesAsync();
        var service = new CatalogCategoryService(ctx);

        Assert.Equal("42", (await service.FindChannelCategoryAsync(3, SalesChannels.Ebay))?.ExternalId);
        Assert.Null(await service.FindChannelCategoryAsync(1, SalesChannels.Ebay));
        Assert.Null(await service.FindChannelCategoryAsync(3, SalesChannels.Amazon));
        Assert.Null(await service.FindChannelCategoryAsync(null, SalesChannels.Ebay));
    }

    [Fact]
    public async Task ValidateParent_RejectsLoops()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.CatalogCategories.AddRange(new CatalogCategory { Id = 1, Name = "A" }, new CatalogCategory { Id = 2, Name = "B", ParentId = 1 });
        await ctx.SaveChangesAsync();
        var service = new CatalogCategoryService(ctx);

        Assert.NotNull(await service.ValidateParentAsync(1, 2)); // A under its own child
        Assert.NotNull(await service.ValidateParentAsync(1, 1));
        Assert.Null(await service.ValidateParentAsync(2, null));
        Assert.Null(await service.ValidateParentAsync(null, 1));
    }

    [Fact]
    public async Task Publish_UsesMyCategorysEbayMapping()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.CatalogCategories.Add(new CatalogCategory { Id = 7, Name = "Candles" });
        ctx.ChannelCategoryMappings.Add(new ChannelCategoryMapping { CatalogCategoryId = 7, Channel = SalesChannels.Ebay, ExternalId = "46782", ExternalName = "Candles" });
        var listing = new Listing
        {
            Title = "Ivory pillar candle", Price = 9.99m, OwnerUserId = 1, CatalogCategoryId = 7,
            PaymentPolicyId = "p", FulfillmentPolicyId = "f", ReturnPolicyId = "r", Marketplace = "eBay",
            ImageUrlsJson = "[\"https://x/a.jpg\"]"
        };
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();
        var ebay = new Moq.Mock<IEbayService>(Moq.MockBehavior.Strict); // must not be asked for suggestions

        var dto = await ListingPublishHelper.PrepareAsync(listing, ctx, ebay.Object, new Server.Configuration.EbaySettings(),
            new SkuService(ctx, new SupplierCatalogService(ctx)));

        Assert.Equal("46782", dto.CategoryId);
    }
}
