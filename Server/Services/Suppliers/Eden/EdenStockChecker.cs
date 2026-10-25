using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Server.Services;

/// <summary>
/// Eden Horticulture products imported by <see cref="EdenCatalogImportService"/> (we know their Shopify variant id).
/// Live trade price from the logged-in /apps/trade/prices lookup; exact stock from the trade data on the
/// logged-in product page. Listings scraped via bulk extract (no linked product) still use the legacy checker.
/// </summary>
public class EdenStockChecker : ISupplierStockChecker
{
    private const string BaseUrl = "https://edenhorticulture.co.uk";
    private const int LowStockThreshold = 5;
    private static readonly Regex HtmlComments = new("<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TradeScript = new(@"<script[^>]*>(?<json>(?:(?!</script>).)*?eden-trade-variant-json(?:(?!</script>).)*)</script>", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly IEdenSupplierSyncService _eden;

    public EdenStockChecker(IEdenSupplierSyncService eden)
    {
        _eden = eden;
    }

    public bool CanCheck(SupplierStockQuery query)
        => string.Equals(query.Product?.Supplier, EdenCatalogImportService.Code, StringComparison.OrdinalIgnoreCase)
           && long.TryParse(query.Product?.ExternalProductId, out _);

    public async Task<SupplierStockResult> CheckAsync(SupplierStockQuery query, CancellationToken ct = default)
    {
        try
        {
            return await CheckInternalAsync(query, ct);
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or HttpRequestException or TaskCanceledException)
        {
            // e.g. Eden login not configured/failed, or the price lookup returned a login page instead of JSON.
            return new SupplierStockResult(EdenCatalogImportService.Code, false, ErrorMessage: $"Eden check failed: {ex.Message}");
        }
    }

    private async Task<SupplierStockResult> CheckInternalAsync(SupplierStockQuery query, CancellationToken ct)
    {
        var product = query.Product!;
        var variantId = long.Parse(product.ExternalProductId!, CultureInfo.InvariantCulture);
        var session = await _eden.OpenTradeSessionAsync();

        // 1. Trade price (same lookup the Eden site itself uses).
        decimal? tradePrice = null;
        var pricesJson = await session.GetStringAsync(new Uri($"{BaseUrl}/apps/trade/prices?variants={variantId}"), ct);
        using (var doc = JsonDocument.Parse(pricesJson))
        {
            if (doc.RootElement.TryGetProperty("prices", out var prices) && prices.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in prices.EnumerateArray())
                {
                    if (EdenCatalogImportService.TryGetLong(p, "variant_id", out var id) && id == variantId
                        && EdenCatalogImportService.TryGetLong(p, "price", out var pence) && pence > 0)
                    {
                        tradePrice = pence / 100m;
                    }
                }
            }
        }

        // 2. Exact stock from the logged-in product page.
        int? qty = null;
        bool? available = null;
        if (!string.IsNullOrWhiteSpace(product.ProductUrl))
        {
            var html = await session.GetStringAsync(new Uri(product.ProductUrl), ct);
            (qty, available) = ReadVariantStock(html, variantId);
        }

        var status = available == false || qty == 0 ? "OutOfStock"
                   : qty is > 0 and <= LowStockThreshold ? "Low"
                   : qty > 0 || available == true ? "InStock"
                   : "Unknown";
        var text = qty.HasValue ? $"{qty} in stock" : available == true ? "In stock" : available == false ? "Out of stock" : null;

        if (tradePrice == null && status == "Unknown")
        {
            return new SupplierStockResult(EdenCatalogImportService.Code, false,
                ErrorMessage: "Eden returned no trade data for this product (logged out, or the product was removed).");
        }

        return new SupplierStockResult(EdenCatalogImportService.Code, true, status, text, qty, tradePrice, IsTradePrice: tradePrice.HasValue);
    }

    /// <summary>Reads qty/avail for one variant from the page's eden-trade JSON (which contains HTML comments).</summary>
    internal static (int? Qty, bool? Available) ReadVariantStock(string html, long variantId)
    {
        foreach (Match m in TradeScript.Matches(html))
        {
            try
            {
                using var doc = JsonDocument.Parse(HtmlComments.Replace(m.Groups["json"].Value, string.Empty));
                if (!doc.RootElement.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array) continue;
                foreach (var v in variants.EnumerateArray())
                {
                    if (!EdenCatalogImportService.TryGetLong(v, "id", out var id) || id != variantId) continue;
                    int? qty = EdenCatalogImportService.TryGetLong(v, "qty", out var q) ? (int)Math.Clamp(q, 0, int.MaxValue) : null;
                    bool? avail = v.TryGetProperty("avail", out var a) && a.ValueKind is JsonValueKind.True or JsonValueKind.False
                        ? a.ValueKind == JsonValueKind.True
                        : null;
                    var tracked = v.TryGetProperty("tracked", out var t) && t.ValueKind == JsonValueKind.True;
                    return (tracked ? qty : null, avail);
                }
            }
            catch (JsonException)
            {
                // not the script we want
            }
        }
        return (null, null);
    }
}
