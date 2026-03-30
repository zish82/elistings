using Microsoft.AspNetCore.Mvc;
using Server.Services;
using Shared;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EbayPoliciesController : ControllerBase
{
    private readonly IEbayService _ebayService;

    public EbayPoliciesController(IEbayService ebayService)
    {
        _ebayService = ebayService;
    }

    [HttpGet("payment")]
    public async Task<ActionResult<List<EbayPolicyDto>>> GetPaymentPolicies()
    {
        return Ok(await _ebayService.GetPaymentPoliciesAsync());
    }

    [HttpGet("fulfillment")]
    public async Task<ActionResult<List<EbayPolicyDto>>> GetFulfillmentPolicies()
    {
        return Ok(await _ebayService.GetFulfillmentPoliciesAsync());
    }

    [HttpGet("return")]
    public async Task<ActionResult<List<EbayPolicyDto>>> GetReturnPolicies()
    {
        return Ok(await _ebayService.GetReturnPoliciesAsync());
    }

    [HttpGet("category-suggestions")]
    public async Task<ActionResult<List<CategorySuggestionDto>>> GetCategorySuggestions([FromQuery] string title)
    {
        return Ok(await _ebayService.GetCategorySuggestionsAsync(title));
    }
}
