using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Services;
using Shared;
using System.Text.Json;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ListingsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IScraperService _scraperService;
    private readonly IEbayService _ebayService;
    private readonly IEdenSupplierSyncService _edenSupplierSyncService;
    private readonly Microsoft.Extensions.Options.IOptions<Server.Configuration.EbaySettings> _ebaySettings;
    private readonly ICurrentUserService _currentUserService;
    private readonly SkuService _skuService;

    public ListingsController(AppDbContext context, IScraperService scraperService, IEbayService ebayService, IEdenSupplierSyncService edenSupplierSyncService, Microsoft.Extensions.Options.IOptions<Server.Configuration.EbaySettings> ebaySettings, ICurrentUserService currentUserService, SkuService skuService)
    {
        _skuService = skuService;
        _context = context;
        _scraperService = scraperService;
        _ebayService = ebayService;
        _edenSupplierSyncService = edenSupplierSyncService;
        _ebaySettings = ebaySettings;
        _currentUserService = currentUserService;
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

    [HttpGet("extract")]
    public async Task<ActionResult<ExtractedDetailsDto>> ExtractFromUrl([FromQuery] string url)
    {
        if (string.IsNullOrEmpty(url)) return BadRequest("URL is required");
        var supplierError = await SupplierAccessErrorAsync(url);
        if (supplierError != null) return StatusCode(StatusCodes.Status403Forbidden, supplierError);
        try
        {
            var details = await _scraperService.ExtractDetailsAsync(url);
            return Ok(details);
        }
        catch (Exception ex)
        {
            return BadRequest($"Failed to extract: {ex.Message}");
        }
    }

    [HttpGet]
    public async Task<ActionResult<List<ListingDto>>> GetListings([FromServices] EbayCategoryService? ebayCategories = null)
    {
        var query = _context.Listings.AsQueryable();
        if (!_currentUserService.CanViewAllListings)
        {
            var userId = _currentUserService.UserId;
            if (userId == null) return Unauthorized();
            query = query.Where(l => l.OwnerUserId == userId.Value);
        }

        var listings = await query
            .Select(l => new ListingDto
            {
                Id = l.Id,
                EbayAccountId = l.EbayAccountId,
                SupplierProductId = l.SupplierProductId,
                EbayAccountName = _context.EbayTokens.Where(t => t.Id == l.EbayAccountId).Select(t => t.Name).FirstOrDefault(),
                OwnerUserId = l.OwnerUserId,
                OwnerEmail = _context.Users.Where(u => u.Id == l.OwnerUserId).Select(u => u.Email).FirstOrDefault(),
                Title = l.Title,
                Description = l.Description,
                Price = l.Price,
                Quantity = l.Quantity,
                Status = l.Status,
                EbayItemId = l.EbayItemId,
                CategoryId = l.CategoryId,
                CategoryName = l.CategoryName,
                FulfillmentPolicyId = l.FulfillmentPolicyId,
                PaymentPolicyId = l.PaymentPolicyId,
                ReturnPolicyId = l.ReturnPolicyId,
                Type = l.Type,
                Brand = l.Brand,
                Colour = l.Colour,
                Sku = l.Sku,
                EbayOfferId = l.EbayOfferId,
                ImageUrls = string.IsNullOrEmpty(l.ImageUrlsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(l.ImageUrlsJson, (JsonSerializerOptions?)null) ?? new List<string>()
                ,
                SourceUrl = l.SourceUrl,
                SourcePrice = l.SourcePrice,
                SourceStockLevel = l.SourceStockLevel,
                SourceProductCode = l.SourceProductCode,
                Marketplace = l.Marketplace,
                DeliveryOptionName = l.DeliveryOptionName,
                DeliveryCost = l.DeliveryCost,
                IncludeDeliveryInPrice = l.IncludeDeliveryInPrice,
                Barcode = l.Barcode,
                SupplierGroupKey = _context.SupplierProducts.Where(p => p.Id == l.SupplierProductId).Select(p => p.GroupKey).FirstOrDefault(),
                SupplierCode = _context.SupplierProducts.Where(p => p.Id == l.SupplierProductId).Select(p => p.Supplier).FirstOrDefault(),
                SupplierCategory = l.SupplierCategory,
                QuantityZeroedAtUtc = l.QuantityZeroedAtUtc,
                SourceTitle = l.SourceTitle
            })
            .ToListAsync();

        // Estimated eBay fees and profit per sale.
        var feeSettings = await new MarketplaceFeeService(_context).GetSettingsAsync();
        var allSuppliers = await new SupplierCatalogService(_context).GetAllAsync();
        // eBay category → parent, to find each listing's fee rate (top-level categories carry the rates).
        IReadOnlyDictionary<string, string?> parents = listings.Any(l => !string.IsNullOrWhiteSpace(l.CategoryId)) && ebayCategories != null
            ? await ebayCategories.GetParentsAsync() // cached
            : new Dictionary<string, string?>();
        foreach (var dto in listings)
        {
            var supplier = dto.SupplierCode != null
                ? allSuppliers.FirstOrDefault(s => s.Code.Equals(dto.SupplierCode, StringComparison.OrdinalIgnoreCase))
                : dto.SourceUrl == null ? null : allSuppliers.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s.WebsiteHost)
                                                                               && dto.SourceUrl.Contains(s.WebsiteHost!, StringComparison.OrdinalIgnoreCase));
            var chain = string.IsNullOrWhiteSpace(dto.CategoryId) ? null : EbayCategoryService.Chain(dto.CategoryId.Trim(), parents);
            var estimate = MarketplaceFeeService.EstimateSale(dto.Price, dto.DeliveryCost, dto.IncludeDeliveryInPrice, dto.SourcePrice, chain, supplier, feeSettings);
            dto.EstimatedProfit = estimate.Profit;
            dto.EstimatedFees = estimate.TotalFees;
            dto.BelowMinProfit = estimate.Profit.HasValue && estimate.Profit.Value < feeSettings.MinProfit;
        }

        // Flag listings of disabled suppliers (by linked product, else by the source URL's website).
        var disabled = await _context.Suppliers.AsNoTracking().Where(s => !s.IsActive).Select(s => new { s.Code, s.WebsiteHost }).ToListAsync();
        if (disabled.Count > 0)
        {
            foreach (var dto in listings)
            {
                dto.SupplierDisabled = dto.SupplierCode != null
                    ? disabled.Any(s => s.Code.Equals(dto.SupplierCode, StringComparison.OrdinalIgnoreCase))
                    : dto.SourceUrl != null && disabled.Any(s => !string.IsNullOrWhiteSpace(s.WebsiteHost)
                                                                && dto.SourceUrl.Contains(s.WebsiteHost!, StringComparison.OrdinalIgnoreCase));
            }
        }

        return Ok(listings);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ListingDto>> GetListing(int id)
    {
        var l = await FindAccessibleListing(id);
        if (l == null) return NotFound();

        var ebayAccountName = await _context.EbayTokens.Where(t => t.Id == l.EbayAccountId).Select(t => t.Name).FirstOrDefaultAsync();
        var ownerEmail = await _context.Users.Where(u => u.Id == l.OwnerUserId).Select(u => u.Email).FirstOrDefaultAsync();
        return Ok(ToListingDto(l, ebayAccountName, ownerEmail));
    }

    /// <summary>
    /// Fetches description/specifications/images from the supplier website for the linked product
    /// and fills any empty fields on the listing (fields already filled in are kept).
    /// </summary>
    [HttpPost("{id}/fetch-supplier-details")]
    public async Task<ActionResult<EnrichResultDto>> FetchSupplierDetails(int id, [FromServices] ProductEnrichmentService enrichment)
    {
        var listing = await FindAccessibleListing(id);
        if (listing == null) return NotFound();

        var (ok, message) = await enrichment.EnrichListingAsync(listing, force: true);
        var ebayAccountName = await _context.EbayTokens.Where(t => t.Id == listing.EbayAccountId).Select(t => t.Name).FirstOrDefaultAsync();
        var ownerEmail = await _context.Users.Where(u => u.Id == listing.OwnerUserId).Select(u => u.Email).FirstOrDefaultAsync();
        return Ok(new EnrichResultDto { Success = ok, Message = message, Listing = ToListingDto(listing, ebayAccountName, ownerEmail) });
    }

    [HttpPost("{id}/reset-from-source")]
    public async Task<ActionResult<ListingDto>> ResetFromSource(int id)
    {
        var listing = await FindAccessibleListing(id);
        if (listing == null) return NotFound();

        if (!listing.SupplierProductId.HasValue)
        {
            return BadRequest("Listing is not linked to a supplier product.");
        }

        var product = await _context.SupplierProducts
            .FirstOrDefaultAsync(p => p.Id == listing.SupplierProductId.Value && p.IsActive);
        if (product == null)
        {
            return NotFound("Linked supplier product was not found.");
        }

        var imageUrls = new List<string>();
        if (!string.IsNullOrWhiteSpace(product.ImageUrl))
        {
            imageUrls.Add(product.ImageUrl);
        }

        listing.Title = product.Title;
        listing.Description = product.Description ?? string.Empty;
        listing.Brand = string.IsNullOrWhiteSpace(product.Brand) ? listing.Brand : product.Brand;
        listing.Price = product.SuggestedPrice ?? product.SupplierPrice ?? listing.Price;
        listing.ImageUrlsJson = JsonSerializer.Serialize(imageUrls);
        listing.SourceUrl = product.ProductUrl;
        listing.SourcePrice = product.SupplierPrice;
        listing.SourceStockLevel = product.StockLevel;
        listing.SourceProductCode = product.SupplierSku;

        await _context.SaveChangesAsync();

        var ebayAccountName = await _context.EbayTokens.Where(t => t.Id == listing.EbayAccountId).Select(t => t.Name).FirstOrDefaultAsync();
        var ownerEmail = await _context.Users.Where(u => u.Id == listing.OwnerUserId).Select(u => u.Email).FirstOrDefaultAsync();
        return Ok(ToListingDto(listing, ebayAccountName, ownerEmail));
    }

    // Manually uploaded images are sent as base64 data URLs (up to 10 x 5MB), which exceeds Kestrel's 30MB default.
    [HttpPost]
    [RequestSizeLimit(100_000_000)]
    public async Task<ActionResult<ListingDto>> CreateListing(CreateListingRequest request)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        string GetDefault(string provided, string type)
        {
            if (!string.IsNullOrEmpty(provided)) return provided;
            var dbEntry = _context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == type && p.IsDefault);
            if (dbEntry != null) return dbEntry.PolicyKey;
            return type switch
            {
                "payment" => _ebaySettings.Value.DefaultPaymentPolicyId,
                "fulfillment" => _ebaySettings.Value.DefaultFulfillmentPolicyId,
                "return" => _ebaySettings.Value.DefaultReturnPolicyId,
                _ => provided
            } ?? string.Empty;
        }

        var sku = SkuService.Normalize(request.Sku);
        if (sku != null && await _skuService.IsInUseAsync(sku, null))
        {
            return BadRequest($"SKU '{sku}' is already used by another listing.");
        }

        var paymentId = GetDefault(request.PaymentPolicyId, "payment");
        var fulfillmentId = GetDefault(request.FulfillmentPolicyId, "fulfillment");
        var returnId = GetDefault(request.ReturnPolicyId, "return");

        var listing = new Listing
        {
            OwnerUserId = userId.Value,
            EbayAccountId = request.EbayAccountId ?? GetDefaultEbayAccountId(userId.Value),
            SupplierProductId = request.SupplierProductId,
            Title = request.Title,
            Description = request.Description,
            Price = request.Price,
            Quantity = request.Quantity < 1 ? 1 : request.Quantity,
            Sku = sku,
            Status = "Draft",
            CategoryId = request.CategoryId,
            CategoryName = request.CategoryName,
            FulfillmentPolicyId = fulfillmentId,
            PaymentPolicyId = paymentId,
            ReturnPolicyId = returnId,
            Type = request.Type,
            Brand = request.Brand,
            Colour = request.Colour,
            ImageUrlsJson = JsonSerializer.Serialize(request.ImageUrls)
            ,
            SourceUrl = request.SourceUrl,
            SourcePrice = request.SourcePrice,
            SourceStockLevel = request.SourceStockLevel,
            SourceProductCode = request.SourceProductCode
        };
        ListingMapping.ApplyExtras(listing, request);
        
        _context.Listings.Add(listing);
        await _context.SaveChangesAsync();

        // No SKU entered: generate one now (the fallback format needs the new listing's Id).
        if (listing.Sku == null)
        {
            await _skuService.EnsureSkuAsync(listing);
            await _context.SaveChangesAsync();
        }
        
        return CreatedAtAction(nameof(GetListing), new { id = listing.Id }, new ListingDto 
        { 
            Id = listing.Id, 
            EbayAccountId = listing.EbayAccountId,
            SupplierProductId = listing.SupplierProductId,
            OwnerUserId = listing.OwnerUserId,
            Title = listing.Title, 
            Description = listing.Description, 
            Price = listing.Price,
            Quantity = listing.Quantity,
            Sku = listing.Sku,
            Status = listing.Status,
            CategoryId = listing.CategoryId,
            CategoryName = listing.CategoryName,
            FulfillmentPolicyId = listing.FulfillmentPolicyId,
            PaymentPolicyId = listing.PaymentPolicyId,
            ReturnPolicyId = listing.ReturnPolicyId,
            Type = listing.Type,
            Brand = listing.Brand,
            Colour = listing.Colour
            ,
            SourceUrl = listing.SourceUrl,
            SourcePrice = listing.SourcePrice,
            SourceStockLevel = listing.SourceStockLevel,
            SourceProductCode = listing.SourceProductCode
        }.WithExtras(listing));
    }

    [HttpPut("{id}")]
    [RequestSizeLimit(100_000_000)]
    public async Task<IActionResult> UpdateListing(int id, CreateListingRequest request)
    {
        var listing = await FindAccessibleListing(id);
        if (listing == null) return NotFound();

        // SKU: keep the existing one when none is supplied; once published, the eBay inventory item is keyed by SKU so it can't change.
        var requestedSku = SkuService.Normalize(request.Sku);
        if (requestedSku != null && !string.Equals(requestedSku, listing.Sku, StringComparison.Ordinal))
        {
            if (listing.Status == "Published")
            {
                return BadRequest("SKU cannot be changed after the listing has been published.");
            }
            if (await _skuService.IsInUseAsync(requestedSku, listing.Id))
            {
                return BadRequest($"SKU '{requestedSku}' is already used by another listing.");
            }
            listing.Sku = requestedSku;
        }

        listing.Title = request.Title;
        listing.Description = request.Description;
        listing.Price = request.Price;
        listing.Quantity = request.Quantity < 1 ? 1 : request.Quantity;
        listing.EbayAccountId = request.EbayAccountId ?? GetDefaultEbayAccountId(listing.OwnerUserId);
        listing.SupplierProductId = request.SupplierProductId;
        listing.CategoryId = request.CategoryId;
        listing.CategoryName = request.CategoryName;
        listing.FulfillmentPolicyId = request.FulfillmentPolicyId;
        listing.PaymentPolicyId = request.PaymentPolicyId;
        listing.ReturnPolicyId = request.ReturnPolicyId;
        listing.Type = request.Type;
        listing.Brand = request.Brand;
        listing.Colour = request.Colour;
        listing.ImageUrlsJson = JsonSerializer.Serialize(request.ImageUrls);
        listing.SourceUrl = request.SourceUrl;
        listing.SourcePrice = request.SourcePrice;
        listing.SourceStockLevel = request.SourceStockLevel;
        listing.SourceProductCode = request.SourceProductCode;
        ListingMapping.ApplyExtras(listing, request);
        await _skuService.EnsureSkuAsync(listing); // older drafts without a SKU get one on save

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("bulk-extract")]
    public async Task<ActionResult<BulkJobStartedDto>> BulkExtract([FromBody] string categoryUrl, [FromServices] Server.Services.Jobs.BulkJobService jobs)
    {
        if (string.IsNullOrWhiteSpace(categoryUrl)) return BadRequest("Category URL is required");
        if (!Uri.TryCreate(categoryUrl.Trim(), UriKind.Absolute, out _)) return BadRequest("Category URL is not a valid URL.");

        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();
        var supplierError = await SupplierAccessErrorAsync(categoryUrl.Trim());
        if (supplierError != null) return StatusCode(StatusCodes.Status403Forbidden, supplierError);

        // Runs on Hangfire (see BackgroundJobs.BulkExtractAsync); the page polls bulk-jobs/{id} for progress.
        var jobId = await jobs.EnqueueBulkExtractAsync(userId.Value, categoryUrl.Trim());
        return Ok(new BulkJobStartedDto { JobId = jobId });
    }

    [HttpGet("bulk-jobs/{jobId}")]
    public async Task<ActionResult<BulkJobDto>> GetBulkJob(string jobId, [FromServices] Server.Services.Jobs.BulkJobService jobs)
    {
        var job = await jobs.GetAsync(jobId);
        if (job == null) return NotFound("Job not found.");
        var owner = await _context.BulkJobs.Where(j => j.Id == jobId).Select(j => j.OwnerUserId).FirstAsync();
        if (owner != _currentUserService.UserId && !_currentUserService.CanViewAllListings) return NotFound();
        return Ok(job);
    }

    [HttpPost("bulk-delete")]
    public async Task<ActionResult> BulkDelete([FromBody] List<int> ids)
    {
        if (ids == null || !ids.Any()) return BadRequest("No IDs provided");

        try
        {
            var query = _context.Listings.Where(l => ids.Contains(l.Id));
            if (!_currentUserService.CanViewAllListings)
            {
                var userId = _currentUserService.UserId;
                if (userId == null) return Unauthorized();
                query = query.Where(l => l.OwnerUserId == userId.Value);
            }

            var toProcess = await query.ToListAsync();
            var marked = 0;
            var deleted = 0;

            foreach (var l in toProcess)
            {
                if (string.Equals(l.Status, "Published", StringComparison.OrdinalIgnoreCase))
                {
                    l.Status = "Deleted";
                    marked++;
                }
                else
                {
                    _context.Listings.Remove(l);
                    deleted++;
                }
            }

            await _context.SaveChangesAsync();
            return Ok(new { Marked = marked, Deleted = deleted });
        }
        catch (Exception ex)
        {
            return BadRequest($"Bulk delete failed: {ex.Message}");
        }
    }

    [HttpGet("{id}/supplier-status")]
    public async Task<ActionResult<SupplierStatusDto>> GetSupplierStatus(int id)
    {
        var listing = await FindAccessibleListing(id);
        if (listing == null) return NotFound();

        var snapshot = await _edenSupplierSyncService.GetLatestSnapshotAsync(id);
        if (snapshot == null)
        {
            return Ok(new SupplierStatusDto
            {
                ListingId = id,
                Supplier = ResolveSupplierName(listing.SourceUrl),
                IsSuccess = false,
                ErrorMessage = "No supplier sync data yet."
            });
        }

        return Ok(snapshot);
    }

    [HttpPost("{id}/supplier-status/refresh")]
    public async Task<ActionResult<SupplierStatusDto>> RefreshSupplierStatus(int id, [FromServices] SupplierStockService stockService)
    {
        var listing = await FindAccessibleListing(id);
        if (listing == null) return NotFound();

        return Ok(await stockService.CheckListingAsync(listing, "manual"));
    }

    /// <summary>Stock check for a listing that hasn't been saved yet.</summary>
    [HttpPost("supplier-status/check")]
    public async Task<ActionResult<SupplierStatusDto>> CheckSupplierStatus([FromBody] SupplierStatusCheckRequest request, [FromServices] SupplierStockService stockService)
    {
        if (request == null || (string.IsNullOrWhiteSpace(request.SourceUrl) && string.IsNullOrWhiteSpace(request.SupplierSku)))
        {
            return BadRequest("SourceUrl or SupplierSku is required.");
        }

        return Ok(await stockService.CheckAsync(new SupplierStockQuery(request.SourceUrl, request.SupplierSku, null)));
    }

    /// <summary>A message when the URL is a supplier's website the user hasn't been given access to.</summary>
    private async Task<string?> SupplierAccessErrorAsync(string url)
    {
        var access = new SupplierAccessService(_context, _currentUserService);
        var code = await access.SupplierCodeForUrlAsync(url);
        return code == null || await access.IsAllowedAsync(code) ? null : $"You don't have access to the supplier {code}. Ask an Admin.";
    }

    private async Task<Listing?> FindAccessibleListing(int id)
    {
        var query = _context.Listings.Where(l => l.Id == id);
        if (!_currentUserService.CanViewAllListings)
        {
            var userId = _currentUserService.UserId;
            if (userId == null) return null;
            query = query.Where(l => l.OwnerUserId == userId.Value);
        }

        return await query.FirstOrDefaultAsync();
    }

    private static string ResolveSupplierName(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return "unknown";
        }

        if (sourceUrl.Contains("edenhorticulture.co.uk", StringComparison.OrdinalIgnoreCase))
        {
            return "edenhorticulture";
        }

        if (sourceUrl.Contains("safelincs.co.uk", StringComparison.OrdinalIgnoreCase))
        {
            return "safelincs";
        }

        return "unknown";
    }

    [HttpPost("bulk-publish")]
    public async Task<ActionResult<BulkJobStartedDto>> BulkPublish([FromBody] List<int> ids, [FromServices] Server.Services.Jobs.BulkJobService jobs)
    {
        if (ids == null || !ids.Any()) return BadRequest("No IDs provided");

        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        // Runs on Hangfire (see BackgroundJobs.BulkPublishAsync) as this user.
        var jobId = await jobs.EnqueueBulkPublishAsync(userId.Value, ids.Distinct().ToList());
        return Ok(new BulkJobStartedDto { JobId = jobId });
    }


    private static ListingDto ToListingDto(Listing listing, string? ebayAccountName, string? ownerEmail)
    {
        return new ListingDto
        {
            Id = listing.Id,
            EbayAccountId = listing.EbayAccountId,
            SupplierProductId = listing.SupplierProductId,
            EbayAccountName = ebayAccountName,
            OwnerUserId = listing.OwnerUserId,
            OwnerEmail = ownerEmail,
            Title = listing.Title,
            Description = listing.Description,
            Price = listing.Price,
            Quantity = listing.Quantity,
            Status = listing.Status,
            EbayItemId = listing.EbayItemId,
            CategoryId = listing.CategoryId,
            CategoryName = listing.CategoryName,
            FulfillmentPolicyId = listing.FulfillmentPolicyId,
            PaymentPolicyId = listing.PaymentPolicyId,
            ReturnPolicyId = listing.ReturnPolicyId,
            Type = listing.Type,
            Brand = listing.Brand,
            Colour = listing.Colour,
            Sku = listing.Sku,
            EbayOfferId = listing.EbayOfferId,
            ImageUrls = string.IsNullOrEmpty(listing.ImageUrlsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(listing.ImageUrlsJson, (JsonSerializerOptions?)null) ?? new List<string>(),
            SourceUrl = listing.SourceUrl,
            SourcePrice = listing.SourcePrice,
            SourceStockLevel = listing.SourceStockLevel,
            SourceProductCode = listing.SourceProductCode
        }.WithExtras(listing);
    }
}
