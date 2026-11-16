using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>
/// Godropship has no product feed (only a manual "download queue" of 30 items per zip), so the catalogue is built
/// from their public website:
///  1. /sitemapproducts.xml lists every product page (~2,400).
///  2. Each product page gives item code, title, description HTML, pictures, breadcrumb category, price and UK stock.
/// Prices include UK delivery (free UK mainland delivery), so products get DeliveryCost = 0. Each colour/size is its own
/// page with its own item code, so there's no variant handling. At one page every few seconds a full run takes ~2 hours.
/// </summary>
public class GodropshipCatalogImportService : ISupplierCatalogImporter
{
    public const string Code = GodropshipWebsiteClient.SupplierCode;
    public const string FeedType = "Website Catalogue";
    /// <summary>Stop when this many pages in a row fail – Godropship is probably blocking us.</summary>
    private const int MaxConsecutiveFailures = 15;
    private const int SaveEvery = 10; // also how often it checks whether it's been switched off

    private readonly AppDbContext _context;
    private readonly GodropshipWebsiteClient _website;
    private readonly ProductTagService _tags;

    public GodropshipCatalogImportService(AppDbContext context, GodropshipWebsiteClient website, ProductTagService tags)
    {
        _context = context;
        _website = website;
        _tags = tags;
    }

    public string SupplierCode => Code;

    /// <summary>Nightly schedule (Hangfire): skipped unless background sync is switched on for Godropship.</summary>
    [Queue(Jobs.BackgroundJobs.GodropshipQueue), AutomaticRetry(Attempts = 1, DelaysInSeconds = new[] { 3600 })]
    public async Task RunScheduledAsync()
    {
        if (!await new SupplierCatalogService(_context).IsBackgroundSyncAllowedAsync(Code))
        {
            Console.WriteLine("[Godropship] Background sync is off; nightly import skipped.");
            return;
        }
        var result = await ImportAsync("scheduler");
        Console.WriteLine(result.IsSuccess
            ? $"[Godropship] Scheduled import complete. processed={result.ProcessedRows}, inserted={result.Inserted}, updated={result.Updated}"
            : $"[Godropship] Scheduled import: {result.ErrorMessage}");
    }

    private static readonly SemaphoreSlim ImportLock = new(1, 1);

    public async Task<FeedSyncResult> ImportAsync(string triggeredBy, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (!await ImportLock.WaitAsync(0, ct))
        {
            return new FeedSyncResult { Supplier = Code, FeedType = FeedType, ErrorMessage = "A Godropship import is already running. Try again when it finishes." };
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
            result.ErrorMessage = "Godropship is disabled on /supplier-feeds, so it was not imported.";
            return result;
        }

        // ---- 1. Sitemap
        progress?.Report("Downloading the Godropship product sitemap...");
        List<string> pageUrls;
        try
        {
            pageUrls = await _website.GetProductUrlsAsync(ct);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException) && !ct.IsCancellationRequested)
        {
            result.ErrorMessage = $"Could not download the Godropship sitemap: {ex.Message}";
            return result;
        }
        if (pageUrls.Count == 0)
        {
            result.ErrorMessage = "The Godropship sitemap listed no product pages (the site may be blocking this server).";
            return result;
        }

        var existing = await _context.SupplierProducts.Where(p => p.Supplier == Code).ToListAsync(ct);
        var byCode = existing.Where(p => p.ExternalProductId != null)
            .GroupBy(p => p.ExternalProductId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(p => p.Id).First(), StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<SupplierProduct>();
        var tagsByEntity = new Dictionary<SupplierProduct, List<string>>();
        var consecutiveFailures = 0;
        var failedPages = 0;

        // ---- 2. Product pages
        for (var i = 0; i < pageUrls.Count; i++)
        {
            var url = pageUrls[i];
            progress?.Report($"Product pages {i + 1}/{pageUrls.Count}: {result.Inserted} new, {result.Updated} updated...");

            GodropshipPage page;
            try
            {
                page = await _website.GetProductAsync(url, ct);
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or InvalidOperationException) && !ct.IsCancellationRequested)
            {
                failedPages++;
                if (++consecutiveFailures >= MaxConsecutiveFailures)
                {
                    notes.Add($"Stopped after {MaxConsecutiveFailures} failed pages in a row ({ex.Message}). Godropship may be blocking this server.");
                    break;
                }
                continue;
            }

            // A page that loaded without a product on it (unknown URLs return 200) counts as a failure,
            // so it can never make an existing product look removed.
            if (!page.IsProduct)
            {
                failedPages++;
                if (++consecutiveFailures >= MaxConsecutiveFailures)
                {
                    notes.Add($"Stopped after {MaxConsecutiveFailures} pages in a row without product details. Godropship may be blocking this server.");
                    break;
                }
                continue;
            }
            consecutiveFailures = 0;

            if (!byCode.TryGetValue(page.ItemCode!, out var entity))
            {
                entity = new SupplierProduct { Supplier = Code, CreatedAtUtc = DateTime.UtcNow, IsActive = true };
                _context.SupplierProducts.Add(entity);
                byCode[page.ItemCode!] = entity;
                result.Inserted++;
            }
            else if (!seen.Contains(entity))
            {
                result.Updated++;
            }

            result.ProcessedRows++;
            ApplyPage(entity, page, url);
            seen.Add(entity);
            tagsByEntity[entity] = page.Breadcrumbs; // every breadcrumb level is a tag, for filtering

            if (i % SaveEvery == 0)
            {
                await _context.SaveChangesAsync(ct);
                // Stop early when switched off meanwhile: the supplier disabled, or (scheduled runs) background sync off.
                var current = await _context.Suppliers.AsNoTracking().Where(s => s.Code == Code)
                    .Select(s => new { s.IsActive, s.BackgroundSyncEnabled }).FirstOrDefaultAsync(ct);
                if (current != null && (!current.IsActive || (triggeredBy == "scheduler" && !current.BackgroundSyncEnabled)))
                {
                    notes.Add($"Stopped after {i + 1} of {pageUrls.Count} pages: {(current.IsActive ? "background sync was switched off" : "Godropship was disabled")}.");
                    break;
                }
            }
        }
        await _context.SaveChangesAsync(ct);

        MarkRemovedProducts(existing, seen, pageUrls.Select(GodropshipPageParser.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase), notes);
        await UpdateLinkedListingsAsync(seen.Concat(existing).Distinct().ToList(), ct);
        await _context.SaveChangesAsync(ct);

        progress?.Report("Saving tags...");
        await _tags.SyncAsync(Code, tagsByEntity.ToDictionary(kv => kv.Key.Id, kv => kv.Value), seen.Select(p => p.Brand), ct);

        if (failedPages > 0) notes.Add($"{failedPages} page(s) could not be loaded; they'll be retried on the next import.");
        if (result.ProcessedRows == 0)
        {
            result.ErrorMessage = "No Godropship product pages could be loaded. " + string.Join(" ", notes);
            return result;
        }

        var summary = $"{result.ProcessedRows}/{pageUrls.Count} pages: {result.Inserted} new, {result.Updated} updated. " + string.Join(" ", notes);
        progress?.Report(summary);
        Console.WriteLine($"[Godropship] {triggeredBy}: {summary}");
        return result;
    }

    // ------------------------------------------------------------------ mapping

    internal static void ApplyPage(SupplierProduct entity, GodropshipPage page, string url)
    {
        var slug = GodropshipPageParser.Slug(url);
        entity.ExternalProductId = page.ItemCode;
        entity.SupplierSku = page.ItemCode;
        entity.Handle = slug;
        entity.GroupKey = null;
        entity.Title = page.Title;
        entity.Description = page.DescriptionHtml ?? entity.Description;
        entity.CategoryName = page.Category ?? entity.CategoryName;
        if (page.Breadcrumbs.Count > 0) entity.CategoryPath = string.Join(" › ", page.Breadcrumbs);
        entity.ProductUrl = url;
        if (page.ImageUrls.Count > 0)
        {
            entity.ImageUrl = page.ImageUrls[0];
            entity.ImageUrlsJson = JsonSerializer.Serialize(page.ImageUrls.Take(12).ToList());
        }
        if (page.WeightGrams is > 0)
        {
            entity.SpecificsJson = ListingMapping.WriteSpecifics(new Dictionary<string, string>
            {
                ["Item Weight"] = $"{page.WeightGrams.Value:0.##} g"
            });
            entity.DetailsFetchedAtUtc = DateTime.UtcNow;
        }
        // Godropship's own price is what we pay (UK delivery included; VAT basis set on /suppliers).
        if (page.Price.HasValue)
        {
            entity.SupplierPrice = page.Price;
            entity.PriceIsEstimate = false;
            entity.TradePriceFetchedAtUtc = DateTime.UtcNow;
        }
        entity.DeliveryCost = 0m;
        entity.StockLevel = page.StockLevel ?? (page.InStock ? null : 0);
        entity.Currency = "GBP";
        entity.RemovedFromSupplierAtUtc = null;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        // IsActive is not changed: products deleted on /products stay deleted.
    }

    /// <summary>
    /// A product whose page is no longer in the sitemap has been removed by Godropship: hide it and treat it as out of stock.
    /// Pages that failed to load are left alone.
    /// </summary>
    internal static void MarkRemovedProducts(List<SupplierProduct> existing, HashSet<SupplierProduct> seen,
        HashSet<string> sitemapSlugs, List<string> notes)
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
            if (sitemapSlugs.Contains(product.Handle)) continue;
            product.RemovedFromSupplierAtUtc = DateTime.UtcNow;
            product.StockLevel = 0;
            removed++;
        }
        if (removed > 0) notes.Add($"{removed} product(s) no longer on Godropship's site were hidden.");
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
