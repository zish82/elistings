using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hangfire;
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>A supplier whose catalogue the app can import itself (registered per supplier in Program.cs).</summary>
public interface ISupplierCatalogImporter
{
    /// <summary>Matches <see cref="SupplierProduct.Supplier"/> / SupplierFeed.Supplier.</summary>
    string SupplierCode { get; }
    Task<FeedSyncResult> ImportAsync(string triggeredBy, IProgress<string>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// Eden Horticulture (Shopify). Eden has no export, so the catalogue is assembled from three sources:
///  1. Public  /products.json            – titles, descriptions, brands, types, images, variant SKUs, RRP (≈3 requests)
///  2. Trade   /apps/trade/prices        – trade price per variant, 100 variants per request (≈17 requests, logged in)
///  3. Trade   /collections/all?page=N   – exact stock per variant from the trade card data (≈12 requests, logged in)
/// Requests are spaced out so the whole sync looks like a person browsing a few dozen pages.
/// Each variant (size) becomes its own SupplierProduct; variants of one product share GroupKey.
/// </summary>
public class EdenCatalogImportService : ISupplierCatalogImporter
{
    public const string Code = "EdenHorticulture";
    private const string BaseUrl = "https://edenhorticulture.co.uk";
    private const int PublicPageSize = 250;
    private const int MaxPublicPages = 20;
    private const int TradePriceBatchSize = 100;
    private const int MaxCollectionPages = 60;
    private static readonly TimeSpan PublicDelay = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TradeDelay = TimeSpan.FromSeconds(4);

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEdenSupplierSyncService _eden;
    private readonly SupplierCatalogService _catalog;
    private readonly ProductTagService _tags;
    private readonly CatalogCategoryService _categories;

    public EdenCatalogImportService(AppDbContext context, IHttpClientFactory httpClientFactory, IEdenSupplierSyncService eden,
        SupplierCatalogService catalog, ProductTagService tags, CatalogCategoryService categories)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _eden = eden;
        _catalog = catalog;
        _tags = tags;
        _categories = categories;
    }

    public string SupplierCode => Code;

    /// <summary>Daily schedule (Hangfire): Eden queue (one Eden job at a time), one retry an hour later.</summary>
    [Queue("eden"), AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 3600 })]
    public async Task RunScheduledAsync()
    {
        var result = await ImportAsync("scheduler");
        Console.WriteLine(result.IsSuccess
            ? $"[Eden] Scheduled import complete. processed={result.ProcessedRows}, inserted={result.Inserted}, updated={result.Updated}"
            : $"[Eden] Scheduled import failed: {result.ErrorMessage}");
    }

    // One import at a time (manual + scheduled could otherwise overlap and insert duplicate rows).
    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    public async Task<FeedSyncResult> ImportAsync(string triggeredBy, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!await ImportLock.WaitAsync(0, ct))
        {
            return new FeedSyncResult { Supplier = Code, FeedType = "Shopify Catalogue", ErrorMessage = "An Eden import is already running. Try again when it finishes." };
        }

        try
        {
            return await ImportInternalAsync(triggeredBy, progress, ct);
        }
        finally
        {
            ImportLock.Release();
        }
    }

    private async Task<FeedSyncResult> ImportInternalAsync(string triggeredBy, IProgress<string>? progress, CancellationToken ct)
    {
        var result = new FeedSyncResult { Supplier = Code, FeedType = "Shopify Catalogue" };
        var notes = new List<string>();
        if (await _catalog.IsDisabledAsync(Code))
        {
            result.ErrorMessage = "Eden Horticulture is disabled on Supplier Feeds, so it was not imported.";
            return result;
        }

        // ---- 1. Public catalogue
        progress?.Report("Downloading Eden catalogue...");
        List<ShopifyProduct> catalogue;
        try
        {
            catalogue = await DownloadPublicCatalogueAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            result.ErrorMessage = $"Could not download the Eden catalogue: {ex.Message}";
            return result;
        }

        if (catalogue.Count == 0)
        {
            result.ErrorMessage = "The Eden catalogue returned no products.";
            return result;
        }

        var supplier = await _catalog.FindAsync(Code, null);
        var existing = await _context.SupplierProducts.Where(p => p.Supplier == Code).ToListAsync(ct);
        var byVariantId = existing.Where(p => p.ExternalProductId != null)
            .GroupBy(p => p.ExternalProductId!).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).First());
        var bySku = existing.Where(p => p.SupplierSku != null)
            .GroupBy(p => p.SupplierSku!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).First(), StringComparer.OrdinalIgnoreCase);

        var byVariant = new Dictionary<long, SupplierProduct>();
        var tagsByVariant = new Dictionary<long, List<string>>();
        foreach (var product in catalogue)
        {
            foreach (var variant in product.Variants)
            {
                result.ProcessedRows++;
                var variantKey = variant.Id.ToString(CultureInfo.InvariantCulture);

                if (!byVariantId.TryGetValue(variantKey, out var entity)
                    && !(variant.Sku != null && bySku.TryGetValue(variant.Sku, out entity)))
                {
                    entity = new SupplierProduct
                    {
                        Supplier = Code,
                        ExternalProductId = variantKey,
                        CreatedAtUtc = DateTime.UtcNow,
                        IsActive = true,
                        PriceIsEstimate = true // until the trade price is fetched below
                    };
                    _context.SupplierProducts.Add(entity);
                    result.Inserted++;
                }
                else
                {
                    result.Updated++;
                }

                entity.ExternalProductId = variantKey; // keep the variant id current (rows matched by SKU)
                ApplyCatalogue(entity, product, variant);
                // RRP is only a placeholder for products that have never had a trade price.
                if ((entity.SupplierPrice == null || entity.PriceIsEstimate) && entity.RrpPrice.HasValue)
                {
                    entity.SupplierPrice = entity.RrpPrice;
                    entity.PriceIsEstimate = true;
                }
                entity.SuggestedPrice = Suggest(entity.SupplierPrice, supplier);
                byVariant[variant.Id] = entity;
                tagsByVariant[variant.Id] = product.Tags;
            }
        }
        MarkRemovedProducts(existing, byVariant, notes);
        await _context.SaveChangesAsync(ct);

        // Eden's tags (its website sections, e.g. "Carbon Filters") – for filtering and my categories.
        progress?.Report("Saving tags...");
        await _tags.SyncAsync(Code,
            byVariant.GroupBy(kv => kv.Value.Id) // two variants can share one row (matched by SKU)
                .ToDictionary(g => g.Key, g => g.SelectMany(kv => tagsByVariant[kv.Key]).Distinct(StringComparer.OrdinalIgnoreCase).ToList()),
            catalogue.Select(p => p.Vendor), ct);

        // ---- 2 + 3. Logged-in trade prices and stock
        IEdenTradeSession? session = null;
        try
        {
            progress?.Report("Logging in to the Eden trade portal...");
            session = await _eden.OpenTradeSessionAsync();
        }
        catch (Exception ex)
        {
            notes.Add($"Trade prices and stock were not updated (Eden login failed: {ex.Message}). Check EdenSupplier settings.");
        }

        if (session != null)
        {
            var priced = await UpdateTradePricesAsync(session, byVariant, supplier, progress, notes, ct);
            var stocked = await UpdateStockAsync(session, byVariant, progress, notes, ct);
            notes.Insert(0, $"Trade prices for {priced}/{byVariant.Count} variants, stock for {stocked}.");
        }

        await UpdateLinkedListingsAsync(byVariant.Values.Concat(existing).Distinct(), ct);
        await _context.SaveChangesAsync(ct);
        await _categories.RecomputeAsync(Code, ct); // my categories, from the category/tag mappings

        result.ErrorMessage = null;
        var summary = $"{catalogue.Count} products / {byVariant.Count} variants: {result.Inserted} new, {result.Updated} updated. " + string.Join(" ", notes);
        progress?.Report(summary);
        Console.WriteLine($"[Eden] {triggeredBy}: {summary}");
        return result;
    }

    // ------------------------------------------------------------------ public catalogue

    private async Task<List<ShopifyProduct>> DownloadPublicCatalogueAsync(CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(60);
        var products = new List<ShopifyProduct>();

        for (var page = 1; page <= MaxPublicPages; page++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/products.json?limit={PublicPageSize}&page={page}");
            request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            using var response = await client.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            var pageProducts = ShopifyProduct.ParseList(json);
            products.AddRange(pageProducts);
            if (pageProducts.Count < PublicPageSize) break;
            await Task.Delay(PublicDelay, ct);
        }

        return products;
    }

    private static void ApplyCatalogue(SupplierProduct entity, ShopifyProduct product, ShopifyVariant variant)
    {
        var hasVariants = product.Variants.Count > 1 || !variant.IsDefaultTitle;
        var title = CleanText(product.Title);

        entity.GroupKey = product.Id.ToString(CultureInfo.InvariantCulture);
        entity.Handle = product.Handle;
        entity.SupplierSku = string.IsNullOrWhiteSpace(variant.Sku) ? entity.SupplierSku : variant.Sku.Trim();
        entity.VariantName = hasVariants ? CleanText(variant.Title) : null;
        entity.VariantOptionsJson = hasVariants && variant.Options.Count > 0 ? JsonSerializer.Serialize(variant.Options) : null;
        entity.Title = hasVariants ? $"{title} - {CleanText(variant.Title)}" : title;
        entity.Description = HtmlSanitizer.Clean(product.BodyHtml) ?? entity.Description;
        entity.Brand = string.IsNullOrWhiteSpace(product.Vendor) ? entity.Brand : CleanText(product.Vendor);
        entity.CategoryName = string.IsNullOrWhiteSpace(product.ProductType) ? entity.CategoryName : CleanText(product.ProductType);
        entity.ProductUrl = $"{BaseUrl}/products/{product.Handle}" + (hasVariants ? $"?variant={variant.Id}" : string.Empty);

        // Variant image first, then the rest of the product's images.
        var images = new List<string>();
        if (!string.IsNullOrWhiteSpace(variant.ImageUrl)) images.Add(variant.ImageUrl);
        images.AddRange(product.ImageUrls.Where(u => !images.Contains(u, StringComparer.OrdinalIgnoreCase)));
        if (images.Count > 0)
        {
            entity.ImageUrl = images[0];
            entity.ImageUrlsJson = JsonSerializer.Serialize(images.Take(12).ToList());
        }

        entity.RrpPrice = variant.Price;
        // Public data only says available yes/no; the exact level comes from the trade pages.
        if (!variant.Available) entity.StockLevel = 0;
        entity.Currency = "GBP";
        entity.UpdatedAtUtc = DateTime.UtcNow;
        // IsActive is not changed: products deleted on /products stay deleted.
    }

    /// <summary>
    /// The public catalogue lists every product Eden sells, so a variant we imported before that is missing
    /// now has been removed from their site: hide it on /products and treat it as out of stock.
    /// Guard: if the download looks incomplete (under half the previous size), nothing is marked.
    /// </summary>
    private static void MarkRemovedProducts(List<SupplierProduct> existing, Dictionary<long, SupplierProduct> seen, List<string> notes)
    {
        var seenIds = seen.Keys.Select(id => id.ToString(CultureInfo.InvariantCulture)).ToHashSet();
        var previouslyListed = existing.Count(p => p.RemovedFromSupplierAtUtc == null);
        if (previouslyListed > 0 && seenIds.Count < previouslyListed / 2)
        {
            notes.Add($"Catalogue looked incomplete ({seenIds.Count} variants vs {previouslyListed} before); removed-product check skipped.");
            return;
        }

        var removed = 0;
        var restored = 0;
        foreach (var product in existing)
        {
            var inCatalogue = product.ExternalProductId != null && seenIds.Contains(product.ExternalProductId);
            if (!inCatalogue && product.RemovedFromSupplierAtUtc == null)
            {
                product.RemovedFromSupplierAtUtc = DateTime.UtcNow;
                product.StockLevel = 0;
                removed++;
            }
            else if (inCatalogue && product.RemovedFromSupplierAtUtc != null)
            {
                product.RemovedFromSupplierAtUtc = null;
                restored++;
            }
        }

        if (removed > 0) notes.Add($"{removed} variant(s) no longer on Eden's site were hidden.");
        if (restored > 0) notes.Add($"{restored} variant(s) are back on Eden's site.");
    }

    // ------------------------------------------------------------------ trade prices

    private async Task<int> UpdateTradePricesAsync(IEdenTradeSession session, Dictionary<long, SupplierProduct> byVariant,
        SupplierDto? supplier, IProgress<string>? progress, List<string> notes, CancellationToken ct)
    {
        var ids = byVariant.Keys.ToList();
        var priced = 0;

        for (var i = 0; i < ids.Count; i += TradePriceBatchSize)
        {
            var batch = ids.Skip(i).Take(TradePriceBatchSize).ToList();
            progress?.Report($"Trade prices {Math.Min(i + batch.Count, ids.Count)}/{ids.Count}...");
            try
            {
                var json = await session.GetStringAsync(new Uri($"{BaseUrl}/apps/trade/prices?variants={string.Join(",", batch)}"), ct);
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("prices", out var prices) || prices.ValueKind != JsonValueKind.Array) continue;

                foreach (var p in prices.EnumerateArray())
                {
                    if (!TryGetLong(p, "variant_id", out var variantId)) continue;
                    if (!byVariant.TryGetValue(variantId, out var entity)) continue;
                    // The trade endpoint returns whole pence (e.g. 2750 = £27.50).
                    if (!TryGetLong(p, "price", out var pence) || pence <= 0) continue;

                    entity.SupplierPrice = pence / 100m;
                    entity.SuggestedPrice = Suggest(entity.SupplierPrice, supplier);
                    entity.PriceIsEstimate = false;
                    entity.TradePriceFetchedAtUtc = DateTime.UtcNow;
                    priced++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or TaskCanceledException)
            {
                notes.Add($"Trade price batch {i / TradePriceBatchSize + 1} failed: {ex.Message}.");
            }

            await _context.SaveChangesAsync(ct);
            await Task.Delay(TradeDelay, ct);
        }

        return priced;
    }

    // ------------------------------------------------------------------ stock

    private async Task<int> UpdateStockAsync(IEdenTradeSession session, Dictionary<long, SupplierProduct> byVariant,
        IProgress<string>? progress, List<string> notes, CancellationToken ct)
    {
        var stocked = 0;
        for (var page = 1; page <= MaxCollectionPages; page++)
        {
            progress?.Report($"Stock levels, page {page}...");
            string html;
            try
            {
                html = await session.GetStringAsync(new Uri($"{BaseUrl}/collections/all?page={page}"), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
            {
                notes.Add($"Stock page {page} failed: {ex.Message}.");
                break;
            }

            var cards = ParseTradeCards(html);
            if (cards.Count == 0) break; // past the last page (or not logged in)

            foreach (var card in cards)
            {
                if (!byVariant.TryGetValue(card.VariantId, out var entity)) continue;
                // Untracked items have no meaningful quantity: keep "available" as unknown level, unavailable as 0.
                entity.StockLevel = card.Tracked ? Math.Max(card.Qty, 0) : (card.Available ? null : 0);
                entity.UpdatedAtUtc = DateTime.UtcNow;
                stocked++;
            }

            await _context.SaveChangesAsync(ct);
            await Task.Delay(TradeDelay, ct);
        }

        return stocked;
    }

    internal sealed record TradeCardVariant(long VariantId, bool Available, int Qty, bool Tracked);

    /// <summary>Reads the trade app's per-card JSON: &lt;script type="application/json" data-eden-trade-product-data&gt;.</summary>
    internal static List<TradeCardVariant> ParseTradeCards(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var scripts = doc.DocumentNode.SelectNodes("//script[@data-eden-trade-product-data]");
        var result = new List<TradeCardVariant>();
        if (scripts == null) return result;

        foreach (var script in scripts)
        {
            try
            {
                using var json = JsonDocument.Parse(WebUtility.HtmlDecode(script.InnerText));
                if (!json.RootElement.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array) continue;
                foreach (var v in variants.EnumerateArray())
                {
                    if (!TryGetLong(v, "id", out var id)) continue;
                    var available = v.TryGetProperty("avail", out var a) && a.ValueKind == JsonValueKind.True;
                    var qty = TryGetLong(v, "qty", out var qv) ? (int)Math.Clamp(qv, 0, int.MaxValue) : 0;
                    var tracked = v.TryGetProperty("tracked", out var t) && t.ValueKind == JsonValueKind.True;
                    result.Add(new TradeCardVariant(id, available, qty, tracked));
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // skip malformed card
            }
        }
        return result;
    }

    // ------------------------------------------------------------------ helpers

    private async Task UpdateLinkedListingsAsync(IEnumerable<SupplierProduct> products, CancellationToken ct)
    {
        var map = products.Where(p => p.Id > 0).ToDictionary(p => p.Id);
        if (map.Count == 0) return;
        var listings = await _context.Listings.Where(l => l.SupplierProductId != null).ToListAsync(ct);
        foreach (var listing in listings)
        {
            if (!map.TryGetValue(listing.SupplierProductId!.Value, out var product)) continue;
            if (!product.PriceIsEstimate) listing.SourcePrice = product.SupplierPrice;
            if (product.StockLevel.HasValue) listing.SourceStockLevel = product.StockLevel;
            listing.SupplierCategory ??= product.CategoryName;
        }
    }

    /// <summary>Reads a numeric property without throwing when it's missing, null or a string.</summary>
    internal static bool TryGetLong(JsonElement e, string name, out long value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var p)) return false;
        if (p.ValueKind == JsonValueKind.Number) return p.TryGetInt64(out value);
        return p.ValueKind == JsonValueKind.String && long.TryParse(p.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static decimal? Suggest(decimal? supplierPrice, SupplierDto? supplier)
        => PricingMath.SuggestItemPrice(PricingMath.CostIncVat(supplierPrice, supplier), supplier?.DefaultMarginPercent ?? 20m);

    private static string CleanText(string? value)
        => Regex.Replace(WebUtility.HtmlDecode(WebUtility.HtmlDecode(value ?? string.Empty)), @"\s+", " ").Trim();

    // ------------------------------------------------------------------ Shopify JSON model

    internal sealed class ShopifyVariant
    {
        public long Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string? Sku { get; init; }
        public decimal? Price { get; init; }
        public bool Available { get; init; }
        public string? ImageUrl { get; init; }
        public Dictionary<string, string> Options { get; init; } = new();
        public bool IsDefaultTitle => Title.Equals("Default Title", StringComparison.OrdinalIgnoreCase);
    }

    internal sealed class ShopifyProduct
    {
        public long Id { get; init; }
        public string Title { get; init; } = string.Empty;
        public string Handle { get; init; } = string.Empty;
        public string? BodyHtml { get; init; }
        public string? Vendor { get; init; }
        public string? ProductType { get; init; }
        public List<string> ImageUrls { get; init; } = new();
        public List<ShopifyVariant> Variants { get; init; } = new();
        /// <summary>Shopify tags (an array in products.json; older stores send one comma-separated string).</summary>
        public List<string> Tags { get; init; } = new();

        public static List<ShopifyProduct> ParseList(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var list = new List<ShopifyProduct>();
            if (!doc.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array) return list;

            foreach (var p in products.EnumerateArray())
            {
                var optionNames = p.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array
                    ? opts.EnumerateArray().Select(o => Str(o, "name") ?? "Option").ToList()
                    : new List<string>();

                var imagesById = new Dictionary<long, string>();
                var imageUrls = new List<string>();
                if (p.TryGetProperty("images", out var imgs) && imgs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var img in imgs.EnumerateArray())
                    {
                        var src = NormaliseUrl(Str(img, "src"));
                        if (src == null) continue;
                        imageUrls.Add(src);
                        if (TryGetLong(img, "id", out var iid)) imagesById[iid] = src;
                    }
                }

                var variants = new List<ShopifyVariant>();
                if (p.TryGetProperty("variants", out var vars) && vars.ValueKind == JsonValueKind.Array)
                {
                    foreach (var v in vars.EnumerateArray())
                    {
                        if (!TryGetLong(v, "id", out var variantId)) continue;

                        var options = new Dictionary<string, string>();
                        for (var i = 0; i < optionNames.Count && i < 3; i++)
                        {
                            var value = Str(v, $"option{i + 1}");
                            if (!string.IsNullOrWhiteSpace(value) && !value.Equals("Default Title", StringComparison.OrdinalIgnoreCase))
                                options[optionNames[i]] = value;
                        }

                        string? image = null;
                        if (v.TryGetProperty("featured_image", out var fi) && fi.ValueKind == JsonValueKind.Object) image = NormaliseUrl(Str(fi, "src"));

                        variants.Add(new ShopifyVariant
                        {
                            Id = variantId,
                            Title = Str(v, "title") ?? string.Empty,
                            Sku = Str(v, "sku"),
                            Price = decimal.TryParse(Str(v, "price"), NumberStyles.Any, CultureInfo.InvariantCulture, out var price) ? price : null,
                            Available = v.TryGetProperty("available", out var av) && av.ValueKind == JsonValueKind.True,
                            ImageUrl = image,
                            Options = options
                        });
                    }
                }

                list.Add(new ShopifyProduct
                {
                    Id = TryGetLong(p, "id", out var id) ? id : 0,
                    Title = Str(p, "title") ?? string.Empty,
                    Handle = Str(p, "handle") ?? string.Empty,
                    BodyHtml = Str(p, "body_html"),
                    Vendor = Str(p, "vendor"),
                    ProductType = Str(p, "product_type"),
                    Tags = ReadTags(p),
                    ImageUrls = imageUrls,
                    Variants = variants
                });
            }
            return list;
        }

        private static List<string> ReadTags(JsonElement p)
        {
            if (!p.TryGetProperty("tags", out var tags)) return new List<string>();
            IEnumerable<string?> values = tags.ValueKind switch
            {
                JsonValueKind.Array => tags.EnumerateArray().Where(t => t.ValueKind == JsonValueKind.String).Select(t => t.GetString()),
                JsonValueKind.String => (tags.GetString() ?? string.Empty).Split(','),
                _ => Array.Empty<string?>()
            };
            return values.Select(t => t?.Trim()).Where(t => !string.IsNullOrEmpty(t)).Select(t => t!)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string? Str(JsonElement e, string name)
            => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static string? NormaliseUrl(string? url)
            => string.IsNullOrWhiteSpace(url) ? null : (url.StartsWith("//") ? "https:" + url : url);
    }
}
