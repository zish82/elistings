using Moq;
using Server.Data;
using Server.Services;
using Xunit;

namespace Server.Tests;

/// <summary>"Enable background sync": scheduled work per supplier, without affecting manual imports or publishing.</summary>
public class BackgroundSyncTests
{
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]   // background sync off
    [InlineData(false, true, false)]   // supplier disabled
    public async Task BackgroundSync_NeedsTheSupplierEnabledAndTheSwitchOn(bool active, bool backgroundSync, bool expected)
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx, "Safelincs", host: "safelincs.co.uk");
        supplier.IsActive = active;
        supplier.BackgroundSyncEnabled = backgroundSync;
        ctx.SaveChanges();

        Assert.Equal(expected, await new SupplierCatalogService(ctx).IsBackgroundSyncAllowedAsync("Safelincs"));
    }

    [Fact]
    public async Task ScheduledSafelincsImport_DoesNothing_WhenBackgroundSyncIsOff()
    {
        using var ctx = TestHelpers.NewContext();
        var supplier = TestHelpers.AddSupplier(ctx, "Safelincs", host: "safelincs.co.uk");
        supplier.BackgroundSyncEnabled = false;
        ctx.SaveChanges();
        var http = new Mock<IHttpClientFactory>(MockBehavior.Strict); // any call to Safelincs would throw
        var catalog = new SupplierCatalogService(ctx);
        var importer = new SafelincsCatalogImportService(ctx, http.Object, catalog, new SupplierPriceListService(ctx, catalog), new ProductTagService(ctx));

        await importer.RunScheduledAsync();

        http.Verify(h => h.CreateClient(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public void Suppliers_HaveBackgroundSyncOnByDefault()
        => Assert.True(new Supplier().BackgroundSyncEnabled);
}
