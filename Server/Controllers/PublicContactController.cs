using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Server.Services;

namespace Server.Controllers;

/// <summary>Digital Club website contact form. Public (no login), rate limited per IP, emails the enquiry.</summary>
[ApiController]
[Route("api/public/contact")]
[AllowAnonymous]
[EnableCors(ContactPolicies.Cors)]
public class PublicContactController : ControllerBase
{
    private readonly ContactService _contact;
    private readonly ILogger<PublicContactController> _logger;

    public PublicContactController(ContactService contact, ILogger<PublicContactController> logger)
    {
        _contact = contact;
        _logger = logger;
    }

    [HttpPost]
    [EnableRateLimiting(ContactPolicies.RateLimit)]
    [RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> Post([FromBody] ContactRequest request, CancellationToken cancellationToken)
    {
        if (!request.Consent)
        {
            ModelState.AddModelError(nameof(ContactRequest.Consent), "Please tick the consent box.");
            return BadRequest(ModelState);
        }

        try
        {
            var result = await _contact.SendAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), cancellationToken);
            return result == ContactResult.NotConfigured
                ? StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "The contact form isn't available right now." })
                : Ok(new { message = "Thank you. Your message has been sent." }); // spam gets the same answer
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Contact form: sending the email failed");
            return StatusCode(StatusCodes.Status502BadGateway, new { message = "Your message couldn't be sent. Please try again later." });
        }
    }
}

public static class ContactPolicies
{
    public const string Cors = "ContactCors";
    public const string RateLimit = "contact";
}
