using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Server.Data;
using Server.Services;
using Shared;

namespace Server.Controllers;

/// <summary>Accounts overview (who posts with which account, and how much) and sharing accounts between users.</summary>
[ApiController]
[Route("api/marketplace-accounts")]
[Authorize]
public class MarketplaceAccountsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public MarketplaceAccountsController(AppDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    /// <summary>Admin: every user; Manager: themselves and their users; others: themselves.</summary>
    [HttpGet("overview")]
    public async Task<ActionResult<List<UserAccountsOverviewDto>>> Overview()
    {
        if (_currentUserService.UserId == null) return Unauthorized();
        return Ok(await AccountsOverviewService.BuildAsync(_context, _currentUserService));
    }

    /// <summary>Admin only: the users (besides the one who connected it) who may post with this account.</summary>
    [HttpPut("{accountId:int}/users")]
    [Authorize(Roles = AuthRoles.Admin)]
    public async Task<IActionResult> SetUsers(int accountId, [FromBody] List<int> userIds)
    {
        try
        {
            await AccountAccess.SetSharedUsersAsync(_context, accountId, userIds ?? new List<int>());
            return NoContent();
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }
}
