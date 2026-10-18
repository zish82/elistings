using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Server.Services;
using Shared;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class EbayPoliciesController : ControllerBase
{
    private readonly IEbayService _ebayService;

    public EbayPoliciesController(IEbayService ebayService)
    {
        _ebayService = ebayService;
    }

    [HttpGet("payment")]
    public Task<ActionResult<List<EbayPolicyDto>>> GetPaymentPolicies([FromQuery] int? accountId = null)
        => PoliciesOrError(() => _ebayService.GetPaymentPoliciesAsync(accountId));

    [HttpGet("fulfillment")]
    public Task<ActionResult<List<EbayPolicyDto>>> GetFulfillmentPolicies([FromQuery] int? accountId = null)
        => PoliciesOrError(() => _ebayService.GetFulfillmentPoliciesAsync(accountId));

    [HttpGet("return")]
    public Task<ActionResult<List<EbayPolicyDto>>> GetReturnPolicies([FromQuery] int? accountId = null)
        => PoliciesOrError(() => _ebayService.GetReturnPoliciesAsync(accountId));

    /// <summary>Returns eBay's error text (502) instead of an empty list, so the UI can explain the problem.</summary>
    private async Task<ActionResult<List<EbayPolicyDto>>> PoliciesOrError(Func<Task<List<EbayPolicyDto>>> load)
    {
        try
        {
            return Ok(await load());
        }
        catch (EbayApiException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    /// <summary>Item specifics eBay requires/recommends for a category, with allowed values.</summary>
    [HttpGet("category-aspects")]
    public async Task<ActionResult<List<CategoryAspectDto>>> GetCategoryAspects([FromQuery] string categoryId, [FromQuery] int? accountId = null)
    {
        try
        {
            return Ok(await _ebayService.GetCategoryAspectsAsync(categoryId, accountId));
        }
        catch (EbayApiException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, ex.Message);
        }
    }

    [HttpGet("category-suggestions")]
    public async Task<ActionResult<List<CategorySuggestionDto>>> GetCategorySuggestions([FromQuery] string title, [FromQuery] int? accountId = null)
    {
        return Ok(await _ebayService.GetCategorySuggestionsAsync(title, accountId));
    }
}
