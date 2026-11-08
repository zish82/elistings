using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Server.Data;
using Server.Services;
using Shared;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SupplierProductsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ICurrentUserService _currentUserService;
    private readonly IFeedProductSyncService _feedProductSyncService;
    private readonly IOptions<EbaySettings> _ebaySettings;
    private readonly ProductEnrichmentService _enrichment;
    private readonly Server.Services.Jobs.BulkJobService _jobs;
    private readonly SkuService _skuService;

    public SupplierProductsController(
        AppDbContext context,
        ICurrentUserService currentUserService,
        IFeedProductSyncService feedProductSyncService,
        IOptions<EbaySettings> ebaySettings,
        ProductEnrichmentService enrichment,
        Server.Services.Jobs.BulkJobService jobs,
        SkuService skuService)
    {
        _skuService = skuService;
        _context = context;
        _currentUserService = currentUserService;
        _feedProductSyncService = feedProductSyncService;
        _ebaySettings = ebaySettings;
        _enrichment = enrichment;
        _jobs = jobs;
    }

    [HttpGet]
    public async Task<ActionResult<List<SupplierProductListItemDto>>> GetProducts([FromQuery] SupplierProductQuery q)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        // Hidden: deleted by the user (IsActive) or no longer sold by the supplier.
        var query = VisibleProducts();
        if (!string.IsNullOrWhiteSpace(q.Supplier))
        {
            query = query.Where(p => p.Supplier == q.Supplier);
        }

        if (!string.IsNullOrWhiteSpace(q.Search))
        {
            // Lower-cased on both sides: PostgreSQL's LIKE is case-sensitive (SQLite's isn't).
            var term = q.Search.Trim().ToLower();
            query = query.Where(p => p.Title.ToLower().Contains(term) || (p.SupplierSku ?? string.Empty).ToLower().Contains(term));
        }

        if (q.Category == SupplierProductQuery.Uncategorised)
        {
            query = query.Where(p => p.CategoryName == null || p.CategoryName == "");
        }
        else if (!string.IsNullOrWhiteSpace(q.Category))
        {
            query = query.Where(p => p.CategoryName == q.Category);
        }

        if (!string.IsNullOrWhiteSpace(q.GroupKey))
        {
            query = query.Where(p => p.GroupKey == q.GroupKey);
        }

        if (q.TagId == 0)
        {
            var typeTags = _context.ProductTags.Where(t => t.Type == ProductTagTypes.Type).Select(t => t.Id);
            query = query.Where(p => !_context.SupplierProductTags.Any(l => l.SupplierProductId == p.Id && typeTags.Contains(l.ProductTagId)));
        }
        else if (q.TagId.HasValue)
        {
            var tagId = q.TagId.Value;
            query = query.Where(p => _context.SupplierProductTags.Any(l => l.SupplierProductId == p.Id && l.ProductTagId == tagId));
        }

        // SQLite stores decimals as TEXT and EF can't compare/sort them there, so cast to double (REAL) in SQL.
        if (q.MinPrice.HasValue)
        {
            var min = (double)q.MinPrice.Value;
            query = query.Where(p => p.SupplierPrice != null && (double)p.SupplierPrice >= min);
        }
        if (q.MaxPrice.HasValue)
        {
            var max = (double)q.MaxPrice.Value;
            query = query.Where(p => p.SupplierPrice != null && (double)p.SupplierPrice <= max);
        }

        if (q.EstimatesOnly)
        {
            query = query.Where(p => p.PriceIsEstimate);
        }

        if (q.InStockOnly)
        {
            query = query.Where(p => p.StockLevel > 0);
        }

        if (q.HideAdded)
        {
            var uid = userId.Value;
            query = query.Where(p => !_context.Listings.Any(l => l.OwnerUserId == uid && l.SupplierProductId == p.Id));
        }

        query = q.Sort switch
        {
            // Id last everywhere, so "Load more" pages never repeat or skip products with equal values.
            SupplierProductSort.PriceHighToLow => query.OrderByDescending(p => p.SupplierPrice != null).ThenByDescending(p => (double?)p.SupplierPrice).ThenBy(p => p.Id),
            SupplierProductSort.PriceLowToHigh => query.OrderByDescending(p => p.SupplierPrice != null).ThenBy(p => (double?)p.SupplierPrice).ThenBy(p => p.Id),
            SupplierProductSort.ProfitHighToLow => query
                .OrderByDescending(p => p.SuggestedPrice != null && p.SupplierPrice != null)
                .ThenByDescending(p => (double?)p.SuggestedPrice - (double?)p.SupplierPrice)
                .ThenBy(p => p.Id),
            SupplierProductSort.StockHighToLow => query.OrderByDescending(p => p.StockLevel ?? -1).ThenBy(p => p.Id),
            _ => query.OrderBy(p => p.Supplier).ThenBy(p => p.Title).ThenBy(p => p.Id)
        };

        var limit = Math.Clamp(q.Limit, 1, SupplierProductQuery.MaxLimit);
        var products = await query.Skip(Math.Max(0, q.Offset)).Take(limit).ToListAsync();

        // Variant counts per group (visible products only), for the "N sizes" link.
        var groupKeys = products.Where(p => p.GroupKey != null).Select(p => p.GroupKey!).Distinct().ToList();
        var groupSizes = groupKeys.Count == 0
            ? new Dictionary<string, int>()
            : await _context.SupplierProducts
                .Where(p => p.IsActive && p.RemovedFromSupplierAtUtc == null && p.GroupKey != null && groupKeys.Contains(p.GroupKey))
                .GroupBy(p => p.GroupKey!)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);

        var productIds = products.Select(p => p.Id).ToList();

        // Type tags for the rows shown.
        var tagNames = (await (from l in _context.SupplierProductTags
                               join t in _context.ProductTags on l.ProductTagId equals t.Id
                               where productIds.Contains(l.SupplierProductId) && t.Type == ProductTagTypes.Type
                               select new { l.SupplierProductId, t.Name }).ToListAsync())
            .ToLookup(x => x.SupplierProductId, x => x.Name);

        var existingMap = await _context.Listings
            .Where(l => l.OwnerUserId == userId.Value && l.SupplierProductId.HasValue && productIds.Contains(l.SupplierProductId.Value))
            .GroupBy(l => l.SupplierProductId!.Value)
            .Select(g => new { ProductId = g.Key, ListingId = g.OrderBy(x => x.Id).Select(x => x.Id).FirstOrDefault() })
            .ToDictionaryAsync(x => x.ProductId, x => x.ListingId);

        // Estimated eBay fees and profit (suggested price + default delivery).
        var feeSettings = await new MarketplaceFeeService(_context).GetSettingsAsync();
        var suppliersByCode = (await new SupplierCatalogService(_context).GetAllAsync())
            .GroupBy(s => s.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var estimates = products.ToDictionary(p => p.Id,
            p => MarketplaceFeeService.EstimateProduct(p, suppliersByCode.GetValueOrDefault(p.Supplier), feeSettings));

        var payload = products.Select(p => new SupplierProductListItemDto
        {
            Id = p.Id,
            Supplier = p.Supplier,
            Title = p.Title,
            SupplierSku = p.SupplierSku,
            ProductUrl = p.ProductUrl,
            ImageUrl = p.ImageUrl,
            SupplierPrice = p.SupplierPrice,
            StockLevel = p.StockLevel,
            SuggestedPrice = p.SuggestedPrice,
            AlreadyAdded = existingMap.ContainsKey(p.Id),
            ExistingListingId = existingMap.GetValueOrDefault(p.Id),
            CategoryName = p.CategoryName,
            ImageUrlsJson = p.ImageUrlsJson,
            RrpPrice = p.RrpPrice,
            PriceIsEstimate = p.PriceIsEstimate,
            GroupKey = p.GroupKey,
            VariantName = p.VariantName,
            GroupSize = p.GroupKey != null && groupSizes.TryGetValue(p.GroupKey, out var size) ? size : 1,
            Tags = tagNames[p.Id].OrderBy(n => n).ToList(),
            EstimatedProfit = estimates[p.Id]?.Profit,
            EstimatedFees = estimates[p.Id]?.TotalFees,
            PriceIncludesVat = suppliersByCode.GetValueOrDefault(p.Supplier)?.PricesIncludeVat ?? false,
            PriceSource = PriceSource(p),
            CostIncVat = PricingMath.CostIncVat(p.SupplierPrice, suppliersByCode.GetValueOrDefault(p.Supplier))
        }).ToList();

        // The database sorts by a simple profit proxy; put the loaded rows in true estimated-profit order.
        if (q.Sort == SupplierProductSort.ProfitHighToLow)
        {
            payload = payload.OrderByDescending(p => p.EstimatedProfit.HasValue).ThenByDescending(p => p.EstimatedProfit).ToList();
        }

        return Ok(payload);
    }

    /// <summary>Latest background import for a supplier (to show progress after a page refresh) and last update time.</summary>
    [HttpGet("import-status")]
    public async Task<ActionResult<SupplierImportStatusDto>> GetImportStatus([FromQuery] string supplier)
    {
        if (!await Access.IsAllowedAsync(supplier)) return NotFound();
        var job = await _jobs.FindLatestAsync("import", supplier);
        var lastUpdated = await _context.SupplierProducts
            .Where(p => p.Supplier == supplier)
            .OrderByDescending(p => p.UpdatedAtUtc)
            .Select(p => (DateTime?)p.UpdatedAtUtc)
            .FirstOrDefaultAsync();

        return Ok(new SupplierImportStatusDto
        {
            JobId = job?.Id,
            IsRunning = Server.Services.Jobs.BulkJobService.IsActive(job),
            LastUpdatedAtUtc = lastUpdated
        });
    }

    /// <summary>
    /// Products shown on /products: not deleted by the user and still sold by the supplier. Disabled suppliers'
    /// products are hidden from Listers; Managers and Admins still see them.
    /// </summary>
    private IQueryable<SupplierProduct> VisibleProducts()
    {
        // Only the suppliers this user may see (chosen per user on /users; Admins see all).
        var query = Access.Filter(_context.SupplierProducts.Where(p => p.IsActive && p.RemovedFromSupplierAtUtc == null));
        if (_currentUserService.CanViewAllListings) return query;
        var disabled = _context.Suppliers.Where(s => !s.IsActive).Select(s => s.Code);
        return query.Where(p => !disabled.Contains(p.Supplier));
    }

    private SupplierAccessService Access => new(_context, _currentUserService);

    /// <summary>Products per supplier category, for the Products page "Categories" view.</summary>
    [HttpGet("category-summary")]
    public async Task<ActionResult<List<SupplierCategorySummaryDto>>> GetCategorySummary([FromQuery] string? supplier = null, [FromQuery] string? groupBy = null,
        [FromQuery] bool inStockOnly = false)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        var query = VisibleProducts();
        if (!string.IsNullOrWhiteSpace(supplier)) query = query.Where(p => p.Supplier == supplier);
        // Same rule as the product list's "In stock only": only groups with stock, counting in-stock products.
        if (inStockOnly) query = query.Where(p => p.StockLevel > 0);

        var uid = userId.Value;
        if (groupBy == ProductGrouping.Tag) return Ok(await SummaryByTagAsync(query, uid));

        var rows = await query
            .GroupBy(p => p.CategoryName ?? "")
            .Select(g => new { Category = g.Key, Products = g.Count(), InStock = g.Count(p => p.StockLevel > 0) })
            .ToListAsync();
        var added = await query
            .Where(p => _context.Listings.Any(l => l.OwnerUserId == uid && l.SupplierProductId == p.Id))
            .GroupBy(p => p.CategoryName ?? "")
            .Select(g => new { Category = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Category, x => x.Count);

        // "" and null are both "no category"; merge them and list it last.
        return Ok(rows
            .GroupBy(r => string.IsNullOrWhiteSpace(r.Category) ? null : r.Category)
            .Select(g => new SupplierCategorySummaryDto
            {
                Category = g.Key,
                Label = g.Key ?? "(No category)",
                Products = g.Sum(r => r.Products),
                InStock = g.Sum(r => r.InStock),
                AlreadyAdded = g.Sum(r => added.GetValueOrDefault(r.Category))
            })
            .OrderBy(r => r.Category == null)
            .ThenBy(r => r.Category)
            .ToList());
    }

    private async Task<List<SupplierCategorySummaryDto>> SummaryByTagAsync(IQueryable<SupplierProduct> query, int uid)
    {
        var typeTags = _context.ProductTags.Where(t => t.Type == ProductTagTypes.Type);
        var rows = await (from l in _context.SupplierProductTags
                          join t in typeTags on l.ProductTagId equals t.Id
                          join p in query on l.SupplierProductId equals p.Id
                          group p by new { t.Id, t.Name } into g
                          select new { g.Key.Id, g.Key.Name, Products = g.Count(), InStock = g.Count(p => p.StockLevel > 0) })
            .ToListAsync();
        var added = await (from l in _context.SupplierProductTags
                           join t in typeTags on l.ProductTagId equals t.Id
                           join p in query on l.SupplierProductId equals p.Id
                           where _context.Listings.Any(x => x.OwnerUserId == uid && x.SupplierProductId == p.Id)
                           group p by t.Id into g
                           select new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count);

        var untagged = query.Where(p => !_context.SupplierProductTags.Any(l => l.SupplierProductId == p.Id && typeTags.Any(t => t.Id == l.ProductTagId)));
        var untaggedCount = await untagged.CountAsync();

        var result = rows
            .Select(r => new SupplierCategorySummaryDto
            {
                Id = r.Id, Label = r.Name, Category = r.Name, Products = r.Products, InStock = r.InStock, AlreadyAdded = added.GetValueOrDefault(r.Id)
            })
            .OrderBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (untaggedCount > 0)
        {
            result.Add(new SupplierCategorySummaryDto
            {
                Id = 0, Label = "(No tag)", Products = untaggedCount,
                InStock = await untagged.CountAsync(p => p.StockLevel > 0),
                AlreadyAdded = await untagged.CountAsync(p => _context.Listings.Any(l => l.OwnerUserId == uid && l.SupplierProductId == p.Id))
            });
        }
        return result;
    }

    /// <summary>Type tags of a supplier (for the tag filter), with product counts.</summary>
    [HttpGet("tags")]
    public async Task<ActionResult<List<ProductTagDto>>> GetTags([FromQuery] string? supplier)
    {
        if (string.IsNullOrWhiteSpace(supplier)) return Ok(new List<ProductTagDto>());
        var visible = VisibleProducts().Where(p => p.Supplier == supplier);
        var rows = await (from l in _context.SupplierProductTags
                          join t in _context.ProductTags on l.ProductTagId equals t.Id
                          join p in visible on l.SupplierProductId equals p.Id
                          where t.Supplier == supplier && t.Type == ProductTagTypes.Type
                          group l by new { t.Id, t.Name, t.Type } into g
                          select new ProductTagDto { Id = g.Key.Id, Name = g.Key.Name, Type = g.Key.Type, ProductCount = g.Count() })
            .ToListAsync();
        return Ok(rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>Where a product's supplier price came from (shown next to the price).</summary>
    internal static string PriceSource(SupplierProduct p)
    {
        var fetched = p.TradePriceFetchedAtUtc.HasValue ? $" ({p.TradePriceFetchedAtUtc.Value:d MMM yyyy})" : "";
        return p.Supplier switch
        {
            "Safelincs" => p.PriceIsEstimate
                ? "Safelincs website price (ex VAT) – not in the trade price list"
                : "Safelincs trade price list" + fetched,
            "EdenHorticulture" => p.PriceIsEstimate
                ? "Eden RRP – trade price not confirmed yet"
                : "Eden trade price (logged-in)" + fetched,
            "DropShipMall" => "DropShipMall trade feed",
            _ => p.PriceIsEstimate ? "Estimate" : "Supplier"
        };
    }

    /// <summary>Distinct categories for the category filter.</summary>
    [HttpGet("categories")]
    public async Task<ActionResult<List<string>>> GetCategories([FromQuery] string? supplier = null)
    {
        var query = VisibleProducts().Where(p => p.CategoryName != null && p.CategoryName != "");
        if (!string.IsNullOrWhiteSpace(supplier)) query = query.Where(p => p.Supplier == supplier);
        return Ok(await query.Select(p => p.CategoryName!).Distinct().OrderBy(c => c).ToListAsync());
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SupplierProductDetailDto>> GetProduct(int id)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        var product = await Access.Filter(_context.SupplierProducts).FirstOrDefaultAsync(p => p.Id == id && p.IsActive);
        if (product == null) return NotFound();
        var supplierSettings = (await new SupplierCatalogService(_context).GetAllAsync())
            .FirstOrDefault(s => s.Code.Equals(product.Supplier, StringComparison.OrdinalIgnoreCase));

        var existingListingId = await _context.Listings
            .Where(l => l.OwnerUserId == userId.Value && l.SupplierProductId == id)
            .OrderBy(l => l.Id)
            .Select(l => (int?)l.Id)
            .FirstOrDefaultAsync();

        return Ok(new SupplierProductDetailDto
        {
            Id = product.Id,
            Supplier = product.Supplier,
            Title = product.Title,
            SupplierSku = product.SupplierSku,
            ProductUrl = product.ProductUrl,
            ImageUrl = product.ImageUrl,
            SupplierPrice = product.SupplierPrice,
            StockLevel = product.StockLevel,
            SuggestedPrice = product.SuggestedPrice,
            Description = product.Description,
            Brand = product.Brand,
            Currency = product.Currency,
            UpdatedAtUtc = product.UpdatedAtUtc,
            AlreadyAdded = existingListingId.HasValue,
            ExistingListingId = existingListingId,
            CategoryName = product.CategoryName,
            ImageUrlsJson = product.ImageUrlsJson,
            RrpPrice = product.RrpPrice,
            PriceIsEstimate = product.PriceIsEstimate,
            PriceIncludesVat = supplierSettings?.PricesIncludeVat ?? false,
            PriceSource = PriceSource(product),
            CostIncVat = PricingMath.CostIncVat(product.SupplierPrice, supplierSettings)
        });
    }

    [HttpPost("add-to-my-listings")]
    public async Task<ActionResult<AddSupplierProductsResult>> AddToMyListings([FromBody] AddSupplierProductsRequest request)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        var productIds = request?.ProductIds?.Distinct().ToList() ?? new List<int>();
        var allowDuplicates = request?.AllowDuplicates == true;
        if (!productIds.Any()) return BadRequest("No products selected.");

        var products = await VisibleProducts()
            .Where(p => productIds.Contains(p.Id))
            .ToListAsync();

        var existing = await _context.Listings
            .Where(l => l.OwnerUserId == userId.Value && l.SupplierProductId.HasValue && productIds.Contains(l.SupplierProductId.Value))
            .Select(l => l.SupplierProductId!.Value)
            .ToListAsync();
        var existingSet = existing.ToHashSet();

        var result = new AddSupplierProductsResult();
        // Another user already posted the product on an account this user also posts to (a shared account)?
        var conflicts = request?.AllowSharedAccountDuplicates == true
            ? new List<SharedAccountConflictDto>()
            : await SharedAccountConflictsAsync(userId.Value, products);
        var created = new List<Listing>();
        foreach (var product in products)
        {
            if (existingSet.Contains(product.Id) && !allowDuplicates)
            {
                result.SkippedAlreadyAdded++;
                continue;
            }
            var productConflicts = conflicts.Where(c => c.ProductId == product.Id).ToList();
            if (productConflicts.Count > 0)
            {
                result.SharedAccountConflicts.AddRange(productConflicts);
                continue;
            }

            var listing = CreateListingFromProduct(userId.Value, product);
            await _enrichment.FillListingFromProductAsync(listing, product); // price (VAT + margin), delivery, images, specifics
            _context.Listings.Add(listing);
            created.Add(listing);
            result.Added++;
        }

        await _context.SaveChangesAsync();

        // SKUs are generated after the first save because the fallback format uses the listing Id.
        foreach (var listing in created) await _skuService.EnsureSkuAsync(listing);
        await _context.SaveChangesAsync();
        result.AddedListingIds = created.Select(l => l.Id).ToList();

        // Fetch descriptions/specifications from the supplier websites in the background,
        // then fill the new listings' missing fields.
        var toEnrich = created
            .Where(l => products.Any(p => p.Id == l.SupplierProductId && _enrichment.HasEnricherFor(p.Supplier)))
            .Select(l => l.Id)
            .ToList();
        if (toEnrich.Any())
        {
            // Runs on Hangfire (BackgroundJobs.EnrichListingsAsync); the page shows its progress.
            result.EnrichmentJobId = await _jobs.EnqueueEnrichAsync(userId.Value, toEnrich);
        }

        return Ok(result);
    }

    /// <summary>
    /// Products already published by another user on an account this user can post with (accounts are shared by an Admin).
    /// Only the account and marketplace are reported – not the other user's listing.
    /// </summary>
    private async Task<List<SharedAccountConflictDto>> SharedAccountConflictsAsync(int userId, List<SupplierProduct> products)
    {
        var accountIds = await AccountAccess.AccountIdsForUserAsync(_context, userId);
        if (accountIds.Count == 0 || products.Count == 0) return new List<SharedAccountConflictDto>();
        var productIds = products.Select(p => p.Id).ToList();

        var rows = await (
                from l in _context.Listings
                join p in _context.ListingPublications on l.Id equals p.ListingId
                where l.OwnerUserId != userId
                      && l.Status != "Deleted"
                      && l.SupplierProductId != null && productIds.Contains(l.SupplierProductId.Value)
                      && p.Status == PublicationStatuses.Published
                      && accountIds.Contains(p.AccountId)
                select new { ProductId = l.SupplierProductId!.Value, p.Marketplace, p.AccountId })
            .Distinct()
            .ToListAsync();
        if (rows.Count == 0) return new List<SharedAccountConflictDto>();

        var conflictAccountIds = rows.Select(r => r.AccountId).Distinct().ToList();
        var names = await _context.EbayTokens.Where(t => conflictAccountIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name);
        return rows.Select(r => new SharedAccountConflictDto
        {
            ProductId = r.ProductId,
            ProductTitle = products.First(p => p.Id == r.ProductId).Title,
            Marketplace = r.Marketplace,
            AccountName = names.GetValueOrDefault(r.AccountId) ?? $"Account {r.AccountId}"
        }).ToList();
    }

    [HttpPost("sync/catalogue")]
    public async Task<ActionResult<FeedSyncResult>> RunCatalogueSync()
    {
        if (!await Access.IsAllowedAsync("DropShipMall")) return NotFound();
        var result = await _feedProductSyncService.ImportCatalogueAsync("manual-api");
        if (!result.IsSuccess)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("sync/stock")]
    public async Task<ActionResult<FeedSyncResult>> RunStockSync()
    {
        if (!await Access.IsAllowedAsync("DropShipMall")) return NotFound();
        var result = await _feedProductSyncService.RefreshStockAsync("manual-api");
        if (!result.IsSuccess)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("sync/feed/{feedId:int}")]
    public async Task<ActionResult<FeedSyncResult>> SyncFeed(int feedId, [FromServices] IEnumerable<ISupplierCatalogImporter> importers)
    {
        // Suppliers with their own importer (e.g. Eden) run in the background; progress via bulk-jobs.
        var feed = await _context.SupplierFeeds.FirstOrDefaultAsync(f => f.Id == feedId);
        if (feed != null && !await Access.IsAllowedAsync(feed.Supplier)) return NotFound();
        if (feed != null && await _context.Suppliers.AnyAsync(s => s.Code == feed.Supplier && !s.IsActive))
        {
            return BadRequest(new FeedSyncResult { Supplier = feed.Supplier, FeedType = feed.FeedType, ErrorMessage = $"{feed.Supplier} is disabled. Enable it on Supplier Feeds to import." });
        }
        var importer = feed == null ? null : importers.FirstOrDefault(i => i.SupplierCode.Equals(feed.Supplier, StringComparison.OrdinalIgnoreCase));
        if (feed != null && importer != null)
        {
            var latest = await _jobs.FindLatestAsync("import", importer.SupplierCode);
            if (Server.Services.Jobs.BulkJobService.IsActive(latest))
            {
                // Re-attach to the queued/running import instead of starting another.
                return Ok(new FeedSyncResult { Supplier = feed.Supplier, FeedType = feed.FeedType, JobId = latest!.Id });
            }

            var userId = _currentUserService.UserId;
            if (userId == null) return Unauthorized();

            // Runs on Hangfire (Eden imports on the single-worker "eden" queue).
            var jobId = await _jobs.EnqueueSupplierImportAsync(userId.Value, importer.SupplierCode);
            return Ok(new FeedSyncResult { Supplier = feed.Supplier, FeedType = feed.FeedType, JobId = jobId });
        }

        var result = await _feedProductSyncService.SyncFeedAsync(feedId, "manual-api");
        if (!result.IsSuccess) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteProduct(int id)
    {
        var product = await Access.Filter(_context.SupplierProducts).FirstOrDefaultAsync(p => p.Id == id);
        if (product == null) return NotFound();

        product.IsActive = false;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("delete")]
    public async Task<ActionResult<BulkProductDeleteResult>> DeleteProducts([FromBody] List<int> ids)
    {
        var distinctIds = ids?.Distinct().ToList() ?? new List<int>();
        if (!distinctIds.Any()) return BadRequest("No product IDs provided.");

        var products = await Access.Filter(_context.SupplierProducts)
            .Where(p => distinctIds.Contains(p.Id) && p.IsActive)
            .ToListAsync();
        foreach (var product in products) product.IsActive = false;
        await _context.SaveChangesAsync();

        return Ok(new BulkProductDeleteResult { Deleted = products.Count });
    }

    private Listing CreateListingFromProduct(int userId, SupplierProduct product)
    {
        var paymentId = GetDefaultPolicy("payment");
        var fulfillmentId = GetDefaultPolicy("fulfillment");
        var returnId = GetDefaultPolicy("return");

        return new Listing
        {
            OwnerUserId = userId,
            EbayAccountId = GetDefaultEbayAccountId(userId),
            SupplierProductId = product.Id,
            Title = product.Title,
            Description = product.Description ?? string.Empty,
            Price = 0m, // set by ProductEnrichmentService.FillListingFromProductAsync (cost + VAT + margin)
            Status = "Draft",
            PaymentPolicyId = paymentId,
            FulfillmentPolicyId = fulfillmentId,
            ReturnPolicyId = returnId,
            Brand = string.IsNullOrWhiteSpace(product.Brand) ? "Unbranded" : product.Brand,
            Type = "Not Specified",
            Colour = "Multicoloured",
            ImageUrlsJson = null, // filled from all product images by FillListingFromProductAsync
            SourceUrl = product.ProductUrl,
            SourcePrice = product.SupplierPrice,
            SourceStockLevel = product.StockLevel,
            SourceProductCode = product.SupplierSku,
            SupplierCategory = product.CategoryName
        };
    }

    private int? GetDefaultEbayAccountId(int userId)
    {
        return _context.EbayTokens
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Id)
            .Select(t => (int?)t.Id)
            .FirstOrDefault();
    }

    private string GetDefaultPolicy(string type)
    {
        var fromDb = _context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == type && p.IsDefault)?.PolicyKey;
        if (!string.IsNullOrWhiteSpace(fromDb)) return fromDb;

        return type switch
        {
            "payment" => _ebaySettings.Value.DefaultPaymentPolicyId,
            "fulfillment" => _ebaySettings.Value.DefaultFulfillmentPolicyId,
            "return" => _ebaySettings.Value.DefaultReturnPolicyId,
            _ => string.Empty
        };
    }
}
