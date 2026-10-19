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

    [HttpDelete("category-rates/{id:int}")]
    public async Task<IActionResult> DeleteCategoryRate(int id)
    {
        if (!CanEdit) return Forbidden();
        return await _fees.DeleteCategoryRateAsync(id) ? NoContent() : NotFound();
    }
}
