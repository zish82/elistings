using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Server.Services;
using Shared;

namespace Server.Controllers;

/// <summary>eBay fee settings used for fee/profit estimates (edited on /suppliers).</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class MarketplaceFeesController : ControllerBase
{
    private readonly MarketplaceFeeService _fees;
    private readonly ICurrentUserService _currentUserService;

    public MarketplaceFeesController(MarketplaceFeeService fees, ICurrentUserService currentUserService)
    {
        _fees = fees;
        _currentUserService = currentUserService;
    }

    private bool CanEdit => _currentUserService.CanManageUsers || _currentUserService.CanViewAllListings;
    private ObjectResult Forbidden() => StatusCode(StatusCodes.Status403Forbidden, SuppliersController.OnlyAdminsMessage);

    [HttpGet]
    public async Task<ActionResult<MarketplaceFeeSettingsDto>> Get([FromQuery] string marketplace = MarketplaceFeeService.Ebay)
        => Ok(await _fees.GetSettingsAsync(marketplace));

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] MarketplaceFeeSettingsDto dto)
    {
        if (!CanEdit) return Forbidden();
        var error = await _fees.SaveSettingsAsync(dto);
        return error == null ? NoContent() : BadRequest(error);
    }

    [HttpPost("category-rates")]
    public async Task<IActionResult> SaveCategoryRate([FromBody] MarketplaceFeeCategoryRateDto dto, [FromQuery] string marketplace = MarketplaceFeeService.Ebay)
    {
        if (!CanEdit) return Forbidden();
        var error = await _fees.SaveCategoryRateAsync(marketplace, dto);
        return error == null ? NoContent() : BadRequest(error);
    }

    /// <summary>Loads eBay's UK business-seller rate card (updates those categories, keeps your other rates).</summary>
    [HttpPost("load-uk-rate-card")]
    public async Task<ActionResult<MessageDto>> LoadUkRateCard()
    {
        if (!CanEdit) return Forbidden();
        var (added, updated) = await _fees.LoadUkRateCardAsync();
        return Ok(new MessageDto { Message = $"eBay UK rates ({EbayUkRateCard.AsOf}) loaded: {added} added, {updated} updated." });
    }

    /// <summary>Downloads the eBay UK category tree, so sub-categories find their top-level fee rate.</summary>
    [HttpPost("ebay-categories/refresh")]
    public async Task<ActionResult<MessageDto>> RefreshEbayCategories([FromServices] EbayCategoryService categories, [FromServices] IEbayService ebay)
    {
        if (!CanEdit) return Forbidden();
        try
        {
            var count = await categories.RefreshAsync(ebay);
            return Ok(new MessageDto { Message = $"Downloaded {count:N0} eBay categories." });
        }
        catch (Exception ex) // e.g. no eBay account connected, eBay error, timeout
        {
            return BadRequest($"Could not download eBay categories: {ex.Message}");
        }
    }

    /// <summary>An eBay category and its parents (most specific first), for the listing editor's estimate.</summary>
    [HttpGet("ebay-categories/{categoryId}/chain")]
    public async Task<ActionResult<List<string>>> GetChain(string categoryId, [FromServices] EbayCategoryService categories)
        => Ok((await categories.GetChainAsync(categoryId)).ToList());

    [HttpDelete("category-rates/{id:int}")]
    public async Task<IActionResult> DeleteCategoryRate(int id)
    {
        if (!CanEdit) return Forbidden();
        return await _fees.DeleteCategoryRateAsync(id) ? NoContent() : NotFound();
    }
}
