using Server.Data;
using Server.Services;
using Xunit;

namespace Server.Tests;

/// <summary>SKU format: {prefix}-{supplier code}, unique, eBay-safe.</summary>
public class SkuServiceTests
{
    [Theory]
    [InlineData("dsm-s16301", "DSM-S16301")]
    [InlineData("  ab c/12 ", "AB-C-12")]
    [InlineData("--x--", "X")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Normalize_UpperCasesAndReplacesInvalidCharacters(string? input, string? expected)
        => Assert.Equal(expected, SkuService.Normalize(input));

    private static (AppDbContext Ctx, SkuService Service) Setup()
    {
        var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx, "DropShipMall", skuPrefix: "DSM");
        return (ctx, new SkuService(ctx, new SupplierCatalogService(ctx)));
    }

    private static Listing SavedListing(AppDbContext ctx, SupplierProduct? product = null, string? sku = null)
    {
        if (product != null) { ctx.SupplierProducts.Add(product); ctx.SaveChanges(); }
        var listing = new Listing { Title = "T", OwnerUserId = 1, SupplierProductId = product?.Id, Sku = sku };
        ctx.Listings.Add(listing);
        ctx.SaveChanges();
        return listing;
    }

    [Fact]
    public async Task EnsureSku_UsesSupplierPrefixAndSupplierCode()
    {
        var (ctx, service) = Setup();
        var listing = SavedListing(ctx, new SupplierProduct { Supplier = "DropShipMall", Title = "Glass", SupplierSku = "s16301" });

        await service.EnsureSkuAsync(listing);

        Assert.Equal("DSM-S16301", listing.Sku);
    }

    [Fact]
    public async Task EnsureSku_AddsSuffixForDuplicates()
    {
        var (ctx, service) = Setup();
        var product = new SupplierProduct { Supplier = "DropShipMall", Title = "Glass", SupplierSku = "S16301" };
        var first = SavedListing(ctx, product);
        await service.EnsureSkuAsync(first);
        ctx.SaveChanges();

        var second = new Listing { Title = "T2", OwnerUserId = 1, SupplierProductId = product.Id };
        ctx.Listings.Add(second);
        ctx.SaveChanges();
        await service.EnsureSkuAsync(second);

        Assert.Equal("DSM-S16301-2", second.Sku);
    }

    [Fact]
    public async Task EnsureSku_WithoutSupplier_FallsBackToManualPrefixAndListingId()
    {
        var (ctx, service) = Setup();
        var listing = SavedListing(ctx);

        await service.EnsureSkuAsync(listing);

        Assert.Equal($"MAN-L{listing.Id}", listing.Sku);
    }

    [Fact]
    public async Task EnsureSku_KeepsExistingSku()
    {
        var (ctx, service) = Setup();
        var listing = SavedListing(ctx, sku: "MY-OWN-SKU");

        await service.EnsureSkuAsync(listing);

        Assert.Equal("MY-OWN-SKU", listing.Sku);
    }

    [Fact]
    public async Task EnsureSku_UnsavedListing_Throws()
    {
        var (_, service) = Setup();
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.EnsureSkuAsync(new Listing { Title = "T" }));
    }

    [Fact]
    public async Task EnsureSku_TruncatesToFiftyCharacters()
    {
        var (ctx, service) = Setup();
        var listing = SavedListing(ctx, new SupplierProduct { Supplier = "DropShipMall", Title = "Long", SupplierSku = new string('A', 80) });

        await service.EnsureSkuAsync(listing);

        Assert.True(listing.Sku!.Length <= SkuService.MaxLength);
        Assert.StartsWith("DSM-AAAA", listing.Sku);
    }
}
