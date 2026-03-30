using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Services;
using Shared;
using System.Text.Json;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ListingsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly Services.ScraperService _scraperService;
    private readonly IEbayService _ebayService;
    private readonly Microsoft.Extensions.Options.IOptions<Server.Configuration.EbaySettings> _ebaySettings;

    public ListingsController(AppDbContext context, Services.ScraperService scraperService, IEbayService ebayService, Microsoft.Extensions.Options.IOptions<Server.Configuration.EbaySettings> ebaySettings)
    {
        _context = context;
        _scraperService = scraperService;
        _ebayService = ebayService;
        _ebaySettings = ebaySettings;
    }

    [HttpGet("extract")]
    public async Task<ActionResult<ExtractedDetailsDto>> ExtractFromUrl([FromQuery] string url)
    {
        if (string.IsNullOrEmpty(url)) return BadRequest("URL is required");
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
    public async Task<ActionResult<List<ListingDto>>> GetListings()
    {
        var listings = await _context.Listings
            .Select(l => new ListingDto
            {
                Id = l.Id,
                Title = l.Title,
                Description = l.Description,
                Price = l.Price,
                Status = l.Status,
                EbayItemId = l.EbayItemId,
                CategoryId = l.CategoryId,
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
                SourceProductCode = l.SourceProductCode
            })
            .ToListAsync();
            
        return Ok(listings);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ListingDto>> GetListing(int id)
    {
        var l = await _context.Listings.FindAsync(id);
        if (l == null) return NotFound();

        return Ok(new ListingDto
        {
            Id = l.Id,
            Title = l.Title,
            Description = l.Description,
            Price = l.Price,
            Status = l.Status,
            EbayItemId = l.EbayItemId,
            CategoryId = l.CategoryId,
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
            SourceProductCode = l.SourceProductCode
        });
    }

    [HttpPost]
    public async Task<ActionResult<ListingDto>> CreateListing(CreateListingRequest request)
    {
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

        var paymentId = GetDefault(request.PaymentPolicyId, "payment");
        var fulfillmentId = GetDefault(request.FulfillmentPolicyId, "fulfillment");
        var returnId = GetDefault(request.ReturnPolicyId, "return");

        var listing = new Listing
        {
            Title = request.Title,
            Description = request.Description,
            Price = request.Price,
            Status = "Draft",
            CategoryId = request.CategoryId,
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
            SourceProductCode = request.SourceProductCode
        };
        
        _context.Listings.Add(listing);
        await _context.SaveChangesAsync();
        
        return CreatedAtAction(nameof(GetListing), new { id = listing.Id }, new ListingDto 
        { 
            Id = listing.Id, 
            Title = listing.Title, 
            Description = listing.Description, 
            Price = listing.Price,
            Status = listing.Status,
            CategoryId = listing.CategoryId,
            FulfillmentPolicyId = listing.FulfillmentPolicyId,
            PaymentPolicyId = listing.PaymentPolicyId,
            ReturnPolicyId = listing.ReturnPolicyId,
            Type = listing.Type,
            Brand = listing.Brand,
            Colour = listing.Colour
            ,
            SourceUrl = listing.SourceUrl,
            SourcePrice = listing.SourcePrice,
            SourceProductCode = listing.SourceProductCode
        });
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateListing(int id, CreateListingRequest request)
    {
        var listing = await _context.Listings.FindAsync(id);
        if (listing == null) return NotFound();

        listing.Title = request.Title;
        listing.Description = request.Description;
        listing.Price = request.Price;
        listing.CategoryId = request.CategoryId;
        listing.FulfillmentPolicyId = request.FulfillmentPolicyId;
        listing.PaymentPolicyId = request.PaymentPolicyId;
        listing.ReturnPolicyId = request.ReturnPolicyId;
        listing.Type = request.Type;
        listing.Brand = request.Brand;
        listing.Colour = request.Colour;
        listing.ImageUrlsJson = JsonSerializer.Serialize(request.ImageUrls);
        listing.SourceUrl = request.SourceUrl;
        listing.SourcePrice = request.SourcePrice;
        listing.SourceProductCode = request.SourceProductCode;

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("bulk-extract")]
    public async Task<ActionResult> BulkExtract([FromBody] string categoryUrl)
    {
        if (string.IsNullOrEmpty(categoryUrl)) return BadRequest("Category URL is required");

        try
        {
            var productLinks = await _scraperService.ExtractProductLinksAsync(categoryUrl);
            var createdCount = 0;

            foreach (var link in productLinks)
            {
                try
                {
                    // Check if already exists by title or external URL (if we stored it)
                    // For now, just add them all.
                    var details = await _scraperService.ExtractDetailsAsync(link);
                    
                    var listing = new Listing
                    {
                        Title = details.Title,
                        Description = details.Description,
                        Price = details.PriceIncVat ?? details.Price ?? 0.99m,
                        Status = "Draft",
                        Brand = details.Brand,
                        ImageUrlsJson = JsonSerializer.Serialize(details.ImageUrls ?? new List<string>()),
                        SourceUrl = link,
                        // Apply server-configured or DB default business policies when bulk-creating
                        PaymentPolicyId = _context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == "payment" && p.IsDefault)?.PolicyKey ?? _ebaySettings.Value.DefaultPaymentPolicyId,
                        FulfillmentPolicyId = _context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == "fulfillment" && p.IsDefault)?.PolicyKey ?? _ebaySettings.Value.DefaultFulfillmentPolicyId,
                        ReturnPolicyId = _context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == "return" && p.IsDefault)?.PolicyKey ?? _ebaySettings.Value.DefaultReturnPolicyId,
                        // Do not set SourcePrice on bulk-extract; SourcePrice is for manual entry.
                        SourceProductCode = details.ProductCode,
                    };

                    _context.Listings.Add(listing);
                    createdCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Bulk] Failed to extract {link}: {ex.Message}");
                }
            }

            await _context.SaveChangesAsync();
            return Ok(new { Count = createdCount });
        }
        catch (Exception ex)
        {
            return BadRequest($"Bulk operation failed: {ex.Message}");
        }
    }

    [HttpPost("bulk-delete")]
    public async Task<ActionResult> BulkDelete([FromBody] List<int> ids)
    {
        if (ids == null || !ids.Any()) return BadRequest("No IDs provided");

        try
        {
            var toProcess = await _context.Listings.Where(l => ids.Contains(l.Id)).ToListAsync();
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

    [HttpPost("bulk-publish")]
    public async Task<ActionResult> BulkPublish([FromBody] List<int> ids)
    {
        if (ids == null || !ids.Any()) return BadRequest("No IDs provided");

        var published = 0;
        var failed = 0;
        var errors = new List<string>();

        foreach (var id in ids)
        {
            try
            {
                var listingEntity = await _context.Listings.FindAsync(id);
                if (listingEntity == null)
                {
                    failed++;
                    errors.Add($"Listing {id} not found");
                    continue;
                }

                // Ensure policy IDs are set, falling back to configured defaults
                if (string.IsNullOrEmpty(listingEntity.PaymentPolicyId))
                    listingEntity.PaymentPolicyId = _ebaySettings.Value.DefaultPaymentPolicyId;
                if (string.IsNullOrEmpty(listingEntity.FulfillmentPolicyId))
                    listingEntity.FulfillmentPolicyId = _ebaySettings.Value.DefaultFulfillmentPolicyId;
                if (string.IsNullOrEmpty(listingEntity.ReturnPolicyId))
                    listingEntity.ReturnPolicyId = _ebaySettings.Value.DefaultReturnPolicyId;

                var dto = new ListingDto
                {
                    Id = listingEntity.Id,
                    Title = listingEntity.Title,
                    Description = listingEntity.Description,
                    Price = listingEntity.Price,
                    CategoryId = listingEntity.CategoryId,
                    FulfillmentPolicyId = listingEntity.FulfillmentPolicyId,
                    PaymentPolicyId = listingEntity.PaymentPolicyId,
                    ReturnPolicyId = listingEntity.ReturnPolicyId,
                    Type = listingEntity.Type,
                    Brand = listingEntity.Brand,
                    Colour = listingEntity.Colour,
                    Sku = listingEntity.Sku,
                    EbayOfferId = listingEntity.EbayOfferId,
                    ImageUrls = string.IsNullOrEmpty(listingEntity.ImageUrlsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(listingEntity.ImageUrlsJson) ?? new List<string>(),
                    SourceUrl = listingEntity.SourceUrl
                };

                // Use the ebay service to create/update and publish the listing
                var result = await _ebayService.CreateListingAsync(dto);

                listingEntity.Status = "Published";
                listingEntity.EbayItemId = result.ListingId;
                listingEntity.Sku = result.Sku;
                listingEntity.EbayOfferId = result.OfferId;

                await _context.SaveChangesAsync();
                published++;
            }
            catch (Exception ex)
            {
                failed++;
                errors.Add($"{id}: {ex.Message}");
                Console.WriteLine($"Bulk publish failed for {id}: {ex}");
            }
        }

        return Ok(new { Published = published, Failed = failed, Errors = errors });
    }
}
