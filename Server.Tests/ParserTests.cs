using System.Text.Json.Nodes;
using Server.Services;
using Xunit;

namespace Server.Tests;

/// <summary>Supplier feed / page parsers – samples copied from the real DropShipMall and Eden formats.</summary>
public class ParserTests
{
    // ---------------- DropShipMall CSV

    [Fact]
    public void DropShipMallCsv_HandlesQuotesInsideValues_AndBackslashEscapes()
    {
        const string csv =
            "\"Product Code\",\"Product Name\",Images,Discontinued,\"Piece Price\"\n" +
            "TOOL11,1.5\" Steel Pins 500g,https://x/a.jpg,No,10.53\n" +
            "X13518,\"Cypress spray 21.5\\\"\",\"https://x/b.jpeg https://x/c.jpeg\",No,3.74\n";

        var rows = DropShipMallFeedSyncService.ParseCsv(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal("1.5\" Steel Pins 500g", DropShipMallFeedSyncService.Pick(rows[0], "Product Name"));
        Assert.Equal("10.53", DropShipMallFeedSyncService.Pick(rows[0], "Piece Price"));
        Assert.Equal("Cypress spray 21.5\"", DropShipMallFeedSyncService.Pick(rows[1], "Product Name"));
        Assert.Equal("https://x/b.jpeg https://x/c.jpeg", DropShipMallFeedSyncService.Pick(rows[1], "Images"));
    }

    [Fact]
    public void DropShipMallCsv_QuotedFieldsCanSpanLines_AndHeadersAreNormalised()
    {
        const string csv = "Handle,Title,Body(HTML),Variant Price\nh1,Pot,\"<p>Line one\nLine two</p>\",2.50\n";

        var row = Assert.Single(DropShipMallFeedSyncService.ParseCsv(csv));

        // "Body (HTML)" matches the "Body(HTML)" header because keys are normalised.
        Assert.Equal("<p>Line one\nLine two</p>", DropShipMallFeedSyncService.Pick(row, "Body (HTML)"));
    }

    [Fact]
    public void DropShipMallCsv_SkipsRowsWithWrongColumnCount_AndPickIgnoresNA()
    {
        const string csv = "A,B,C\n1,2,3\n1,2\n4,N/A,6\n";

        var rows = DropShipMallFeedSyncService.ParseCsv(csv);

        Assert.Equal(2, rows.Count);
        Assert.Null(DropShipMallFeedSyncService.Pick(rows[1], "B"));
        Assert.Equal("6", DropShipMallFeedSyncService.Pick(rows[1], "B", "C"));
    }

    // ---------------- DropShipMall availability text

    [Theory]
    [InlineData("More than 150", "InStock", null)]
    [InlineData("12", "InStock", 12)]
    [InlineData("3", "Low", 3)]
    [InlineData("0", "OutOfStock", 0)]
    [InlineData("Out of Stock", "OutOfStock", 0)]
    [InlineData("", "Unknown", null)]
    [InlineData("Call us", "Unknown", null)]
    public void DropShipMallAvailability_IsInterpreted(string text, string status, int? level)
    {
        var (s, l) = DropShipMallStockChecker.Interpret(text);
        Assert.Equal(status, s);
        Assert.Equal(level, l);
    }

    // ---------------- Eden (Shopify)

    private const string ShopifyJson = """
        {"products":[
          {"id":7110264258620,"title":"Omega Eurowing 600W Black Digi Kit","handle":"omega-eurowing-600w-black-digi-kit",
           "body_html":"<p>Great kit</p>","vendor":"Omega Lighting","product_type":"Lighting",
           "options":[{"name":"Title"}],
           "images":[{"id":1,"src":"//cdn.shopify.com/a.jpg"}],
           "variants":[{"id":40587117035580,"title":"Default Title","option1":"Default Title","sku":"HYK-0298","price":"33.50","available":true}]},
          {"id":2,"title":"Root Riot","handle":"root-riot","options":[{"name":"Size"}],"images":[],
           "variants":[
             {"id":11,"title":"50 Bag","option1":"50 Bag","sku":"RR-50","price":"7.99","available":true},
             {"id":12,"title":"100 Bag","option1":"100 Bag","sku":"RR-100","price":"12.99","available":false}]}
        ]}
        """;

    [Fact]
    public void ShopifyCatalogue_ParsesProductsVariantsAndOptions()
    {
        var products = EdenCatalogImportService.ShopifyProduct.ParseList(ShopifyJson);

        Assert.Equal(2, products.Count);
        var kit = products[0];
        Assert.Equal("Omega Lighting", kit.Vendor);
        Assert.Equal("https://cdn.shopify.com/a.jpg", Assert.Single(kit.ImageUrls));
        var kitVariant = Assert.Single(kit.Variants);
        Assert.True(kitVariant.IsDefaultTitle);
        Assert.Empty(kitVariant.Options); // "Default Title" is not a real option
        Assert.Equal(33.50m, kitVariant.Price);

        var riot = products[1];
        Assert.Equal(2, riot.Variants.Count);
        Assert.Equal("100 Bag", riot.Variants[1].Options["Size"]);
        Assert.False(riot.Variants[1].Available);
    }

    [Fact]
    public void ShopifyCatalogue_ToleratesMissingOrOddFields()
    {
        var products = EdenCatalogImportService.ShopifyProduct.ParseList("""{"products":[{"id":"5","title":"X","variants":[{"id":null},{"id":9,"price":"abc"}]}]}""");

        var product = Assert.Single(products);
        Assert.Equal(5, product.Id); // numeric strings are accepted
        var variant = Assert.Single(product.Variants); // variant without id skipped
        Assert.Null(variant.Price);
    }

    [Fact]
    public void EdenTradeCards_ReadStockFromCollectionPage()
    {
        const string html = """
            <eden-trade-card data-product-id="1"><script type="application/json" data-eden-trade-product-data>
              {"id":1,"variants":[{"id":40602499743804,"sku":"HYD-0043","rrp":5376,"avail":false,"qty":0,"tracked":true},
                                  {"id":40588052594748,"sku":"HYN-0466","rrp":1395,"avail":true,"qty":51,"tracked":true}]}
            </script></eden-trade-card>
            <script type="application/json" data-eden-trade-product-data>{"id":2,"variants":[{"id":7,"avail":true,"qty":null,"tracked":false}]}</script>
            <script type="application/json" data-eden-trade-product-data>not json</script>
            """;

        var cards = EdenCatalogImportService.ParseTradeCards(html);

        Assert.Equal(3, cards.Count);
        Assert.Equal(51, cards.Single(c => c.VariantId == 40588052594748).Qty);
        Assert.False(cards.Single(c => c.VariantId == 40602499743804).Available);
        Assert.False(cards.Single(c => c.VariantId == 7).Tracked);
    }

    [Fact]
    public void EdenProductPage_ReadVariantStock_FromTradeJsonWithHtmlComments()
    {
        const string html = """
            <script type="application/json">{"id":1,"selected":40587117035580,"variants":[<!-- BEGIN app snippet: eden-trade-variant-json -->{"id":40587117035580,"title":"Default Title","sku":"HYK-0298","p":2750,"rrp":3350,"avail":true,"qty":51,"tracked":true} <!-- END app snippet -->]}</script>
            <script>var other = 1;</script>
            """;

        var (qty, available) = EdenStockChecker.ReadVariantStock(html, 40587117035580);
        Assert.Equal(51, qty);
        Assert.True(available);

        var (missingQty, missingAvail) = EdenStockChecker.ReadVariantStock(html, 999);
        Assert.Null(missingQty);
        Assert.Null(missingAvail);
    }

    // ---------------- eBay category aspects

    [Fact]
    public void EbayAspects_RequiredFirst_ThenRecommended_WithModes()
    {
        var node = JsonNode.Parse("""
            {"aspects":[
              {"localizedAspectName":"Scent","aspectConstraint":{"aspectRequired":false,"aspectUsage":"OPTIONAL"}},
              {"localizedAspectName":"Colour","aspectConstraint":{"aspectRequired":false,"aspectUsage":"RECOMMENDED","itemToAspectCardinality":"MULTI"}},
              {"localizedAspectName":"Type","aspectConstraint":{"aspectRequired":true,"aspectMode":"FREE_TEXT"},
               "aspectValues":[{"localizedValue":"Pillar Candle"},{"localizedValue":"Tealight"}]},
              {"localizedAspectName":"Brand","aspectConstraint":{"aspectRequired":"true","aspectMode":"SELECTION_ONLY"}}
            ]}
            """);

        var aspects = EbayService.ParseAspects(node);

        Assert.Equal(new[] { "Type", "Colour", "Scent", "Brand" }, aspects.Select(a => a.Name));
        var type = aspects[0];
        Assert.True(type.Required);
        Assert.False(type.SelectionOnly);
        Assert.Equal(new[] { "Pillar Candle", "Tealight" }, type.Values);
        Assert.True(aspects[1].MultipleValues);
        // "true" as a string isn't a JSON boolean: treated as not required (no exception).
        Assert.False(aspects[3].Required);
        Assert.True(aspects[3].SelectionOnly);
    }
}
