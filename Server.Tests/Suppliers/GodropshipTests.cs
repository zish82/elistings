using System.Text.Json;
using Server.Data;
using Server.Services;
using Xunit;

namespace Server.Tests;

public class GodropshipTests
{
    // Trimmed from a real product page (Nov 2026): JSON-LD Product, buy box spans, breadcrumb.
    private const string ProductHtml = """
        <html><head>
        <script type="application/ld+json">
        {"@context":"https:\/\/schema.org\/","@type":"Product","brand":{"@type":"Brand","name":"Go Dropship"},
         "image":["https:\/\/www.godropship.co.uk\/uploadfile\/images\/600517\/a.png","https:\/\/www.godropship.co.uk\/uploadfile\/images\/600517\/b.png"],
         "description":"<span>6L Digital Air Fryer<\/span><br \/>Key features",
         "mpn":"EPG-76816","name":"Maya Range 6L Air Fryer 1500W",
         "offers":{"@type":"Offer","availability":"https:\/\/schema.org\/InStock","price":"66.88","priceCurrency":"GBP"},
         "sku":"EPG-76816"}
        </script></head><body>
        <div class="crumbs"><ul class="clearfix">
          <li><a href="https://www.godropship.co.uk/index"><span class="crumbs-fisrt"><em>Home</em></span></a></li>
          <li><a href="#"><span class="crumbs-order">Home &amp; Garden </span></a></li>
          <li><a href="#"><span class="crumbs-order">Kitchen &amp; Dining</span></a></li>
        </ul></div>
        <h1>Maya Range 6L Air Fryer 1500W</h1>
        <span class="span1">Weight :</span><span class="details-weight" id="change_child_weight">3000.00</span> <span>g</span>
        <span class="span1">HOLD  :</span><span class="details-weight details-hold" id="change_child_weight">YES</span>
        <span>UK Stock :</span><span class="details-in-mgr f60" id="change_child_inventory">100</span>
        <span class="span1">Price :</span><span class="details-price-n" id="change_child_price">£66.50</span>
        </body></html>
        """;

    [Fact]
    public void Parse_ReadsJsonLdAndBuyBox()
    {
        var page = GodropshipPageParser.Parse(ProductHtml);

        Assert.True(page.IsProduct);
        Assert.Equal("Maya Range 6L Air Fryer 1500W", page.Title);
        Assert.Equal("EPG-76816", page.ItemCode);
        Assert.Equal(66.50m, page.Price); // the buy box wins over the JSON-LD offer
        Assert.Equal(100, page.StockLevel);
        Assert.True(page.InStock);
        Assert.Equal(3000m, page.WeightGrams); // "HOLD: YES" reuses the weight id and is ignored
        Assert.Equal(new[] { "Home & Garden", "Kitchen & Dining" }, page.Breadcrumbs);
        Assert.Equal("Kitchen & Dining", page.Category);
        Assert.Equal(2, page.ImageUrls.Count);
        Assert.StartsWith("https://www.godropship.co.uk/uploadfile/", page.ImageUrls[0]);
        Assert.Contains("6L Digital Air Fryer", page.DescriptionHtml);
    }

    [Fact]
    public void Parse_NonProductPage_IsNotAProduct()
    {
        var page = GodropshipPageParser.Parse("<html><body><h1>New Arrival</h1>" + new string(' ', 600) + "</body></html>");
        Assert.False(page.IsProduct);
    }

    [Fact]
    public void Parse_ZeroStock_IsOutOfStock()
    {
        var page = GodropshipPageParser.Parse(ProductHtml.Replace(">100</span>", ">0</span>"));
        Assert.Equal(0, page.StockLevel);
        Assert.False(page.InStock);
        Assert.Equal(("OutOfStock", (int?)0, "UK Stock: 0"), GodropshipStockChecker.Interpret(page));
    }

    [Theory]
    [InlineData(3, "Low")]
    [InlineData(6, "InStock")]
    public void StockChecker_InterpretsLevels(int level, string status)
    {
        var (s, l, _) = GodropshipStockChecker.Interpret(new GodropshipPage { StockLevel = level, InStock = true });
        Assert.Equal(status, s);
        Assert.Equal(level, l);
    }

    [Fact]
    public void ParseSitemap_KeepsOnlyGodropshipUrls()
    {
        var xml = """
            <urlset><url><loc>https://www.godropship.co.uk/maya-air-fryer</loc></url>
            <url><loc>https://www.godropship.co.uk/bag-strap---gold</loc></url>
            <url><loc>https://example.com/other</loc></url>
            <url><loc>https://www.godropship.co.uk/maya-air-fryer</loc></url></urlset>
            """;
        Assert.Equal(new[] { "https://www.godropship.co.uk/maya-air-fryer", "https://www.godropship.co.uk/bag-strap---gold" },
            GodropshipPageParser.ParseSitemap(xml));
    }

    [Fact]
    public void ApplyPage_MapsToSupplierProduct_WithDeliveryIncluded()
    {
        var page = GodropshipPageParser.Parse(ProductHtml);
        var product = new SupplierProduct { Supplier = "Godropship", DeliveryCost = 4.99m, RemovedFromSupplierAtUtc = DateTime.UtcNow };

        GodropshipCatalogImportService.ApplyPage(product, page, "https://www.godropship.co.uk/maya-air-fryer");

        Assert.Equal("EPG-76816", product.SupplierSku);
        Assert.Equal("EPG-76816", product.ExternalProductId);
        Assert.Equal("maya-air-fryer", product.Handle);
        Assert.Equal(66.50m, product.SupplierPrice);
        Assert.False(product.PriceIsEstimate);
        Assert.Equal(0m, product.DeliveryCost); // Godropship prices include UK delivery
        Assert.Equal(100, product.StockLevel);
        Assert.Equal("Kitchen & Dining", product.CategoryName);
        Assert.Equal("Home & Garden › Kitchen & Dining", product.CategoryPath);
        Assert.Equal(2, JsonSerializer.Deserialize<List<string>>(product.ImageUrlsJson!)!.Count);
        Assert.Null(product.RemovedFromSupplierAtUtc); // back on the site
    }

    [Fact]
    public void MarkRemovedProducts_HidesOnlyProductsGoneFromTheSitemap()
    {
        var kept = new SupplierProduct { Handle = "kept", StockLevel = 5 };
        var gone = new SupplierProduct { Handle = "gone", StockLevel = 5 };
        var failedToLoad = new SupplierProduct { Handle = "slow", StockLevel = 5 };
        var notes = new List<string>();

        GodropshipCatalogImportService.MarkRemovedProducts(new List<SupplierProduct> { kept, gone, failedToLoad },
            new HashSet<SupplierProduct> { kept }, new HashSet<string> { "kept", "slow" }, notes);

        Assert.Null(kept.RemovedFromSupplierAtUtc);
        Assert.Null(failedToLoad.RemovedFromSupplierAtUtc); // still in the sitemap: left alone
        Assert.NotNull(gone.RemovedFromSupplierAtUtc);
        Assert.Equal(0, gone.StockLevel);
    }

    [Fact]
    public void StockChecker_HandlesGodropshipProductsAndUrlsOnly()
    {
        var checker = new GodropshipStockChecker(new GodropshipWebsiteClient(new HttpClient()));
        Assert.True(checker.CanCheck(new SupplierStockQuery(null, null, new SupplierProduct { Supplier = "Godropship" })));
        Assert.True(checker.CanCheck(new SupplierStockQuery("https://www.godropship.co.uk/maya-air-fryer", null, null)));
        Assert.False(checker.CanCheck(new SupplierStockQuery("https://www.safelincs.co.uk/x", null, null)));
    }
}
