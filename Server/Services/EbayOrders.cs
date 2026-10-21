using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

public sealed record ParsedOrderLine(string LineItemId, string? Sku, string? LegacyItemId, string Title, int Quantity, decimal LineTotal);

public sealed record ParsedOrder(
    string OrderId, DateTime CreatedAtUtc, DateTime? LastModifiedAtUtc, string? Buyer,
    decimal SaleTotal, decimal ItemsTotal, decimal Postage, decimal Refunded, string Currency,
    string? FulfillmentStatus, string? PaymentStatus, string? CancelState,
    decimal? MarketplaceFee, bool HasFulfillments, IReadOnlyList<ParsedOrderLine> Lines);

public sealed record ParsedTracking(string? TrackingNumber, string? Carrier, DateTime? ShippedAtUtc);

/// <summary>Reads eBay Fulfillment API JSON (getOrders, getShippingFulfillments).</summary>
public static class EbayOrderParser
{
    public static (List<ParsedOrder> Orders, int Total) ParseOrders(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var total = root.TryGetProperty("total", out var t) && t.TryGetInt32(out var n) ? n : 0;
        var orders = new List<ParsedOrder>();
        if (!root.TryGetProperty("orders", out var list) || list.ValueKind != JsonValueKind.Array) return (orders, total);

        foreach (var o in list.EnumerateArray())
        {
            var orderId = Str(o, "orderId");
            if (string.IsNullOrWhiteSpace(orderId)) continue;

            var pricing = o.TryGetProperty("pricingSummary", out var ps) ? ps : default;
            var payment = o.TryGetProperty("paymentSummary", out var pay) ? pay : default;
            var itemsTotal = Amount(pricing, "priceSubtotal") ?? 0m;
            var postage = Amount(pricing, "deliveryCost") ?? 0m;
            // What you're paid: totalDueSeller excludes tax eBay collects and remits itself; fall back to the order total.
            var saleTotal = Amount(payment, "totalDueSeller") ?? Amount(pricing, "total") ?? itemsTotal + postage;

            decimal refunded = 0m;
            if (payment.ValueKind == JsonValueKind.Object && payment.TryGetProperty("refunds", out var refunds) && refunds.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in refunds.EnumerateArray())
                {
                    if (string.Equals(Str(r, "refundStatus"), "FAILED", StringComparison.OrdinalIgnoreCase)) continue;
                    refunded += Amount(r, "amount") ?? 0m;
                }
            }

            var lines = new List<ParsedOrderLine>();
            if (o.TryGetProperty("lineItems", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var li in items.EnumerateArray())
                {
                    lines.Add(new ParsedOrderLine(
                        Str(li, "lineItemId") ?? Guid.NewGuid().ToString("N"),
                        Str(li, "sku"),
                        Str(li, "legacyItemId"),
                        Str(li, "title") ?? string.Empty,
                        li.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qty) ? qty : 1,
                        Amount(li, "lineItemCost") ?? Amount(li, "total") ?? 0m));
                }
            }

            orders.Add(new ParsedOrder(
                orderId!,
                Date(Str(o, "creationDate")) ?? DateTime.UtcNow,
                Date(Str(o, "lastModifiedDate")),
                o.TryGetProperty("buyer", out var buyer) ? Str(buyer, "username") : null,
                saleTotal, itemsTotal, postage, refunded,
                (pricing.ValueKind == JsonValueKind.Object && pricing.TryGetProperty("total", out var tot) ? Str(tot, "currency") : null) ?? "GBP",
                Str(o, "orderFulfillmentStatus"),
                Str(o, "orderPaymentStatus"),
                o.TryGetProperty("cancelStatus", out var cancel) ? Str(cancel, "cancelState") : null,
                Amount(o, "totalMarketplaceFee"),
                o.TryGetProperty("fulfillmentHrefs", out var hrefs) && hrefs.ValueKind == JsonValueKind.Array && hrefs.GetArrayLength() > 0,
                lines));
        }
        return (orders, total);
    }

    /// <summary>The first fulfillment that has a tracking number.</summary>
    public static ParsedTracking? ParseTracking(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("fulfillments", out var list) || list.ValueKind != JsonValueKind.Array) return null;
        foreach (var f in list.EnumerateArray())
        {
            var number = Str(f, "shipmentTrackingNumber");
            if (string.IsNullOrWhiteSpace(number)) continue;
            return new ParsedTracking(number, Str(f, "shippingCarrierCode"), Date(Str(f, "shippedDate")));
        }
        return null;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>eBay amounts are {"value": "12.34", "currency": "GBP"}.</summary>
    private static decimal? Amount(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Object) return null;
        if (!a.TryGetProperty("value", out var v)) return null;
        var text = v.ValueKind == JsonValueKind.String ? v.GetString() : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : null;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static DateTime? Date(string? iso)
        => DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) ? d : null;
}

/// <summary>
/// Imports eBay orders into the sales report: order details from eBay, cost of sales from the matched listing or
/// supplier product (by SKU), fees (eBay's when the order reports them, else estimated), tracking.
/// </summary>
public class EbayOrderSyncService
{
    public const int DefaultHistoryDays = 120;
    private const int PageSize = 200;
    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    private readonly AppDbContext _context;
    private readonly IEbayService _ebay;
    private readonly IConfiguration _configuration;
    private readonly BackgroundUserContext _userContext;

    public EbayOrderSyncService(AppDbContext context, IEbayService ebay, IConfiguration configuration, BackgroundUserContext userContext)
    {
        _context = context;
        _ebay = ebay;
        _configuration = configuration;
        _userContext = userContext;
    }

    /// <summary>How far back to import (EbayOrders:HistoryDays; 120 ≈ 4 months for now, up to 730 = eBay's 2-year limit).</summary>
    public int HistoryDays => Math.Clamp(_configuration.GetValue<int?>("EbayOrders:HistoryDays") ?? DefaultHistoryDays, 1, 730);

    /// <summary>Every 2 hours (Hangfire): all accounts that allow reading orders, each as its owner.</summary>
    [AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 900 })]
    public async Task RunScheduledAsync()
    {
        var accounts = await _context.EbayTokens.AsNoTracking().ToListAsync();
        foreach (var account in accounts.Where(CanReadOrders))
        {
            try
            {
                if (!await ActAsOwnerAsync(account.UserId)) continue;
                var summary = await SyncAccountAsync(account.Id);
                Console.WriteLine($"[Sales] {account.Name}: {summary}");
            }
            catch (Exception ex)
            {
                _context.ChangeTracker.Clear(); // don't let a half-saved account break the next one
                Console.WriteLine($"[Sales] Sync failed for account {account.Id} ({account.Name}): {ex.Message}");
            }
        }
    }

    /// <summary>Accounts a user can sync: their own, or every account for an Admin.</summary>
    public async Task<List<EbayTokenInfo>> AccountsForAsync(ICurrentUserService user)
        => await _context.EbayTokens.AsNoTracking()
            .Where(t => user.CanManageUsers || t.UserId == user.UserId)
            .ToListAsync();

    /// <summary>Imports/updates one account's orders. Returns a short summary.</summary>
    public async Task<string> SyncAccountAsync(int accountId, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        await SyncLock.WaitAsync(ct);
        try
        {
            return await SyncAccountInternalAsync(accountId, progress, ct);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private async Task<string> SyncAccountInternalAsync(int accountId, IProgress<string>? progress, CancellationToken ct)
    {
        var account = await _context.EbayTokens.FirstAsync(t => t.Id == accountId, ct);
        if (!CanReadOrders(account))
        {
            throw new InvalidOperationException($"Reconnect the eBay account '{account.Name}' on eBay Accounts to allow the app to read its orders.");
        }

        var startedAt = DateTime.UtcNow;
        var wantFrom = startedAt.AddDays(-HistoryDays); // not .Date: 730 days must stay within eBay's 2-year limit
        // First run, or the history window was extended: import by creation date. Otherwise only what changed since last time.
        var fullImport = account.OrdersSyncedFromUtc == null || wantFrom < account.OrdersSyncedFromUtc.Value.AddDays(-1) || account.OrdersLastSyncedAtUtc == null;
        var since = fullImport ? wantFrom : account.OrdersLastSyncedAtUtc!.Value.AddDays(-1);
        var filter = $"{(fullImport ? "creationdate" : "lastmodifieddate")}:[{since.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}..]";

        var feeSettings = await new MarketplaceFeeService(_context).GetSettingsAsync(ct: ct);
        var matcher = await OrderCostMatcher.CreateAsync(_context, account.UserId, ct);

        int added = 0, updated = 0, offset = 0, total;
        do
        {
            progress?.Report($"{account.Name}: reading orders {offset + 1}–{offset + PageSize}...");
            var (orders, pageTotal) = EbayOrderParser.ParseOrders(await _ebay.GetOrdersJsonAsync(account.Id, filter, offset, PageSize));
            total = pageTotal;

            var ids = orders.Select(o => o.OrderId).ToList();
            var existing = await _context.EbayOrders.Where(o => ids.Contains(o.OrderId)).ToDictionaryAsync(o => o.OrderId, ct);
            var existingIds = existing.Values.Select(o => o.Id).ToList();
            var existingLines = (await _context.EbayOrderLines.Where(l => existingIds.Contains(l.EbayOrderId)).ToListAsync(ct)).ToLookup(l => l.EbayOrderId);

            foreach (var parsed in orders)
            {
                if (!existing.TryGetValue(parsed.OrderId, out var order))
                {
                    order = new EbayOrder { OrderId = parsed.OrderId, EbayAccountId = account.Id, OwnerUserId = account.UserId, CreatedAtUtc = parsed.CreatedAtUtc };
                    _context.EbayOrders.Add(order);
                    await _context.SaveChangesAsync(ct); // gives the order its Id for its lines
                    added++;
                }
                else updated++;

                var lines = UpdateOrder(order, parsed, existingLines[order.Id].ToList());
                await matcher.ApplyAsync(order, lines, ct);
                await ApplyFeesAsync(order, parsed, lines, matcher, feeSettings, ct);
                order.SyncedAtUtc = DateTime.UtcNow;

                if (order.TrackingNumber == null && parsed.HasFulfillments)
                {
                    try
                    {
                        var tracking = EbayOrderParser.ParseTracking(await _ebay.GetShippingFulfillmentsJsonAsync(account.Id, parsed.OrderId));
                        if (tracking != null)
                        {
                            order.TrackingNumber = tracking.TrackingNumber;
                            order.DeliveryCompany = tracking.Carrier;
                            order.ShippedAtUtc = tracking.ShippedAtUtc;
                        }
                    }
                    catch (Exception ex) when (ex is EbayApiException or HttpRequestException or JsonException)
                    {
                        Console.WriteLine($"[Sales] No tracking for {parsed.OrderId}: {ex.Message}");
                    }
                }

            }
            await _context.SaveChangesAsync(ct);
            offset += PageSize;
        } while (offset < total);

        if (fullImport) account.OrdersSyncedFromUtc = wantFrom;
        account.OrdersLastSyncedAtUtc = startedAt;
        await _context.SaveChangesAsync(ct);
        return $"{added} new, {updated} updated order(s) since {since:d MMM yyyy}.";
    }

    /// <summary>Copies eBay's fields onto the order and returns its lines (kept by line id, so cost snapshots survive).</summary>
    private List<EbayOrderLine> UpdateOrder(EbayOrder order, ParsedOrder parsed, List<EbayOrderLine> current)
    {
        order.CreatedAtUtc = parsed.CreatedAtUtc;
        order.LastModifiedAtUtc = parsed.LastModifiedAtUtc;
        order.BuyerUsername = parsed.Buyer;
        order.SaleTotal = parsed.SaleTotal;
        order.ItemsTotal = parsed.ItemsTotal;
        order.PostageCharged = parsed.Postage;
        order.RefundedTotal = parsed.Refunded;
        order.Currency = parsed.Currency;
        order.FulfillmentStatus = parsed.FulfillmentStatus;
        order.PaymentStatus = parsed.PaymentStatus;
        order.CancelState = parsed.CancelState;

        var result = new List<EbayOrderLine>();
        foreach (var p in parsed.Lines)
        {
            var line = current.FirstOrDefault(l => l.LineItemId == p.LineItemId);
            if (line == null)
            {
                line = new EbayOrderLine { LineItemId = p.LineItemId, EbayOrderId = order.Id };
                _context.EbayOrderLines.Add(line);
            }
            line.Sku = p.Sku;
            line.LegacyItemId = p.LegacyItemId;
            line.Title = p.Title;
            line.Quantity = p.Quantity;
            line.LineTotal = p.LineTotal;
            result.Add(line);
        }
        _context.EbayOrderLines.RemoveRange(current.Where(l => result.All(r => r.Id != l.Id)));
        return result;
    }

    public static bool IsCancelled(EbayOrder order) => string.Equals(order.CancelState, "CANCELED", StringComparison.OrdinalIgnoreCase);

    /// <summary>Connected with the orders scope (or a manually pasted token, whose scopes are unknown).</summary>
    public static bool CanReadOrders(EbayTokenInfo account) => account.RefreshToken == "MANUAL" || EbayScopes.CanReadOrders(account.Scopes);

    /// <summary>eBay's fee for the order when it reports one (plus VAT when not VAT-registered), else our estimate.</summary>
    private static async Task ApplyFeesAsync(EbayOrder order, ParsedOrder parsed, List<EbayOrderLine> lines, OrderCostMatcher matcher,
        MarketplaceFeeSettingsDto settings, CancellationToken ct)
    {
        if (IsCancelled(order))
        {
            order.Fees = 0m; // eBay credits the fees of cancelled orders
            order.FeesAreActual = false;
            return;
        }
        if (parsed.MarketplaceFee.HasValue)
        {
            var fee = parsed.MarketplaceFee.Value;
            order.Fees = settings.VatRegistered ? fee : Math.Round(fee * (1 + settings.VatRatePercent / 100m), 2, MidpointRounding.AwayFromZero);
            order.FeesAreActual = true;
            return;
        }
        var (categoryId, supplier) = await matcher.FeeContextForAsync(lines, ct);
        var categoryChain = string.IsNullOrWhiteSpace(categoryId) ? null : new[] { categoryId };
        var estimate = FeeMath.EstimateSale(order.SaleTotal, null, false, null, categoryChain, supplier, settings);
        order.Fees = estimate.TotalFees;
        order.FeesAreActual = false;
    }

    /// <summary>Lets the eBay service act for the account's owner (tokens are per user) in background runs.</summary>
    private async Task<bool> ActAsOwnerAsync(int userId)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            _userContext.User = null;
            return false;
        }
        _userContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        }, "BackgroundJob"));
        return true;
    }
}

/// <summary>
/// Works out what the supplier charges for an order's items: via the seller's listing with the same SKU (its source
/// price + VAT and delivery), else via the supplier product named by the SKU ("DSM-S16301" → DropShipMall S16301).
/// Costs are a snapshot: lines already costed keep their cost.
/// </summary>
public class OrderCostMatcher
{
    private readonly AppDbContext _context;
    private readonly int _ownerUserId;
    private readonly List<SupplierDto> _suppliers;
    private readonly Dictionary<int, Listing> _listingCache = new();

    private OrderCostMatcher(AppDbContext context, int ownerUserId, List<SupplierDto> suppliers)
    {
        _context = context;
        _ownerUserId = ownerUserId;
        _suppliers = suppliers;
    }

    public static async Task<OrderCostMatcher> CreateAsync(AppDbContext context, int ownerUserId, CancellationToken ct = default)
        => new(context, ownerUserId, await new SupplierCatalogService(context).GetAllAsync());

    public async Task ApplyAsync(EbayOrder order, List<EbayOrderLine> lines, CancellationToken ct = default)
    {
        string? supplierName = null;
        foreach (var line in lines.Where(l => l.UnitCost == null && !string.IsNullOrWhiteSpace(l.Sku)))
        {
            var sku = line.Sku!.Trim();
            var listing = await _context.Listings.AsNoTracking()
                .Where(l => l.Sku == sku && l.OwnerUserId == _ownerUserId)
                .OrderByDescending(l => l.Status == "Published").ThenByDescending(l => l.Id)
                .FirstOrDefaultAsync(ct);

            SupplierProduct? product = null;
            if (listing?.SupplierProductId != null)
            {
                product = await _context.SupplierProducts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == listing.SupplierProductId, ct);
            }
            // No listing, or a listing without a cost: the SKU may still name a supplier product ("DSM-S16301").
            if (product == null && listing?.SourcePrice == null)
            {
                product = await FindProductBySkuAsync(sku, ct);
            }

            var supplier = SupplierFor(product?.Supplier, listing?.SourceUrl);
            var sourcePrice = listing?.SourcePrice ?? product?.SupplierPrice;
            if (!sourcePrice.HasValue) continue; // not ours / unknown cost

            line.ListingId = listing?.Id;
            line.SupplierProductId = product?.Id ?? listing?.SupplierProductId;
            line.UnitCost = PricingMath.CostIncVat(sourcePrice, supplier);
            line.DeliveryCost = listing?.DeliveryCost ?? (product == null ? null : MarketplaceFeeService.DefaultDeliveryIncVat(product, supplier)) ?? 0m;
            if (listing != null) _listingCache[listing.Id] = listing;
            supplierName ??= supplier?.Name;
        }

        if (order.SupplierPriceIsManual) return;
        if (EbayOrderSyncService.IsCancelled(order) || lines.Count == 0 || lines.Any(l => l.UnitCost == null))
        {
            order.SupplierPrice = EbayOrderSyncService.IsCancelled(order) ? 0m : null;
        }
        else
        {
            order.SupplierPrice = lines.Sum(l => l.UnitCost!.Value * l.Quantity + (l.DeliveryCost ?? 0m));
        }
        if (supplierName != null) order.Supplier ??= supplierName;
    }

    /// <summary>For the fee estimate: the first matched listing's eBay category and its supplier.</summary>
    public async Task<(string? CategoryId, SupplierDto? Supplier)> FeeContextForAsync(List<EbayOrderLine> lines, CancellationToken ct = default)
    {
        foreach (var line in lines.Where(l => l.ListingId.HasValue))
        {
            if (!_listingCache.TryGetValue(line.ListingId!.Value, out var listing))
            {
                listing = await _context.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == line.ListingId!.Value, ct);
                if (listing == null) continue;
                _listingCache[listing.Id] = listing;
            }
            string? code = null;
            if (listing.SupplierProductId.HasValue)
            {
                code = await _context.SupplierProducts.AsNoTracking().Where(p => p.Id == listing.SupplierProductId.Value).Select(p => p.Supplier).FirstOrDefaultAsync(ct);
            }
            return (listing.CategoryId, SupplierFor(code, listing.SourceUrl));
        }
        return (null, null);
    }

    private async Task<SupplierProduct?> FindProductBySkuAsync(string sku, CancellationToken ct)
    {
        var dash = sku.IndexOf('-');
        if (dash <= 0 || dash == sku.Length - 1) return null;
        var prefix = sku[..dash];
        var supplierSku = sku[(dash + 1)..];
        var supplier = _suppliers.FirstOrDefault(s => string.Equals(s.SkuPrefix, prefix, StringComparison.OrdinalIgnoreCase));
        if (supplier == null) return null;
        return await _context.SupplierProducts.AsNoTracking()
            .Where(p => p.Supplier == supplier.Code && p.SupplierSku == supplierSku)
            .OrderBy(p => p.Id)
            .FirstOrDefaultAsync(ct);
    }

    private SupplierDto? SupplierFor(string? code, string? sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(code))
        {
            var byCode = _suppliers.FirstOrDefault(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase));
            if (byCode != null) return byCode;
        }
        if (string.IsNullOrWhiteSpace(sourceUrl)) return null;
        return _suppliers.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.WebsiteHost) && sourceUrl.Contains(s.WebsiteHost!, StringComparison.OrdinalIgnoreCase));
    }
}
