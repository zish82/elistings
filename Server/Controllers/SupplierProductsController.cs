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
    private readonly BulkJobRunner _jobRunner;
    private readonly SkuService _skuService;

    public SupplierProductsController(
        AppDbContext context,
        ICurrentUserService currentUserService,
        IFeedProductSyncService feedProductSyncService,
        IOptions<EbaySettings> ebaySettings,
        ProductEnrichmentService enrichment,
        BulkJobRunner jobRunner,
        SkuService skuService)
    {
        _skuService = skuService;
        _context = context;
        _currentUserService = currentUserService;
        _feedProductSyncService = feedProductSyncService;
        _ebaySettings = ebaySettings;
        _enrichment = enrichment;
        _jobRunner = jobRunner;
    }

    [HttpGet]
    public async Task<ActionResult<List<SupplierProductListItemDto>>> GetProducts([FromQuery] string? supplier = null, [FromQuery] string? search = null)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        var query = _context.SupplierProducts.AsQueryable().Where(p => p.IsActive);
        if (!string.IsNullOrWhiteSpace(supplier))
        {
            query = query.Where(p => p.Supplier == supplier);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(p => p.Title.Contains(term) || (p.SupplierSku ?? string.Empty).Contains(term));
        }

        var products = await query
            .OrderBy(p => p.Supplier)
            .ThenBy(p => p.Title)
            .Take(1000)
            .ToListAsync();

        var productIds = products.Select(p => p.Id).ToList();
        var existingMap = await _context.Listings
            .Where(l => l.OwnerUserId == userId.Value && l.SupplierProductId.HasValue && productIds.Contains(l.SupplierProductId.Value))
            .GroupBy(l => l.SupplierProductId!.Value)
            .Select(g => new { ProductId = g.Key, ListingId = g.OrderBy(x => x.Id).Select(x => x.Id).FirstOrDefault() })
            .ToDictionaryAsync(x => x.ProductId, x => x.ListingId);

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
            ImageUrlsJson = p.ImageUrlsJson
        }).ToList();

        return Ok(payload);
    }

    [HttpGet("{id:int}")]
    public async Task<ActionResult<SupplierProductDetailDto>> GetProduct(int id)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        var product = await _context.SupplierProducts.FirstOrDefaultAsync(p => p.Id == id && p.IsActive);
        if (product == null) return NotFound();

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
            ImageUrlsJson = product.ImageUrlsJson
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

        var products = await _context.SupplierProducts
            .Where(p => productIds.Contains(p.Id) && p.IsActive)
            .ToListAsync();

        var existing = await _context.Listings
            .Where(l => l.OwnerUserId == userId.Value && l.SupplierProductId.HasValue && productIds.Contains(l.SupplierProductId.Value))
            .Select(l => l.SupplierProductId!.Value)
            .ToListAsync();
        var existingSet = existing.ToHashSet();

        var result = new AddSupplierProductsResult();
        var created = new List<Listing>();
        foreach (var product in products)
        {
            if (existingSet.Contains(product.Id) && !allowDuplicates)
            {
                result.SkippedAlreadyAdded++;
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
            var job = _jobRunner.Start("enrich", User, userId.Value, async (services, bulkJob) =>
            {
                var context = services.GetRequiredService<AppDbContext>();
                var enrichment = services.GetRequiredService<ProductEnrichmentService>();
                bulkJob.Total = toEnrich.Count;
                bulkJob.Message = "Fetching product details from supplier websites...";
                foreach (var listingId in toEnrich)
                {
                    try
                    {
                        var listing = await context.Listings.FirstOrDefaultAsync(l => l.Id == listingId);
                        if (listing == null) { bulkJob.ItemSkipped(); continue; }
                        var (ok, message) = await enrichment.EnrichListingAsync(listing, force: false);
                        if (ok && !message.StartsWith("Could not", StringComparison.Ordinal) && !message.StartsWith("Product page not found", StringComparison.Ordinal)) bulkJob.ItemSucceeded();
                        else bulkJob.ItemFailed($"#{listingId} {listing.Title}: {message}");
                    }
                    catch (Exception ex)
                    {
                        context.ChangeTracker.Clear();
                        bulkJob.ItemFailed($"#{listingId}: {ex.Message}");
                    }
                    await Task.Delay(400); // be polite to the supplier site
                }
                bulkJob.Message = "Supplier details fetched.";
            });
            result.EnrichmentJobId = job.Id;
        }

        return Ok(result);
    }

    [HttpPost("sync/catalogue")]
    public async Task<ActionResult<FeedSyncResult>> RunCatalogueSync()
    {
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
        var result = await _feedProductSyncService.RefreshStockAsync("manual-api");
        if (!result.IsSuccess)
        {
            return BadRequest(result);
        }

        return Ok(result);
    }

    [HttpPost("sync/feed/{feedId:int}")]
    public async Task<ActionResult<FeedSyncResult>> SyncFeed(int feedId)
    {
        var result = await _feedProductSyncService.SyncFeedAsync(feedId, "manual-api");
        if (!result.IsSuccess) return BadRequest(result);
        return Ok(result);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> DeleteProduct(int id)
    {
        var product = await _context.SupplierProducts.FirstOrDefaultAsync(p => p.Id == id);
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

        var products = await _context.SupplierProducts
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
            SourceProductCode = product.SupplierSku
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
