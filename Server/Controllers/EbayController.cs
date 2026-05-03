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
public class EbayController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly IEbayService _ebayService;
    private readonly Microsoft.Extensions.Options.IOptions<Server.Configuration.EbaySettings> _ebaySettings;
    private readonly ICurrentUserService _currentUserService;

    public EbayController(AppDbContext context, IEbayService ebayService, Microsoft.Extensions.Options.IOptions<Server.Configuration.EbaySettings> ebaySettings, ICurrentUserService currentUserService)
    {
        _context = context;
        _ebayService = ebayService;
        _ebaySettings = ebaySettings;
        _currentUserService = currentUserService;
    }

    [HttpPost("publish/{id}")]
    public async Task<ActionResult<ListingDto>> PublishListing(int id)
    {
        try
        {
            var query = _context.Listings.Where(l => l.Id == id);
            if (!_currentUserService.CanViewAllListings)
            {
                var userId = _currentUserService.UserId;
                if (userId == null) return Unauthorized();
                query = query.Where(l => l.OwnerUserId == userId.Value);
            }

            var listing = await query.FirstOrDefaultAsync();
            if (listing == null) return NotFound();

            var dto = new ListingDto
            {
                Id = listing.Id,
                EbayAccountId = listing.EbayAccountId,
                Title = listing.Title,
                Description = listing.Description,
                Price = listing.Price,
                CategoryId = listing.CategoryId,
                FulfillmentPolicyId = listing.FulfillmentPolicyId,
                PaymentPolicyId = listing.PaymentPolicyId,
                ReturnPolicyId = listing.ReturnPolicyId,
                Type = listing.Type,
                Brand = listing.Brand,
                Colour = listing.Colour,
                Sku = listing.Sku,
                EbayOfferId = listing.EbayOfferId,
                ImageUrls = string.IsNullOrEmpty(listing.ImageUrlsJson) ? new List<string>() : JsonSerializer.Deserialize<List<string>>(listing.ImageUrlsJson) ?? new List<string>()
            };

            var result = await _ebayService.CreateListingAsync(dto);
            
            listing.Status = "Published";
            listing.EbayItemId = result.ListingId;
            listing.Sku = result.Sku;
            listing.EbayOfferId = result.OfferId;
            
            await _context.SaveChangesAsync();

            return Ok(new ListingDto
            {
                Id = listing.Id,
                EbayAccountId = listing.EbayAccountId,
                Title = listing.Title,
                Description = listing.Description,
                Price = listing.Price,
                Status = listing.Status,
                EbayItemId = listing.EbayItemId,
                Sku = listing.Sku,
                EbayOfferId = listing.EbayOfferId
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error publishing listing {id}: {ex}");
            return StatusCode(500, $"Publication Error: {ex.Message}");
        }
    }

    [HttpGet("default-policies")]
    public ActionResult<object> GetDefaultPolicies()
    {
        // Prefer DB-configured defaults if present
        try
        {
            var payment = _context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == "payment" && p.IsDefault);
            var fulfillment = _context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == "fulfillment" && p.IsDefault);
            var ret = _context.Policies.FirstOrDefault(p => p.Marketplace.ToLower() == "ebay" && p.PolicyType == "return" && p.IsDefault);

            return Ok(new {
                PaymentPolicyId = payment?.PolicyKey ?? _ebaySettings.Value.DefaultPaymentPolicyId,
                FulfillmentPolicyId = fulfillment?.PolicyKey ?? _ebaySettings.Value.DefaultFulfillmentPolicyId,
                ReturnPolicyId = ret?.PolicyKey ?? _ebaySettings.Value.DefaultReturnPolicyId
            });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error reading default policies from DB: {ex.Message}");
            return Ok(new {
                PaymentPolicyId = _ebaySettings.Value.DefaultPaymentPolicyId,
                FulfillmentPolicyId = _ebaySettings.Value.DefaultFulfillmentPolicyId,
                ReturnPolicyId = _ebaySettings.Value.DefaultReturnPolicyId
            });
        }
    }

    [HttpPost("fee-estimate")]
    public async Task<ActionResult<object>> GetFeeEstimate([FromBody] ListingDto listing)
    {
        try
        {
            var node = await _ebayService.GetFeeEstimateAsync(listing);
            if (node == null) return StatusCode(502, "Failed to fetch fee estimate from eBay");

            // If the service returned an error node (contains statusCode), forward it as 502 for clarity
            if (node is System.Text.Json.Nodes.JsonObject obj && obj.ContainsKey("statusCode"))
            {
                return StatusCode(502, obj);
            }

            return Ok(node);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fee estimate error: {ex}");
            return StatusCode(500, ex.Message);
        }
    }
}
