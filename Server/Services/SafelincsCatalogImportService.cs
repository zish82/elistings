using System.Text.Json;
using System.Text.RegularExpressions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>
/// Safelincs has no product feed, so the catalogue is built from their public website:
///  1. /sitemap.xml lists every product page (~1,600).
///  2. Each product page gives title, description, images, brand, specifications, category (breadcrumb),
///     retail price and stock ("5 In Stock"). Pages with a variant dropdown are fetched once per variant (?option=id).
///  3. Trade prices (ex VAT) and per-product delivery come from the price list uploaded on /supplier-feeds,
///     matched by Product Code. Without one, the website's ex-VAT retail price is used and marked "est.".
/// Every request goes through the per-host throttle (RequestThrottle:safelincs.co.uk), so a full run takes a few hours.
/// Each variant becomes its own SupplierProduct; variants of one page share GroupKey ("safelincs:{page slug}").
/// </summary>
public class SafelincsCatalogImportService : ISupplierCatalogImporter
{
    public const string Code = "Safelincs";
    public const string FeedType = "Website Catalogue";
    private const string BaseUrl = "https://www.safelincs.co.uk";
    private const string GroupPrefix = "safelincs:";
    /// <summary>Safety cap: a page with more variants than this only imports the first ones.</summary>
    private const int MaxVariantsPerPage = 40;
    /// <summary>Stop when this many pages in a row fail – Safelincs is probably blocking us, and carrying on won't help.</summary>
    private const int MaxConsecutiveFailures = 15;
    private const int SaveEvery = 10; // also how often it checks whether it's been switched off

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SupplierCatalogService _catalog;
    private readonly SupplierPriceListService _priceList;
    private readonly ProductTagService _tags;

    public SafelincsCatalogImportService(AppDbContext context, IHttpClientFactory httpClientFactory,
        SupplierCatalogService catalog, SupplierPriceListService priceList, ProductTagService tags)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _catalog = catalog;
        _priceList = priceList;
        _tags = tags;
    }

    public string SupplierCode => Code;

    /// <summary>Nightly schedule (Hangfire): own single-worker queue, one retry an hour later.</summary>
    [Queue(Jobs.BackgroundJobs.SafelincsQueue), AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 3600 })]
    public async Task RunScheduledAsync()
    {
        if (!await _catalog.IsBackgroundSyncAllowedAsync(Code)) { Console.WriteLine("[Safelincs] Background sync is off; nightly import skipped."); return; }
        var result = await ImportAsync("scheduler");
        Console.WriteLine(result.IsSuccess
            ? $"[Safelincs] Scheduled import complete. processed={result.ProcessedRows}, inserted={result.Inserted}, updated={result.Updated}"
            : $"[Safelincs] Scheduled import: {result.ErrorMessage}");
    }

    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    public async Task<FeedSyncResult> ImportAsync(string triggeredBy, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!await ImportLock.WaitAsync(0, ct))
        {
            return new FeedSyncResult { Supplier = Code, FeedType = FeedType, ErrorMessage = "A Safelincs import is already running. Try again when it finishes." };
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
        var result = new FeedSyncResult { Supplier = Code, FeedType = FeedType };
        var notes = new List<string>();

        var supplierRow = await _context.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Code == Code, ct);
        if (supplierRow is { IsActive: false })
        {
            // Scheduled runs simply skip; a manual start is refused by the controller before getting here.
            result.ErrorMessage = "Safelincs is disabled on /supplier-feeds, so it was not imported.";
            return result;
        }
        if (await _priceList.EnsurePricesExVatAsync(Code, ct))
        {
            notes.Add("Safelincs was set to 'prices exclude VAT' on /suppliers (imported prices are ex VAT).");
        }
        var supplier = await _catalog.FindAsync(Code, null);
        var prices = await _priceList.LoadAsync(Code, ct);
        if (prices.Count == 0) notes.Add("No trade price list uploaded yet: website prices (ex VAT) are shown as estimates.");

        // ---- 1. Sitemap
        progress?.Report("Downloading the Safelincs sitemap...");
        var client = CreateClient();
        List<string> pageUrls;
        try
        {
            pageUrls = ParseSitemapProductUrls(await GetStringAsync(client, $"{BaseUrl}/sitemap.xml", ct));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            result.ErrorMessage = $"Could not download the Safelincs sitemap: {ex.Message}";
            return result;
        }
        if (pageUrls.Count == 0)
        {
            result.ErrorMessage = "The Safelincs sitemap listed no product pages (the site may be blocking this server).";
            return result;
        }

        var existing = await _context.SupplierProducts.Where(p => p.Supplier == Code).ToListAsync(ct);
        var byVariantId = existing.Where(p => p.ExternalProductId != null)
            .GroupBy(p => p.ExternalProductId!).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).First());
        var bySku = existing.Where(p => p.SupplierSku != null)
            .GroupBy(p => p.SupplierSku!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).First(), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<SupplierProduct>();
        var tagsByEntity = new Dictionary<SupplierProduct, List<string>>();
        var pagesOk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // slugs fetched successfully
        var consecutiveFailures = 0;
        var failedPages = 0;

        // ---- 2. Product pages
        for (var i = 0; i < pageUrls.Count; i++)
        {
            var url = pageUrls[i];
            var slug = Slug(url);
            progress?.Report($"Product pages {i + 1}/{pageUrls.Count}: {result.Inserted} new, {result.Updated} updated...");

            List<(SafelincsPage Page, string Url)> variants;
            bool complete;
            try
            {
                (variants, complete) = await FetchVariantsAsync(client, url, ct);
                consecutiveFailures = 0;
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException) && !ct.IsCancellationRequested)
            {
                failedPages++;
                if (++consecutiveFailures >= MaxConsecutiveFailures)
                {
                    notes.Add($"Stopped after {MaxConsecutiveFailures} failed pages in a row ({ex.Message}). Safelincs may be blocking this server.");
                    break;
                }
                continue;
            }

            // A page that loaded but has no product on it at all (e.g. a challenge/blocked page served as 200)
            // counts as a failure, so it can never make existing products look removed.
            if (variants.All(v => string.IsNullOrWhiteSpace(v.Page.Title)))
            {
                failedPages++;
                if (++consecutiveFailures >= MaxConsecutiveFailures)
                {
                    notes.Add($"Stopped after {MaxConsecutiveFailures} pages in a row without product details. Safelincs may be blocking this server.");
                    break;
                }
                continue;
            }
            // Only a fully loaded page with real product data can prove a variant has gone (see MarkRemovedProducts).
            if (complete && variants.Any(v => v.Page.HasBuyBox)) pagesOk.Add(slug);
            foreach (var (page, variantUrl) in variants)
            {
                // Services/courses have no buy box; discontinued items aren't imported (existing ones go out of stock below).
                if (!page.HasBuyBox || string.IsNullOrWhiteSpace(page.Title) || string.IsNullOrWhiteSpace(page.ProductCode) && page.VariantId == null)
                {
                    result.Skipped++;
                    continue;
                }

                var variantKey = page.VariantId?.ToString() ?? $"{slug}:{page.ProductCode}";
                // SKU fallback (rows imported before variant ids were known) – but never a product already matched
                // this run, otherwise variants sharing one product code would overwrite each other.
                var found = byVariantId.TryGetValue(variantKey, out var entity)
                            || (page.ProductCode != null && bySku.TryGetValue(page.ProductCode, out entity) && !seen.Contains(entity));
                if (!found)
                {
                    if (page.IsDiscontinued) { result.Skipped++; continue; }
                    entity = new SupplierProduct { Supplier = Code, CreatedAtUtc = DateTime.UtcNow, IsActive = true };
                    _context.SupplierProducts.Add(entity);
                    byVariantId[variantKey] = entity;
                    if (page.ProductCode != null) bySku[page.ProductCode] = entity;
                    result.Inserted++;
                }
                else
                {
                    result.Updated++;
                }

                result.ProcessedRows++;
                entity!.ExternalProductId = variantKey;
                ApplyPage(entity, page, slug, variantUrl, variants.Count > 1);
                ApplyPrice(entity, page, prices, supplier);
                entity.RemovedFromSupplierAtUtc = page.IsDiscontinued ? entity.RemovedFromSupplierAtUtc ?? DateTime.UtcNow : null;
                seen.Add(entity);
                tagsByEntity[entity] = page.Breadcrumbs; // every breadcrumb level is a tag, for filtering
            }

            if (i % SaveEvery == 0)
            {
                await _context.SaveChangesAsync(ct);
                // Stop early when switched off meanwhile: the supplier disabled, or (scheduled runs) background sync off.
                var current = await _context.Suppliers.AsNoTracking().Where(s => s.Code == Code).Select(s => new { s.IsActive, s.BackgroundSyncEnabled }).FirstOrDefaultAsync(ct);
                if (current != null && (!current.IsActive || (triggeredBy == "scheduler" && !current.BackgroundSyncEnabled)))
                {
                    notes.Add($"Stopped after {i + 1} of {pageUrls.Count} pages: {(current.IsActive ? "background sync was switched off" : "Safelincs was disabled")}.");
                    break;
                }
            }
        }
        await _context.SaveChangesAsync(ct);

        MarkRemovedProducts(existing, seen, pageUrls.Select(Slug).ToHashSet(StringComparer.OrdinalIgnoreCase), pagesOk, notes);
        await UpdateLinkedListingsAsync(seen.Concat(existing).Distinct().ToList(), ct);
        await _context.SaveChangesAsync(ct);

        progress?.Report("Saving tags...");
        await _tags.SyncAsync(Code, tagsByEntity.ToDictionary(kv => kv.Key.Id, kv => kv.Value),
            seen.Select(p => p.Brand), ct);

        if (failedPages > 0) notes.Add($"{failedPages} page(s) could not be loaded; they'll be retried on the next import.");
        if (result.Skipped > 0) notes.Add($"{result.Skipped} item(s) skipped (services, discontinued, or no product code).");
        if (result.ProcessedRows == 0)
        {
            result.ErrorMessage = "No Safelincs product pages could be loaded. " + string.Join(" ", notes);
            return result;
        }

        var priced = seen.Count(p => !p.PriceIsEstimate);
        var summary = $"{pagesOk.Count}/{pageUrls.Count} pages, {seen.Count} products: {result.Inserted} new, {result.Updated} updated, "
                      + $"{priced} with trade price. " + string.Join(" ", notes);
        progress?.Report(summary);
        Console.WriteLine($"[Safelincs] {triggeredBy}: {summary}");
        return result;
    }

    // ------------------------------------------------------------------ fetching

    private HttpClient CreateClient()
    {
        var client = _httpClientFactory.CreateClient("safelincs");
        client.Timeout = TimeSpan.FromSeconds(60);
        return client;
    }

    private static async Task<string> GetStringAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        // A blocked request comes back as 200 with an empty/near-empty body.
        if (body.Length < 500) throw new InvalidOperationException($"Safelincs returned an empty page for {url}.");
        return body;
    }

    /// <summary>
    /// The page itself, plus one request per other variant in its dropdown.
    /// Complete = every variant loaded (none failed or were cut off by <see cref="MaxVariantsPerPage"/>).
    /// </summary>
    private static async Task<(List<(SafelincsPage Page, string Url)> Variants, bool Complete)> FetchVariantsAsync(HttpClient client, string url, CancellationToken ct)
    {
        var first = SafelincsPageParser.Parse(await GetStringAsync(client, url, ct));
        var result = new List<(SafelincsPage, string)>();
        if (first.Options.Count <= 1)
        {
            result.Add((first, url));
            return (result, true);
        }

        var complete = first.Options.Count <= MaxVariantsPerPage;
        foreach (var option in first.Options.Take(MaxVariantsPerPage))
        {
            var variantUrl = $"{url}?option={option.Id}";
            SafelincsPage page;
            if (first.VariantId == option.Id) page = first;
            else
            {
                try { page = SafelincsPageParser.Parse(await GetStringAsync(client, variantUrl, ct)); }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException) && !ct.IsCancellationRequested)
                {
                    complete = false;
                    continue;
                }
            }
            page.VariantId = option.Id; // the id we asked for (the page's own snapshot may name the default variant)
            page.VariantLabel = option.Label;
            page.Title = $"{page.Title} - {option.Label}";
            result.Add((page, variantUrl));
        }
        return (result, complete);
    }

    internal static List<string> ParseSitemapProductUrls(string xml)
        => Regex.Matches(xml, @"<loc>\s*([^<\s]+/product/[^<\s]+)\s*</loc>", RegexOptions.IgnoreCase)
            .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim())
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Host.EndsWith("safelincs.co.uk", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static string Slug(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return path.TrimEnd('/').Split('/').Last();
    }

    // ------------------------------------------------------------------ mapping

    internal static void ApplyPage(SupplierProduct entity, SafelincsPage page, string slug, string url, bool hasVariants)
    {
        entity.GroupKey = GroupPrefix + slug;
        entity.Handle = slug;
        entity.SupplierSku = string.IsNullOrWhiteSpace(page.ProductCode) ? entity.SupplierSku : page.ProductCode!.Trim();
        entity.Title = page.Title;
        entity.VariantName = hasVariants ? page.VariantLabel : null;
        entity.VariantOptionsJson = hasVariants && entity.VariantName != null
            ? JsonSerializer.Serialize(new Dictionary<string, string> { ["Option"] = entity.VariantName })
            : null;
        entity.Description = page.DescriptionHtml ?? entity.Description;
        entity.Brand = page.Brand ?? entity.Brand;
        entity.CategoryName = page.Category ?? entity.CategoryName;
        if (page.Breadcrumbs.Count > 0) entity.CategoryPath = string.Join(" › ", page.Breadcrumbs);
        entity.Barcode = page.Barcode ?? entity.Barcode;
        entity.ProductUrl = url;
        if (page.ImageUrls.Count > 0)
        {
            entity.ImageUrl = page.ImageUrls[0];
            entity.ImageUrlsJson = JsonSerializer.Serialize(page.ImageUrls.Take(12).ToList());
        }
        if (page.Specifics.Count > 0)
        {
            entity.SpecificsJson = ListingMapping.WriteSpecifics(page.Specifics);
            entity.DetailsFetchedAtUtc = DateTime.UtcNow;
        }
        entity.RrpPrice = page.PriceIncVat;
        // "Available to Order" has no stock figure (null = unknown); out of stock / discontinued = 0.
        entity.StockLevel = page.InStock ? (page.AvailableToOrder ? null : page.StockLevel) : 0;
        entity.Currency = "GBP";
        entity.UpdatedAtUtc = DateTime.UtcNow;
        // IsActive is not changed: products deleted on /products stay deleted.
    }

    private static void ApplyPrice(SupplierProduct entity, SafelincsPage page, Dictionary<string, SupplierPriceListItem> prices, SupplierDto? supplier)
    {
        if (entity.SupplierSku != null && prices.TryGetValue(entity.SupplierSku, out var item))
        {
            SupplierPriceListService.ApplyPrice(entity, item, supplier);
        }
        else
        {
            // No trade price: the retail price ex VAT is the best estimate of cost (trade is usually lower).
            entity.SupplierPrice = page.PriceExVat ?? (page.PriceIncVat.HasValue ? Math.Round(page.PriceIncVat.Value / 1.2m, 2) : entity.SupplierPrice);
            entity.PriceIsEstimate = true;
            entity.SuggestedPrice = PricingMath.SuggestItemPrice(PricingMath.CostIncVat(entity.SupplierPrice, supplier), supplier?.DefaultMarginPercent ?? 20m);
        }

        // Delivery: the price list's per-product charge wins; otherwise the website's standard delivery (ex VAT).
        if (!(entity.SupplierSku != null && prices.TryGetValue(entity.SupplierSku, out var listed) && listed.DeliveryCost.HasValue)
            && page.StandardDeliveryIncVat.HasValue)
        {
            entity.DeliveryCost = Math.Round(page.StandardDeliveryIncVat.Value / (1 + (supplier?.VatRatePercent ?? 20m) / 100m), 2);
        }
    }

    /// <summary>
    /// A product whose page is no longer in the sitemap, or whose page loaded but no longer offers that variant,
    /// has been removed by Safelincs: hide it and treat it as out of stock. Pages that failed to load are left alone.
    /// </summary>
    private static void MarkRemovedProducts(List<SupplierProduct> existing, HashSet<SupplierProduct> seen,
        HashSet<string> sitemapSlugs, HashSet<string> pagesOk, List<string> notes)
    {
        if (sitemapSlugs.Count < existing.Select(p => p.Handle).Distinct().Count() / 2)
        {
            notes.Add("Sitemap looked incomplete; removed-product check skipped.");
            return;
        }

        var removed = 0;
        foreach (var product in existing)
        {
            if (seen.Contains(product) || product.RemovedFromSupplierAtUtc != null || product.Handle == null) continue;
            var gone = !sitemapSlugs.Contains(product.Handle) || pagesOk.Contains(product.Handle);
            if (!gone) continue;
            product.RemovedFromSupplierAtUtc = DateTime.UtcNow;
            product.StockLevel = 0;
            removed++;
        }
        if (removed > 0) notes.Add($"{removed} product(s) no longer on Safelincs' site were hidden.");
    }

    private async Task UpdateLinkedListingsAsync(List<SupplierProduct> products, CancellationToken ct)
    {
        var map = products.Where(p => p.Id > 0).GroupBy(p => p.Id).ToDictionary(g => g.Key, g => g.First());
        if (map.Count == 0) return;
        var ids = map.Keys.ToList();
        var listings = await _context.Listings.Where(l => l.SupplierProductId != null && ids.Contains(l.SupplierProductId.Value)).ToListAsync(ct);
        foreach (var listing in listings)
        {
            var product = map[listing.SupplierProductId!.Value];
            if (!product.PriceIsEstimate) listing.SourcePrice = product.SupplierPrice;
            listing.SourceStockLevel = product.StockLevel ?? listing.SourceStockLevel;
            listing.SupplierCategory ??= product.CategoryName;
        }
    }
}
