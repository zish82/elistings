using System.Security.Claims;
using System.Text.Json;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Server.Data;

namespace Server.Services.Jobs;

/// <summary>
/// Page-triggered jobs, executed by Hangfire (see /hangfire). Each method:
///  - runs "as" the user who started it (eBay tokens, listing ownership),
///  - reports progress to its BulkJobs row via <see cref="JobProgress"/>,
///  - is not retried automatically (the user can simply start it again).
/// Jobs that call Eden Horticulture run on the single-worker "eden" queue, so only one runs at a time.
/// </summary>
public class BackgroundJobs
{
    public const string EdenQueue = "eden";
    /// <summary>Safelincs imports take hours (one page at a time), so they get their own single worker.</summary>
    public const string SafelincsQueue = "safelincs";
    public const string DefaultQueue = "default";

    private readonly IServiceProvider _services;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AppDbContext _context;
    private readonly BackgroundUserContext _userContext;

    public BackgroundJobs(IServiceProvider services, IServiceScopeFactory scopeFactory, AppDbContext context, BackgroundUserContext userContext)
    {
        _services = services;
        _scopeFactory = scopeFactory;
        _context = context;
        _userContext = userContext;
    }

    // ------------------------------------------------------------------ entry points (Hangfire)

    [Queue(DefaultQueue), AutomaticRetry(Attempts = 0)]
    public Task BulkExtractAsync(string jobId, int userId, string categoryUrl)
        => RunAsync(jobId, userId, progress => BulkExtractCoreAsync(progress, userId, categoryUrl));

    [Queue(EdenQueue), AutomaticRetry(Attempts = 0)]
    public Task BulkExtractEdenAsync(string jobId, int userId, string categoryUrl)
        => RunAsync(jobId, userId, progress => BulkExtractCoreAsync(progress, userId, categoryUrl));

    [Queue(DefaultQueue), AutomaticRetry(Attempts = 0)]
    public Task BulkPublishAsync(string jobId, int userId, List<int> listingIds)
        => RunAsync(jobId, userId, progress => BulkPublishCoreAsync(progress, userId, listingIds));

    [Queue(DefaultQueue), AutomaticRetry(Attempts = 0)]
    public Task EnrichListingsAsync(string jobId, int userId, List<int> listingIds)
        => RunAsync(jobId, userId, progress => EnrichCoreAsync(progress, listingIds));

    [Queue(EdenQueue), AutomaticRetry(Attempts = 0)]
    public Task ImportEdenCatalogueAsync(string jobId, int userId)
        => RunAsync(jobId, userId, progress => ImportCoreAsync(progress, EdenCatalogImportService.Code));

    [Queue(SafelincsQueue), AutomaticRetry(Attempts = 0)]
    public Task ImportSafelincsCatalogueAsync(string jobId, int userId)
        => RunAsync(jobId, userId, progress => ImportCoreAsync(progress, SafelincsCatalogImportService.Code));

    [Queue(DefaultQueue), AutomaticRetry(Attempts = 0)]
    public Task SetSupplierMarketplaceQuantityAsync(string jobId, int userId, string supplierCode, bool restore)
        => RunAsync(jobId, userId, progress => SetSupplierQuantityCoreAsync(progress, supplierCode, restore));

    [Queue(DefaultQueue), AutomaticRetry(Attempts = 0)]
    public Task ImportSupplierCatalogueAsync(string jobId, int userId, string supplierCode)
        => RunAsync(jobId, userId, progress => ImportCoreAsync(progress, supplierCode));

    // ------------------------------------------------------------------ plumbing

    private async Task RunAsync(string jobId, int userId, Func<JobProgress, Task<string?>> work)
    {
        await using var progress = await JobProgress.BeginAsync(_scopeFactory, jobId);
        try
        {
            await ImpersonateAsync(userId);
            var summary = await work(progress);
            await progress.CompleteAsync(summary);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Jobs] {jobId} failed: {ex}");
            await progress.FailAsync(ex.Message);
            throw; // also shown as failed on /hangfire (no automatic retry)
        }
    }

    /// <summary>Makes ICurrentUserService in this job's scope return the user who started the job.</summary>
    private async Task ImpersonateAsync(int userId)
    {
        var user = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId)
                   ?? throw new InvalidOperationException($"User {userId} no longer exists.");
        var identity = new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Email),
            new Claim(ClaimTypes.Role, user.Role)
        }, "BackgroundJob");
        _userContext.User = new ClaimsPrincipal(identity);
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    // ------------------------------------------------------------------ bulk extract

    private async Task<string?> BulkExtractCoreAsync(JobProgress progress, int userId, string url)
    {
        var scraper = Get<IScraperService>();
        var skuService = Get<SkuService>();
        var settings = Get<IOptions<EbaySettings>>().Value;

        progress.Message = "Finding product links...";
        var productLinks = await scraper.ExtractProductLinksAsync(url);
        if (!productLinks.Any())
        {
            throw new InvalidOperationException("No product links were found on that page. Check it is a category/collection URL for a supported site (Safelincs or Eden Horticulture).");
        }

        // Skip products this user already has as listings (re-running a category won't duplicate).
        var existing = (await _context.Listings
                .Where(l => l.OwnerUserId == userId && l.SourceUrl != null)
                .Select(l => l.SourceUrl!)
                .ToListAsync())
            .Select(SourceUrls.Normalize)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        progress.Total = productLinks.Count;
        progress.Message = $"Extracting {productLinks.Count} products...";

        string? DefaultPolicy(string type) => _context.Policies
            .FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == type && p.IsDefault)?.PolicyKey;
        var paymentPolicy = DefaultPolicy("payment") ?? settings.DefaultPaymentPolicyId;
        var fulfillmentPolicy = DefaultPolicy("fulfillment") ?? settings.DefaultFulfillmentPolicyId;
        var returnPolicy = DefaultPolicy("return") ?? settings.DefaultReturnPolicyId;
        var defaultAccountId = _context.EbayTokens
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.IsDefault).ThenBy(t => t.Id)
            .Select(t => (int?)t.Id)
            .FirstOrDefault();

        foreach (var link in productLinks)
        {
            if (existing.Contains(SourceUrls.Normalize(link)))
            {
                progress.ItemSkipped();
                continue;
            }

            try
            {
                var details = await scraper.ExtractDetailsAsync(link);
                if (string.IsNullOrWhiteSpace(details.Title))
                {
                    progress.ItemFailed($"{link}: no title found on page");
                    continue;
                }

                var title = System.Net.WebUtility.HtmlDecode(details.Title).Trim();
                var listing = new Listing
                {
                    OwnerUserId = userId,
                    EbayAccountId = defaultAccountId,
                    Title = title,
                    SourceTitle = title,
                    Description = details.Description ?? string.Empty,
                    // 0 = "price not set": the editor and publish validation will flag it.
                    Price = details.PriceIncVat ?? details.Price ?? 0m,
                    Quantity = 1,
                    Status = "Draft",
                    Brand = string.IsNullOrWhiteSpace(details.Brand) ? null : details.Brand,
                    ImageUrlsJson = JsonSerializer.Serialize(details.ImageUrls ?? new List<string>()),
                    SourceUrl = link,
                    PaymentPolicyId = paymentPolicy,
                    FulfillmentPolicyId = fulfillmentPolicy,
                    ReturnPolicyId = returnPolicy,
                    SourceProductCode = details.ProductCode,
                    SupplierCategory = string.IsNullOrWhiteSpace(details.Category) ? null : details.Category,
                };
                _context.Listings.Add(listing);
                await _context.SaveChangesAsync(); // save per item so progress survives a failure
                await skuService.EnsureSkuAsync(listing);
                await _context.SaveChangesAsync();
                existing.Add(SourceUrls.Normalize(link));
                progress.ItemSucceeded();
            }
            catch (Exception ex)
            {
                _context.ChangeTracker.Clear();
                progress.ItemFailed($"{link}: {ex.Message}");
            }

            await Task.Delay(500); // plus the shared per-host throttle for supplier sites
        }

        return $"Created {progress.Succeeded} drafts.";
    }

    // ------------------------------------------------------------------ bulk publish

    private async Task<string?> BulkPublishCoreAsync(JobProgress progress, int userId, List<int> listingIds)
    {
        var ebayService = Get<IEbayService>();
        var skuService = Get<SkuService>();
        var settings = Get<IOptions<EbaySettings>>().Value;
        var canViewAll = Get<ICurrentUserService>().CanViewAllListings;

        var ids = listingIds.Distinct().ToList();
        progress.Total = ids.Count;
        progress.Message = $"Publishing {ids.Count} listings...";

        foreach (var id in ids)
        {
            var label = $"#{id}";
            try
            {
                var listing = await _context.Listings
                    .FirstOrDefaultAsync(l => l.Id == id && (canViewAll || l.OwnerUserId == userId));
                if (listing == null)
                {
                    progress.ItemFailed($"{label}: not found or access denied");
                    continue;
                }
                label = $"#{id} {Truncate(listing.Title, 50)}";

                var dto = await ListingPublishHelper.PrepareAsync(listing, _context, ebayService, settings, skuService, Get<ICurrentUserService>());
                await _context.SaveChangesAsync(); // keep auto-selected category even if eBay rejects

                var result = await ebayService.CreateListingAsync(dto);
                ListingPublishHelper.ApplyPublishResult(listing, result);
                await _context.SaveChangesAsync();
                progress.ItemSucceeded();
            }
            catch (Exception ex)
            {
                _context.ChangeTracker.Clear();
                Console.WriteLine($"Bulk publish failed for {id}: {ex}");
                progress.ItemFailed($"{label}: {ex.Message}");
            }
        }

        return $"Published {progress.Succeeded}, failed {progress.Failed}.";
    }

    // ------------------------------------------------------------------ fetch supplier details

    private async Task<string?> EnrichCoreAsync(JobProgress progress, List<int> listingIds)
    {
        var enrichment = Get<ProductEnrichmentService>();
        progress.Total = listingIds.Count;
        progress.Message = "Fetching product details from supplier websites...";

        foreach (var listingId in listingIds)
        {
            try
            {
                var listing = await _context.Listings.FirstOrDefaultAsync(l => l.Id == listingId);
                if (listing == null) { progress.ItemSkipped(); continue; }

                var (ok, message) = await enrichment.EnrichListingAsync(listing, force: false);
                if (ok && !message.StartsWith("Could not", StringComparison.Ordinal) && !message.StartsWith("Product page not found", StringComparison.Ordinal))
                    progress.ItemSucceeded();
                else
                    progress.ItemFailed($"#{listingId} {listing.Title}: {message}");
            }
            catch (Exception ex)
            {
                _context.ChangeTracker.Clear();
                progress.ItemFailed($"#{listingId}: {ex.Message}");
            }
            await Task.Delay(400); // plus the shared per-host throttle
        }

        return "Supplier details fetched.";
    }

    // ------------------------------------------------------------------ supplier catalogue import

    private async Task<string?> ImportCoreAsync(JobProgress progress, string supplierCode)
    {
        var importer = Get<IEnumerable<ISupplierCatalogImporter>>()
            .FirstOrDefault(i => i.SupplierCode.Equals(supplierCode, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No catalogue importer for supplier '{supplierCode}'.");

        string? last = null;
        var reporter = new SyncProgress(message => { last = message; progress.Message = message; });
        var result = await importer.ImportAsync("manual", reporter);
        if (!result.IsSuccess) throw new InvalidOperationException(result.ErrorMessage);
        return last;
    }

    // ------------------------------------------------------------------ supplier disabled: eBay quantity 0 / restore

    private async Task<string?> SetSupplierQuantityCoreAsync(JobProgress progress, string supplierCode, bool restore)
    {
        var ebay = Get<IEbayService>();
        var supplier = await _context.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Code == supplierCode)
                       ?? throw new InvalidOperationException($"Supplier '{supplierCode}' not found.");

        var query = SupplierCatalogService.ListingsOf(_context.Listings, _context, supplier.Code, supplier.WebsiteHost)
            .Where(l => l.Status == "Published" && l.Sku != null && l.EbayOfferId != null);
        query = restore ? query.Where(l => l.QuantityZeroedAtUtc != null) : query.Where(l => l.QuantityZeroedAtUtc == null);
        var listings = await query.OrderBy(l => l.Id).ToListAsync();

        progress.Total = listings.Count;
        progress.Message = restore
            ? $"Restoring eBay quantity for {listings.Count} {supplier.Name} listings..."
            : $"Setting eBay quantity to 0 for {listings.Count} {supplier.Name} listings...";

        foreach (var account in listings.GroupBy(l => l.EbayAccountId))
        {
            foreach (var batch in account.Chunk(25))
            {
                var updates = batch.Select(l => new MarketplaceQuantityUpdate(l.Sku!, l.EbayOfferId!, restore ? Math.Max(1, l.Quantity) : 0)).ToList();
                Dictionary<string, string> failures;
                try
                {
                    failures = await ebay.UpdateQuantitiesAsync(account.Key, updates);
                }
                catch (Exception ex)
                {
                    failures = batch.ToDictionary(l => l.Sku!, _ => ex.Message, StringComparer.OrdinalIgnoreCase);
                }

                foreach (var listing in batch)
                {
                    if (failures.TryGetValue(listing.Sku!, out var error))
                    {
                        progress.ItemFailed($"#{listing.Id} {Truncate(listing.Title, 50)}: {error}");
                        continue;
                    }
                    listing.QuantityZeroedAtUtc = restore ? null : DateTime.UtcNow;
                    progress.ItemSucceeded();
                }
                await _context.SaveChangesAsync();
            }
        }

        return restore
            ? $"eBay quantity restored for {progress.Succeeded} listing(s), {progress.Failed} failed."
            : $"eBay quantity set to 0 for {progress.Succeeded} listing(s), {progress.Failed} failed.";
    }

    private static string Truncate(string? value, int max)
        => string.IsNullOrEmpty(value) ? string.Empty : (value.Length <= max ? value : value.Substring(0, max) + "...");
}

/// <summary>Runs progress callbacks inline and in order (unlike Progress&lt;T&gt;, which posts to the thread pool).</summary>
public sealed class SyncProgress : IProgress<string>
{
    private readonly Action<string> _report;
    public SyncProgress(Action<string> report) => _report = report;
    public void Report(string value) => _report(value);
}

/// <summary>Normalises product URLs so the same product found via different paths is recognised.</summary>
public static class SourceUrls
{
    public static string Normalize(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return url.Trim().TrimEnd('/');
        var path = uri.AbsolutePath;
        // Shopify: /collections/x/products/y and /products/y are the same product.
        var productsIdx = path.IndexOf("/products/", StringComparison.OrdinalIgnoreCase);
        if (productsIdx > 0) path = path.Substring(productsIdx);
        return (uri.Host.ToLowerInvariant().Replace("www.", "") + path).TrimEnd('/');
    }
}
