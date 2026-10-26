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

    /// <summary>Listings this user may publish / see progress for: their own; Managers/Admins everyone's.</summary>
    private IQueryable<Listing>? AccessibleListings()
    {
        var query = _context.Listings.AsQueryable();
        if (_currentUserService.CanViewAllListings) return query;
        var userId = _currentUserService.UserId;
        return userId == null ? null : query.Where(l => l.OwnerUserId == userId.Value);
    }

    /// <summary>Live status of a publish that is running (picture by picture). 204 when there is none.</summary>
    [HttpGet("publish/{id}/progress")]
    public async Task<ActionResult<PublishProgressDto>> GetPublishProgress(int id, [FromServices] PublishProgressStore progressStore)
    {
        var query = AccessibleListings();
        if (query == null) return Unauthorized();
        if (!await query.AnyAsync(l => l.Id == id)) return NotFound();

        var progress = progressStore.Get(id);
        return progress == null ? NoContent() : Ok(progress);
    }

    [HttpPost("publish/{id}")]
    public async Task<ActionResult<ListingDto>> PublishListing(int id, [FromServices] SkuService skuService, [FromServices] PublishProgressStore progressStore)
    {
        try
        {
            var query = AccessibleListings();
            if (query == null) return Unauthorized();

            var listing = await query.FirstOrDefaultAsync(l => l.Id == id);
            if (listing == null) return NotFound();
            progressStore.Start(id, "Checking the listing");

            ListingDto dto;
            try
            {
                dto = await ListingPublishHelper.PrepareAsync(listing, _context, _ebayService, _ebaySettings.Value, skuService, _currentUserService);
            }
            catch (InvalidOperationException validationError)
            {
                return BadRequest(validationError.Message);
            }

            // Persist an auto-selected category / defaulted policies even if eBay rejects the listing.
            await _context.SaveChangesAsync();

            var result = await _ebayService.CreateListingAsync(dto);
            ListingPublishHelper.ApplyPublishResult(listing, result);
            await _context.SaveChangesAsync();
            progressStore.SetStage(id, "Published");

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
                EbayOfferId = listing.EbayOfferId,
                PublishWarnings = result.ImageWarnings
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
