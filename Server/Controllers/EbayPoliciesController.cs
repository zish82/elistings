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
    public async Task<ActionResult<List<EbayPolicyDto>>> GetPaymentPolicies([FromQuery] int? accountId = null)
    {
        return Ok(await _ebayService.GetPaymentPoliciesAsync(accountId));
    }

    [HttpGet("fulfillment")]
    public async Task<ActionResult<List<EbayPolicyDto>>> GetFulfillmentPolicies([FromQuery] int? accountId = null)
    {
        return Ok(await _ebayService.GetFulfillmentPoliciesAsync(accountId));
    }

    [HttpGet("return")]
    public async Task<ActionResult<List<EbayPolicyDto>>> GetReturnPolicies([FromQuery] int? accountId = null)
    {
        return Ok(await _ebayService.GetReturnPoliciesAsync(accountId));
    }

    [HttpGet("category-suggestions")]
    public async Task<ActionResult<List<CategorySuggestionDto>>> GetCategorySuggestions([FromQuery] string title, [FromQuery] int? accountId = null)
    {
        return Ok(await _ebayService.GetCategorySuggestionsAsync(title, accountId));
    }
}
