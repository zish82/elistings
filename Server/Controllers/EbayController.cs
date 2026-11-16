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

    /// <summary>Listings this user may see: Admin everyone's; Manager own + assigned users'; others own. Null when signed out.</summary>
    private async Task<IQueryable<Listing>?> AccessibleListings()
    {
        if (_currentUserService.UserId == null) return null;
        return _context.Listings.VisibleTo(await UserScope.VisibleUserIdsAsync(_context, _currentUserService));
    }

    /// <summary>Live status of a publish that is running (picture by picture). 204 when there is none.</summary>
    [HttpGet("publish/{id}/progress")]
    public async Task<ActionResult<PublishProgressDto>> GetPublishProgress(int id, [FromServices] PublishProgressStore progressStore)
    {
        var query = await AccessibleListings();
        if (query == null) return Unauthorized();
        if (!await query.AnyAsync(l => l.Id == id)) return NotFound();

        var progress = progressStore.Get(id);
        return progress == null ? NoContent() : Ok(progress);
    }

    /// <summary>
    /// Publishes (or re-syncs) a listing. Body (optional): { "accountIds": [..] } – the owner's accounts to publish to.
    /// Without it: every account the listing is already on, else its own account.
    /// </summary>
    [HttpPost("publish/{id}")]
    public async Task<ActionResult<ListingDto>> PublishListing(int id, [FromServices] SkuService skuService, [FromServices] PublishProgressStore progressStore,
        [FromBody] PublishRequest? request = null)
    {
        try
        {
            var query = await AccessibleListings();
            if (query == null) return Unauthorized();

            var listing = await query.FirstOrDefaultAsync(l => l.Id == id);
            if (listing == null) return NotFound();
            progressStore.Start(id, "Checking the listing");

            ListingPublishOutcome outcome;
            try
            {
                var publisher = new ListingPublisher(_context, _ebayService, _ebaySettings.Value, skuService, _currentUserService);
                outcome = await publisher.PublishAsync(listing, request?.AccountIds);
            }
            catch (InvalidOperationException validationError)
            {
                return BadRequest(validationError.Message);
            }
            progressStore.SetStage(id, "Published");
            var publications = await PublicationQueries.ForListingsAsync(_context, new[] { listing.Id }, _ebaySettings.Value.IsSandbox);

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
                PublishWarnings = outcome.Warnings(),
                Publications = publications.GetValueOrDefault(listing.Id) ?? new List<ListingPublicationDto>()
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
