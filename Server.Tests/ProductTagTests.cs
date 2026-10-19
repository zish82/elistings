using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>Supplier tags: normalising, guessing the type, and syncing products' tags.</summary>
public class ProductTagTests
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
}
