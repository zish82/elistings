using System.Text.Json;
using Moq;
using Server.Configuration;
using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>Safelincs page/sitemap parsing, trade price lists, supplier categories and disabled suppliers.</summary>
public class SafelincsAndCategoryTests
{
    // Trimmed from a real Safelincs product page (Oct 2026), including Livewire comments and snapshots.
    private const string ProductPage = """
        <html><head><meta property="og:image" content="https://www.safelincs.co.uk/images/og.jpg"></head><body>
        <ul class="breadcrumb" itemscope itemtype="https://schema.org/BreadcrumbList">
          <li itemprop="itemListElement"><a href="https://www.safelincs.co.uk">Home<meta itemprop="name" content="Home"></a></li>
          <li itemprop="itemListElement"><a href="https://www.safelincs.co.uk/category/products">Products<meta itemprop="name" content="Products"></a></li>
          <li itemprop="itemListElement"><a href="#">Extinguishers<meta itemprop="name" content="Extinguishers &amp; Equipment"></a></li>
          <li itemprop="itemListElement"><a href="#">Signs<meta itemprop="name" content="Fire Extinguisher ID Signs"></a></li>
        </ul>
        <div class="images">
          <meta itemprop="image" content="https://www.safelincs.co.uk/images/sign-front/530x530.jpg">
          <meta itemprop="image" content="https://www.safelincs.co.uk/images/sign-back/530x530.jpg">
          <div class="image"><img src="https://www.safelincs.co.uk/images/sign-front/530x530.jpg"></div>
        </div>
        <h1>Fire Extinguisher Self-Adhesive Signs</h1>
        <div wire:name="listing.buy-box.form">
          <select class="select" wire:model.live="listingableId">
            <option value="1003" selected>Water</option>
            <option value="1004">Foam</option>
            <option value="1005">Powder ABC</option>
          </select>
          <div itemprop="offers" itemscope itemtype="https://schema.org/Offer">
            <div wire:snapshot="{&quot;data&quot;:{&quot;listingableId&quot;:1004,&quot;quantity&quot;:1},&quot;memo&quot;:{}}" wire:name="listingable.stock">
              <div class="stock in-stock">
                <link itemprop="availability" href="https://schema.org/InStock">
                <p><!--[if BLOCK]><![endif]-->  20+ <!--[if ENDBLOCK]><![endif]--> In Stock</p>
                <i>Estimated dispatch: <!--[if BLOCK]><![endif]--> Tomorrow <!--[if ENDBLOCK]><![endif]--></i>
              </div>
            </div>
            <div class="price">
              <meta itemprop="price" content="4.39">
              <p class="primary-price">£4.39 inc VAT</p>
              <p class="secondary-price">£3.66 ex VAT</p>
            </div>
          </div>
          <div class="shipping-option"><h2>Standard Delivery</h2><p>2-3 working days</p><p class="shipping-price"> £4.20 inc VAT </p></div>
          <div class="shipping-option"><h2>Next Day Delivery</h2><p>Next working day</p><p class="shipping-price"> £7.00 inc VAT </p></div>
        </div>
        <div class="description"><p>Photoluminescent sign. See <a href="https://www.safelincs.co.uk/product/x">other signs</a>.</p></div>
        <meta itemprop="sku" content="JSLB6355S"><meta itemprop="mpn" content="LB6355SV">
        <table class="technical-data"><tbody>
          <tr><th width="30%">Product Code</th><td width="70%">JSLB6355S</td></tr>
          <tr><th>Brand</th><td>Jalite</td></tr>
          <tr><th width="30%">Manufacturer's Part Number</th><td width="70%">LB6355SV</td></tr>
          <tr><th rowspan="2">Compatible Products</th><td>Foam Extinguishers</td></tr>
          <tr><td>AFFF Extinguishers</td></tr>
          <tr><th rowspan="1">Material</th><td>Rigid Plastic</td></tr>
        </tbody></table>
        <div class="images"><meta itemprop="image" content="https://www.safelincs.co.uk/images/related/210x210.jpg"></div>
        </body></html>
        """;

    [Fact]
    public void SafelincsPage_ReadsProductDetails()
    {
        var page = SafelincsPageParser.Parse(ProductPage);

        Assert.Equal("Fire Extinguisher Self-Adhesive Signs", page.Title);
        Assert.Equal("JSLB6355S", page.ProductCode);
        Assert.Equal("Jalite", page.Brand);
        Assert.Equal("LB6355SV", page.Mpn);
        Assert.Equal(4.39m, page.PriceIncVat);
        Assert.Equal(3.66m, page.PriceExVat);
        Assert.Equal(4.20m, page.StandardDeliveryIncVat);
        Assert.Equal(1004, page.VariantId);
        Assert.Equal("Fire Extinguisher ID Signs", page.Category);
        Assert.Equal(new[] { "Extinguishers & Equipment", "Fire Extinguisher ID Signs" }, page.Breadcrumbs);
        Assert.True(page.HasBuyBox);
        Assert.True(page.InStock);
        Assert.False(page.AvailableToOrder);
        Assert.Equal(20, page.StockLevel);
        Assert.Equal("Tomorrow", page.DispatchText);
    }

    [Fact]
    public void SafelincsPage_ImagesSpecificsAndDescription()
    {
        var page = SafelincsPageParser.Parse(ProductPage);

        // Gallery only (not the related-products block further down).
        Assert.Equal(2, page.ImageUrls.Count);
        Assert.StartsWith("https://www.safelincs.co.uk/images/sign-front", page.ImageUrls[0]);
        // Rowspan values are joined; code/brand aren't specifics; MPN is.
        Assert.Equal("Foam Extinguishers, AFFF Extinguishers", page.Specifics["Compatible Products"]);
        Assert.Equal("Rigid Plastic", page.Specifics["Material"]);
        Assert.Equal("LB6355SV", page.Specifics["MPN"]);
        Assert.False(page.Specifics.ContainsKey("Product Code"));
        Assert.False(page.Specifics.ContainsKey("Brand"));
        // Links to Safelincs are removed from the description (eBay doesn't allow off-site links), text kept.
        Assert.Contains("other signs", page.DescriptionHtml);
        Assert.DoesNotContain("<a", page.DescriptionHtml);
        Assert.DoesNotContain("BLOCK", page.DescriptionHtml);
    }

    [Fact]
    public void SafelincsPage_ReadsVariantDropdown()
    {
        var options = SafelincsPageParser.Parse(ProductPage).Options;
        Assert.Equal(new[] { 1003L, 1004L, 1005L }, options.Select(o => o.Id));
        Assert.Equal(new[] { "Water", "Foam", "Powder ABC" }, options.Select(o => o.Label));
    }

    [Theory]
    [InlineData("<p> 5 In Stock </p>", "in-stock", "InStock", true, 5, false, false)]
    [InlineData("<p> In Stock </p>", "in-stock", "InStock", true, null, false, false)]
    [InlineData("<p> Available to Order </p>", "in-stock", "BackOrder", true, null, true, false)]
    [InlineData("<p> Discontinued </p><i>This product has been discontinued</i>", "no-stock", "Discontinued", false, 0, false, true)]
    public void SafelincsPage_StockStates(string inner, string cls, string availability, bool inStock, int? level, bool toOrder, bool discontinued)
    {
        var html = $"""<h1>X</h1><div itemprop="offers"><div class="stock {cls}"><link itemprop="availability" href="https://schema.org/{availability}">{inner}</div></div>""";
        var page = SafelincsPageParser.Parse(html);

        Assert.True(page.HasBuyBox);
        Assert.Equal(inStock, page.InStock);
        Assert.Equal(level, page.StockLevel);
        Assert.Equal(toOrder, page.AvailableToOrder);
        Assert.Equal(discontinued, page.IsDiscontinued);
    }

    [Fact]
    public void SafelincsPage_WithoutBuyBox_IsFlagged()
    {
        var page = SafelincsPageParser.Parse("<h1>Fire Safety Training Course</h1><div class=\"description\"><p>Course</p></div>");
        Assert.False(page.HasBuyBox);
        Assert.Empty(page.Options);
    }

    [Fact]
    public void Sitemap_KeepsProductPagesOnly()
    {
        const string xml = """
            <urlset><url><loc>https://www.safelincs.co.uk/category/signs</loc></url>
            <url><loc>https://www.safelincs.co.uk/product/fire-blanket</loc></url>
            <url><loc> https://www.safelincs.co.uk/product/fire-blanket </loc></url>
            <url><loc>https://www.safelincs.co.uk/product/co2-extinguisher</loc></url>
            <url><loc>https://evil.example.com/product/x</loc></url></urlset>
            """;
        var urls = SafelincsCatalogImportService.ParseSitemapProductUrls(xml);
        Assert.Equal(new[] { "https://www.safelincs.co.uk/product/fire-blanket", "https://www.safelincs.co.uk/product/co2-extinguisher" }, urls);
        Assert.Equal("fire-blanket", SafelincsCatalogImportService.Slug(urls[0]));
    }

    [Fact]
    public void ApplyPage_MapsVariantAndStock()
    {
        var page = SafelincsPageParser.Parse(ProductPage);
        page.Title += " - Foam";
        page.VariantLabel = "Foam";
        var product = new SupplierProduct { Supplier = "Safelincs" };

        SafelincsCatalogImportService.ApplyPage(product, page, "fire-extinguisher-self-adhesive-signs", "https://www.safelincs.co.uk/product/fire-extinguisher-self-adhesive-signs?option=1004", hasVariants: true);

        Assert.Equal("safelincs:fire-extinguisher-self-adhesive-signs", product.GroupKey);
        Assert.Equal("JSLB6355S", product.SupplierSku);
        Assert.Equal("Foam", product.VariantName);
        Assert.Equal("Fire Extinguisher ID Signs", product.CategoryName);
        Assert.Equal(20, product.StockLevel);
        Assert.Equal(4.39m, product.RrpPrice);
        Assert.Equal("Jalite", product.Brand);
        Assert.Contains("Material", product.SpecificsJson);
    }

    // ------------------------------------------------------------------ price list

    [Fact]
    public void PriceList_FindsHeaderAndColumns()
    {
        var rows = new List<IReadOnlyList<string?>>
        {
            new[] { "Safelincs Trade Price List – January", null, null, null, null, null, null },
            new string?[7],
            new[] { "Product Code", "Description", "Category", "Price (ex VAT)", "Difference", "Delivery", "With Delivery and tax" },
            new[] { "JSLB6355S", "Foam sign", "Signs", "2.10", "-1.56", "3.50", "6.72" },
            new[] { "STJG5BATT", "G5 Battery", "AEDs & Accessories", "£241.50", "", "", "289.80" },
            new[] { "", "blank code", "", "1", "", "", "" },
            new[] { "NOPRICE", "no price", "", "", "", "", "" },
        };
        var warnings = new List<string>();

        var result = SupplierPriceListParser.ParseTable(rows, sheetName: null, warnings);

        Assert.Empty(warnings);
        Assert.Equal(2, result.Count);
        Assert.Equal(new PriceListRow("JSLB6355S", "Foam sign", "Signs", 2.10m, 3.50m), result[0]);
        Assert.Equal(241.50m, result[1].Price);
        Assert.Null(result[1].DeliveryCost);
    }

    [Fact]
    public void PriceList_WithoutCodeColumn_Warns()
    {
        var warnings = new List<string>();
        var result = SupplierPriceListParser.ParseTable(new List<IReadOnlyList<string?>> { new[] { "Name", "Price" } }, "Sheet1", warnings);
        Assert.Empty(result);
        Assert.Contains(warnings, w => w.Contains("Product Code"));
    }

    [Fact]
    public void PriceList_Csv()
    {
        var csv = "Product Code,Description,Price (ex VAT),Delivery\nABC1,\"Sign, large\",12.5,4\n";
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));
        var (rows, warnings) = SupplierPriceListParser.Parse(stream, "prices.csv");
        Assert.Empty(warnings);
        var row = Assert.Single(rows);
        Assert.Equal("Sign, large", row.Description);
        Assert.Equal(12.5m, row.Price);
        Assert.Equal(4m, row.DeliveryCost);
    }

    [Fact]
    public void PriceList_ApplyPrice_SetsTradePriceDeliveryAndSuggestion()
    {
        var product = new SupplierProduct { Supplier = "Safelincs", SupplierSku = "ABC1", SupplierPrice = 20m, PriceIsEstimate = true };
        var item = new SupplierPriceListItem { ProductCode = "ABC1", Price = 10m, DeliveryCost = 4m, Category = "Signs", UploadedAtUtc = new DateTime(2026, 1, 5) };
        var supplier = new SupplierDto { Code = "Safelincs", PricesIncludeVat = false, VatRatePercent = 20m, DefaultMarginPercent = 20m };

        SupplierPriceListService.ApplyPrice(product, item, supplier);

        Assert.Equal(10m, product.SupplierPrice);
        Assert.False(product.PriceIsEstimate);
        Assert.Equal(4m, product.DeliveryCost);
        Assert.Equal("Signs", product.CategoryName);
        Assert.Equal(14.99m, PricingMath.SuggestPrice(product.SupplierPrice, product.RrpPrice, supplier).Price); // 10 + VAT = 12, +20% = 14.40 → 14.99
    }

    // ------------------------------------------------------------------ categories

    [Theory]
    [InlineData("Home > Fire Safety > Fire Extinguisher ID Signs", "Fire Extinguisher ID Signs")]
    [InlineData("  Nutrients  ", "Nutrients")]
    [InlineData("Home\n   Products\n   Ribbons &amp; Bows\n", "Ribbons & Bows")]
    [InlineData("Home", null)]
    [InlineData("", null)]
    public void CategoryText_KeepsLastMeaningfulPart(string raw, string? expected)
        => Assert.Equal(expected, CategoryText.Clean(raw));

    [Fact]
    public async Task Enrichment_CopiesSupplierCategoryAndPerProductDelivery()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.Suppliers.Add(new Supplier { Code = "Safelincs", Name = "Safelincs", WebsiteHost = "safelincs.co.uk", PricesIncludeVat = false, VatRatePercent = 20m, IsActive = true });
        await ctx.SaveChangesAsync();
        var catalog = new SupplierCatalogService(ctx);
        var enrichment = new ProductEnrichmentService(ctx, Array.Empty<ISupplierProductEnricher>(), catalog);
        var product = new SupplierProduct { Supplier = "Safelincs", Title = "Sign", CategoryName = "Fire Signs", SupplierPrice = 10m, DeliveryCost = 3.50m };
        var listing = new Listing { Title = "Sign", Price = 0m };

        await enrichment.FillListingFromProductAsync(listing, product);

        Assert.Equal("Fire Signs", listing.SupplierCategory);
        Assert.Equal(4.20m, listing.DeliveryCost); // 3.50 + 20% VAT
        Assert.Equal("Safelincs delivery", listing.DeliveryOptionName);
    }

    // ------------------------------------------------------------------ disabled suppliers

    [Fact]
    public async Task DisabledSupplier_IsFoundForProductAndUrlListings()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.Suppliers.Add(new Supplier { Code = "Safelincs", Name = "Safelincs", WebsiteHost = "safelincs.co.uk", IsActive = false });
        ctx.Suppliers.Add(new Supplier { Code = "DropShipMall", Name = "DropShipMall", WebsiteHost = "dropshipmall.co.uk", IsActive = true });
        var sfl = new SupplierProduct { Supplier = "Safelincs", Title = "A" };
        var dsm = new SupplierProduct { Supplier = "DropShipMall", Title = "B" };
        ctx.SupplierProducts.AddRange(sfl, dsm);
        await ctx.SaveChangesAsync();
        var catalog = new SupplierCatalogService(ctx);

        Assert.Equal("Safelincs", await catalog.FindDisabledSupplierNameAsync(new Listing { SupplierProductId = sfl.Id }));
        Assert.Equal("Safelincs", await catalog.FindDisabledSupplierNameAsync(new Listing { SourceUrl = "https://www.safelincs.co.uk/product/x" }));
        Assert.Null(await catalog.FindDisabledSupplierNameAsync(new Listing { SupplierProductId = dsm.Id }));
        Assert.Null(await catalog.FindDisabledSupplierNameAsync(new Listing { SourceUrl = "https://www.dropshipmall.co.uk/x" }));
    }

    [Fact]
    public async Task Publish_IsRefusedForDisabledSupplier()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.Suppliers.Add(new Supplier { Code = "Safelincs", Name = "Safelincs", WebsiteHost = "safelincs.co.uk", IsActive = false });
        var listing = new Listing
        {
            Title = "Fire blanket 1m x 1m", Price = 9.99m, OwnerUserId = 1, CategoryId = "1",
            PaymentPolicyId = "p", FulfillmentPolicyId = "f", ReturnPolicyId = "r", Marketplace = "eBay",
            ImageUrlsJson = JsonSerializer.Serialize(new[] { "https://x/a.jpg" }),
            SourceUrl = "https://www.safelincs.co.uk/product/fire-blanket"
        };
        ctx.Listings.Add(listing);
        await ctx.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ListingPublishHelper.PrepareAsync(
            listing, ctx, new Mock<IEbayService>().Object, new EbaySettings(), new SkuService(ctx, new SupplierCatalogService(ctx))));
        Assert.Contains("disabled", ex.Message);
    }

    [Fact]
    public void PublishResult_ClearsZeroedQuantity()
    {
        var listing = new Listing { QuantityZeroedAtUtc = DateTime.UtcNow };
        ListingPublishHelper.ApplyPublishResult(listing, new EbayListingResponse { ListingId = "1", Sku = "S", OfferId = "O" });
        Assert.Null(listing.QuantityZeroedAtUtc);
    }
}
