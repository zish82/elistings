using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Moq;
using Server.Controllers;
using Server.Data;
using Server.Services;
using Shared;
using Xunit;

namespace Server.Tests;

/// <summary>Sales report: reading eBay orders, matching cost of sales, earnings/profit/ROI and month totals.</summary>
public class SalesReportTests
{
    private const string OrdersJson = """
        {
          "total": 2,
          "orders": [
            {
              "orderId": "12-34567-89012",
              "creationDate": "2026-09-15T10:30:00.000Z",
              "lastModifiedDate": "2026-09-16T08:00:00.000Z",
              "orderFulfillmentStatus": "FULFILLED",
              "orderPaymentStatus": "PAID",
              "buyer": { "username": "buyer_one" },
              "cancelStatus": { "cancelState": "NONE_REQUESTED" },
              "pricingSummary": {
                "priceSubtotal": { "value": "14.99", "currency": "GBP" },
                "deliveryCost": { "value": "0.00", "currency": "GBP" },
                "total": { "value": "14.99", "currency": "GBP" }
              },
              "paymentSummary": { "totalDueSeller": { "value": "12.69", "currency": "GBP" }, "refunds": [] },
              "totalMarketplaceFee": { "value": "2.30", "currency": "GBP" },
              "fulfillmentHrefs": [ "https://api.ebay.com/sell/fulfillment/v1/order/12-34567-89012/shipping_fulfillment/1" ],
              "lineItems": [
                { "lineItemId": "L1", "sku": "DSM-S16301", "legacyItemId": "1111", "title": "Ivory Pillar Candle", "quantity": 1,
                  "lineItemCost": { "value": "14.99", "currency": "GBP" } }
              ]
            },
            {
              "orderId": "98-76543-21098",
              "creationDate": "2026-10-02T09:00:00.000Z",
              "orderFulfillmentStatus": "NOT_STARTED",
              "orderPaymentStatus": "PARTIALLY_REFUNDED",
              "buyer": { "username": "buyer_two" },
              "pricingSummary": {
                "priceSubtotal": { "value": "20.00", "currency": "GBP" },
                "deliveryCost": { "value": "3.00", "currency": "GBP" },
                "total": { "value": "23.00", "currency": "GBP" }
              },
              "paymentSummary": {
                "refunds": [ { "amount": { "value": "5.00", "currency": "GBP" }, "refundStatus": "REFUNDED" },
                             { "amount": { "value": "9.99", "currency": "GBP" }, "refundStatus": "FAILED" } ]
              },
              "lineItems": [
                { "lineItemId": "L2", "sku": "MAN-L77", "title": "Something else", "quantity": 2, "lineItemCost": { "value": "20.00", "currency": "GBP" } }
              ]
            }
          ]
        }
        """;

    private const string TrackingJson = """
        { "fulfillments": [ { "shipmentTrackingNumber": "JD000123456GB", "shippingCarrierCode": "Royal Mail", "shippedDate": "2026-09-16T07:00:00.000Z" } ] }
        """;

    [Fact]
    public void Parser_ReadsOrdersLinesRefundsAndFees()
    {
        var (orders, total) = EbayOrderParser.ParseOrders(OrdersJson);

        Assert.Equal(2, total);
        var first = orders[0];
        Assert.Equal("12-34567-89012", first.OrderId);
        Assert.Equal(new DateTime(2026, 9, 15, 10, 30, 0, DateTimeKind.Utc), first.CreatedAtUtc);
        Assert.Equal(14.99m, first.SaleTotal);    // what the buyer paid, not totalDueSeller (net of fees)
        Assert.Equal(2.30m, first.MarketplaceFee);
        Assert.True(first.HasFulfillments);
        Assert.Equal("DSM-S16301", Assert.Single(first.Lines).Sku);

        var second = orders[1];
        Assert.Equal(23.00m, second.SaleTotal);   // no totalDueSeller: order total
        Assert.Equal(5.00m, second.Refunded);     // failed refund ignored
        Assert.Null(second.MarketplaceFee);
        Assert.False(second.HasFulfillments);
        Assert.Equal(2, second.Lines[0].Quantity);
    }

    [Fact]
    public void Parser_WorksOutFeesFromTotalDueSeller_WhenNotReported()
    {
        // Order 13-15324-44412: £25.65 sold, eBay pays £22.13 → £3.52 fees (9.5% + 40p + 0.35%, + VAT).
        const string json = """
            { "total": 1, "orders": [ { "orderId": "13-15324-44412", "creationDate": "2026-10-22T14:56:06.000Z",
              "pricingSummary": { "priceSubtotal": { "value": "25.65" }, "deliveryCost": { "value": "0.00" }, "total": { "value": "25.65" } },
              "paymentSummary": { "totalDueSeller": { "value": "22.13" } },
              "lineItems": [ { "lineItemId": "L", "title": "Oil pump", "quantity": 1, "lineItemCost": { "value": "25.65" } } ] } ] }
            """;
        var order = EbayOrderParser.ParseOrders(json).Orders.Single();
        Assert.Equal(25.65m, order.SaleTotal);
        Assert.Equal(3.52m, order.MarketplaceFee);
    }

    [Fact]
    public void Parser_ReadsTracking()
    {
        var tracking = EbayOrderParser.ParseTracking(TrackingJson);
        Assert.Equal("JD000123456GB", tracking?.TrackingNumber);
        Assert.Equal("Royal Mail", tracking?.Carrier);
        Assert.Null(EbayOrderParser.ParseTracking("""{ "fulfillments": [] }"""));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(EbayScopes.Original, false)]
    [InlineData(EbayScopes.Current, true)]
    public void Scopes_KnowWhetherOrdersCanBeRead(string? scopes, bool expected)
        => Assert.Equal(expected, EbayScopes.CanReadOrders(scopes));

    [Fact]
    public void Report_EarningProfitRoi()
    {
        var order = new EbayOrder { SaleTotal = 20m, RefundedTotal = 2m, Fees = 3m, SupplierPrice = 10m };
        Assert.Equal(15m, SalesReport.Earning(order));
        Assert.Equal(5m, SalesReport.Profit(order));
        Assert.Equal(50m, SalesReport.RoiPercent(5m, 10m));
        Assert.Null(SalesReport.Profit(new EbayOrder { SaleTotal = 20m })); // unknown supplier price
        Assert.Null(SalesReport.RoiPercent(5m, 0m));
    }

    [Fact]
    public void Report_MonthTotals_LeaveUnknownCostsOutOfProfit()
    {
        var orders = new[]
        {
            new EbayOrder { CreatedAtUtc = new DateTime(2026, 10, 5, 12, 0, 0), SaleTotal = 20m, Fees = 3m, SupplierPrice = 10m },
            new EbayOrder { CreatedAtUtc = new DateTime(2026, 10, 6, 12, 0, 0), SaleTotal = 30m, Fees = 4m },            // no cost
            new EbayOrder { CreatedAtUtc = new DateTime(2026, 9, 30, 12, 0, 0), SaleTotal = 10m, Fees = 1m, SupplierPrice = 6m },
            new EbayOrder { CreatedAtUtc = new DateTime(2026, 10, 7, 12, 0, 0), SaleTotal = 0m, Fees = 0m, SupplierPrice = 0m, CancelState = "CANCELED" }
        };

        var months = SalesReport.Months(orders);

        Assert.Equal(new[] { "2026-10", "2026-09" }, months.Select(m => m.Month));
        var october = months[0];
        Assert.Equal(2, october.Orders);              // the cancelled one isn't counted
        Assert.Equal(50m, october.Sales);
        Assert.Equal(43m, october.Earning);
        Assert.Equal(10m, october.SupplierPrice);
        Assert.Equal(7m, october.Profit);             // only the costed order: 17 - 10
        Assert.Equal(70m, october.RoiPercent);
        Assert.Equal(1, october.OrdersWithoutCost);
    }

    [Fact]
    public void Report_MonthUsesUkTime()
    {
        // 23:30 UTC on 31 Aug in summer is 00:30 on 1 Sep in the UK.
        Assert.Equal("2026-09", SalesReport.MonthOf(new DateTime(2026, 8, 31, 23, 30, 0, DateTimeKind.Utc)));
    }

    [Theory]
    [InlineData("CANCELED", 0, 10, "FULFILLED", "Cancelled")]
    [InlineData(null, 10, 10, "FULFILLED", "Refunded")]
    [InlineData(null, 2, 10, "FULFILLED", "Part refunded")]
    [InlineData(null, 0, 10, "NOT_STARTED", "To dispatch")]
    [InlineData(null, 0, 10, "FULFILLED", "Dispatched")]
    public void Report_Status(string? cancel, int refunded, int total, string fulfillment, string expected)
        => Assert.Equal(expected, SalesReport.Status(new EbayOrder { CancelState = cancel, RefundedTotal = refunded, SaleTotal = total, FulfillmentStatus = fulfillment }));

    // ------------------------------------------------------------------ full sync

    private static (AppDbContext Ctx, EbayOrderSyncService Sync, Mock<IEbayService> Ebay) SyncSetup()
    {
        var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx); // DropShipMall, prefix DSM, prices ex VAT
        ctx.Users.Add(new AppUser { Id = 1, Email = "seller@example.com", Role = AuthRoles.Lister });
        ctx.EbayTokens.Add(new EbayTokenInfo { Id = 5, UserId = 1, Name = "Shop", Scopes = EbayScopes.Current, AccessToken = "t", ExpiryTime = DateTime.UtcNow.AddHours(1) });
        // The seller's published listing for DSM-S16301: cost £10 ex VAT, delivery £4.80.
        ctx.Listings.Add(new Listing { Id = 50, OwnerUserId = 1, Sku = "DSM-S16301", Title = "Candle", Price = 14.99m, SourcePrice = 10m, DeliveryCost = 4.80m, Status = "Published", SourceUrl = "https://www.dropshipmall.co.uk/x" });
        ctx.SaveChanges();

        var ebay = new Mock<IEbayService>();
        ebay.Setup(e => e.GetOrdersJsonAsync(5, It.IsAny<string>(), 0, It.IsAny<int>())).ReturnsAsync(OrdersJson);
        ebay.Setup(e => e.GetShippingFulfillmentsJsonAsync(5, "12-34567-89012")).ReturnsAsync(TrackingJson);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["EbayOrders:HistoryDays"] = "120" }).Build();
        return (ctx, new EbayOrderSyncService(ctx, ebay.Object, config, new BackgroundUserContext()), ebay);
    }

    [Fact]
    public async Task Sync_ImportsOrders_MatchesCost_AndFetchesTracking()
    {
        var (ctx, sync, ebay) = SyncSetup();
        using var _ = ctx;

        await sync.SyncAccountAsync(5);

        var matched = ctx.EbayOrders.Single(o => o.OrderId == "12-34567-89012");
        Assert.Equal(16.80m, matched.SupplierPrice);         // £10 + VAT = £12, + £4.80 delivery
        Assert.Equal("DropShipMall", matched.Supplier);
        Assert.Equal(2.30m, matched.Fees);                   // eBay's figure already includes VAT
        Assert.Equal(12.69m, SalesReport.Earning(matched));  // = eBay's totalDueSeller
        Assert.True(matched.FeesAreActual);
        Assert.Equal("JD000123456GB", matched.TrackingNumber);
        Assert.Equal("Royal Mail", matched.DeliveryCompany);
        Assert.Equal(50, ctx.EbayOrderLines.Single(l => l.EbayOrderId == matched.Id).ListingId);

        var unmatched = ctx.EbayOrders.Single(o => o.OrderId == "98-76543-21098");
        Assert.Null(unmatched.SupplierPrice);                // MAN-L77 isn't a supplier product
        Assert.False(unmatched.FeesAreActual);
        Assert.True(unmatched.Fees > 0);                     // estimated

        // First sync imports by creation date over the history window; the next one only fetches changes.
        ebay.Verify(e => e.GetOrdersJsonAsync(5, It.Is<string>(f => f.StartsWith("creationdate:[")), 0, It.IsAny<int>()), Times.Once);
        await sync.SyncAccountAsync(5);
        ebay.Verify(e => e.GetOrdersJsonAsync(5, It.Is<string>(f => f.StartsWith("lastmodifieddate:[")), 0, It.IsAny<int>()), Times.Once);
        Assert.Equal(2, ctx.EbayOrders.Count());             // no duplicates
    }

    [Fact]
    public async Task Sync_KeepsTypedInValues()
    {
        var (ctx, sync, _) = SyncSetup();
        using var __ = ctx;
        await sync.SyncAccountAsync(5);

        var order = ctx.EbayOrders.Single(o => o.OrderId == "98-76543-21098");
        order.SupplierPrice = 9m;
        order.SupplierPriceIsManual = true;
        order.SourceOrderId = "DSM-ORDER-1";
        order.SourceEmail = "orders@example.com";
        ctx.SaveChanges();

        await sync.SyncAccountAsync(5);

        Assert.Equal(9m, order.SupplierPrice);
        Assert.Equal("DSM-ORDER-1", order.SourceOrderId);
        Assert.Equal("orders@example.com", order.SourceEmail);
    }

    [Fact]
    public async Task Sync_RefusesAccountsWithoutOrderAccess()
    {
        var (ctx, sync, _) = SyncSetup();
        using var __ = ctx;
        ctx.EbayTokens.Single().Scopes = null; // connected before the sales report
        ctx.SaveChanges();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sync.SyncAccountAsync(5));
        Assert.Contains("Reconnect", ex.Message);
    }

    [Fact]
    public async Task Matcher_UsesTheSupplierProductWhenThereIsNoListing()
    {
        using var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx);
        ctx.SupplierDeliveryOptions.Add(new SupplierDeliveryOption { SupplierId = ctx.Suppliers.Single().Id, Name = "Standard", Cost = 4m, IsDefault = true, IsActive = true });
        ctx.SupplierProducts.Add(new SupplierProduct { Supplier = "DropShipMall", SupplierSku = "S99", Title = "Vase", SupplierPrice = 5m });
        ctx.SaveChanges();
        var order = new EbayOrder { OrderId = "x" };
        var lines = new List<EbayOrderLine> { new() { Sku = "DSM-S99", Quantity = 2, Title = "Vase" } };

        await (await OrderCostMatcher.CreateAsync(ctx, ownerUserId: 1)).ApplyAsync(order, lines);

        Assert.Equal(6m, lines[0].UnitCost);       // £5 + VAT
        Assert.Equal(16.80m, order.SupplierPrice); // 2 × £6 + £4.80 delivery
    }

    [Fact]
    public async Task SourceLinks_ComeFromTheCatalogue_AndOnlyForOurItems()
    {
        using var ctx = TestHelpers.NewContext();
        TestHelpers.AddSupplier(ctx); // prefix DSM
        ctx.SupplierProducts.Add(new SupplierProduct { Id = 7, Supplier = "DropShipMall", SupplierSku = "S99", Title = "Vase", ProductUrl = "https://www.dropshipmall.co.uk/vase" });
        ctx.SupplierProducts.Add(new SupplierProduct { Id = 8, Supplier = "DropShipMall", SupplierSku = "S1", Title = "Lamp", ProductUrl = "https://www.dropshipmall.co.uk/lamp" });
        ctx.Listings.Add(new Listing { Id = 50, OwnerUserId = 1, Sku = "MY-CANDLE", Title = "Candle", SourceUrl = "https://www.dropshipmall.co.uk/candle", Status = "Published" });
        ctx.Listings.Add(new Listing { Id = 51, OwnerUserId = 2, Sku = "OTHER", Title = "Not yours", SourceUrl = "https://example.com/not-yours", Status = "Published" });
        ctx.Listings.Add(new Listing { Id = 52, OwnerUserId = 1, Sku = "MULTI", Title = "Lamps", Status = "Published" });
        ctx.ListingVariants.Add(new ListingVariant { ListingId = 52, Sku = "MULTI-RED", OptionsJson = "{}", SupplierProductId = 8 });
        ctx.Listings.Add(new Listing { Id = 53, OwnerUserId = 1, Sku = "BAD", Title = "Bad link", SourceUrl = "javascript:alert(1)", Status = "Published" });
        var orders = new List<EbayOrder>
        {
            new() { Id = 1, OrderId = "a", OwnerUserId = 1 }, // matched supplier product
            new() { Id = 2, OrderId = "b", OwnerUserId = 1 }, // listing by SKU
            new() { Id = 3, OrderId = "c", OwnerUserId = 1 }, // SKU names a supplier product
            new() { Id = 4, OrderId = "d", OwnerUserId = 1 }, // someone else's SKU, unknown SKU, non-web link: nothing
            new() { Id = 5, OrderId = "e", OwnerUserId = 1 }  // variation SKU
        };
        ctx.SaveChanges();
        var lines = new List<EbayOrderLine>
        {
            new() { EbayOrderId = 1, Title = "Vase", Sku = "whatever", SupplierProductId = 7 },
            new() { EbayOrderId = 2, Title = "Candle", Sku = "MY-CANDLE" },
            new() { EbayOrderId = 3, Title = "Vase again", Sku = "DSM-S99" },
            new() { EbayOrderId = 4, Title = "x", Sku = "OTHER" },
            new() { EbayOrderId = 4, Title = "y", Sku = "MAN-L77" },
            new() { EbayOrderId = 4, Title = "z", Sku = "BAD" },
            new() { EbayOrderId = 5, Title = "Red lamp", Sku = "MULTI-RED" }
        }.ToLookup(l => l.EbayOrderId);

        var links = await OrderSourceLinks.FindAsync(ctx, orders, lines);

        Assert.Equal("https://www.dropshipmall.co.uk/vase", links[1].Single().Url);
        Assert.Equal("https://www.dropshipmall.co.uk/candle", links[2].Single().Url);
        Assert.Equal("https://www.dropshipmall.co.uk/vase", links[3].Single().Url);
        Assert.False(links.ContainsKey(4));
        Assert.Equal("https://www.dropshipmall.co.uk/lamp", links[5].Single().Url);
        Assert.Equal("Red lamp", links[5].Single().Title);
    }

    [Fact]
    public async Task Update_SavesTypedInSourceLink_AndRefusesNonWebLinks()
    {
        using var ctx = TestHelpers.NewContext();
        ctx.EbayOrders.Add(new EbayOrder { Id = 1, OrderId = "a", OwnerUserId = 1 });
        ctx.SaveChanges();
        var controller = new SalesController(ctx, TestHelpers.User(1)).WithHttpContext(1);

        await controller.Update(1, new UpdateSalesOrderRequest { SourceUrl = "  https://www.amazon.co.uk/dp/B000  " });
        Assert.Equal("https://www.amazon.co.uk/dp/B000", ctx.EbayOrders.Single().SourceUrl);

        var bad = await controller.Update(1, new UpdateSalesOrderRequest { SourceUrl = "javascript:alert(1)" });
        Assert.IsType<BadRequestObjectResult>(bad.Result);
        Assert.Equal("https://www.amazon.co.uk/dp/B000", ctx.EbayOrders.Single().SourceUrl);

        await controller.Update(1, new UpdateSalesOrderRequest { SourceUrl = "" });
        Assert.Null(ctx.EbayOrders.Single().SourceUrl);
    }
}
