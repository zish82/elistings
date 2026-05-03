using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Server.Data;
using Server.Services;
using Shared;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private sealed class PendingOAuthState
    {
        public int UserId { get; set; }
        public string? AccountName { get; set; }
        public string ReturnUrl { get; set; } = "/";
        public DateTime ExpiresAtUtc { get; set; }
    }

    private static readonly ConcurrentDictionary<string, PendingOAuthState> PendingStates = new();

    private readonly EbaySettings _settings;
    private readonly AppDbContext _context;
    private readonly HttpClient _httpClient;
    private readonly ICurrentUserService _currentUserService;

    public AuthController(IOptions<EbaySettings> settings, AppDbContext context, HttpClient httpClient, ICurrentUserService currentUserService)
    {
        _settings = settings.Value;
        _context = context;
        _httpClient = httpClient;
        _currentUserService = currentUserService;
    }

    [Authorize]
    [HttpGet("login-url")]
    public ActionResult<LoginUrlResponse> GetLoginUrl([FromQuery] string? returnUrl = "/", [FromQuery] string? accountName = null)
    {
        var userId = _currentUserService.UserId;
        if (userId == null)
        {
            return Unauthorized();
        }

        // For eBay OAuth 2.0, the redirect_uri parameter MUST be the RuName string itself
        var baseUrl = _settings.IsSandbox ? "https://auth.sandbox.ebay.com/oauth2/authorize" : "https://auth.ebay.com/oauth2/authorize";
        var scope = "https://api.ebay.com/oauth/api_scope https://api.ebay.com/oauth/api_scope/sell.inventory https://api.ebay.com/oauth/api_scope/sell.account";
        var state = Guid.NewGuid().ToString("N");
        var expiry = DateTime.UtcNow.AddMinutes(15);

        var safeReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        if (!Uri.IsWellFormedUriString(safeReturnUrl, UriKind.Relative))
        {
            safeReturnUrl = "/";
        }

        // Keep pending OAuth state server-side so callback validation also works
        // when the callback domain differs (e.g., localhost + ngrok during local testing).
        PendingStates[state] = new PendingOAuthState
        {
            UserId = userId.Value,
            AccountName = NormalizeAccountName(accountName),
            ReturnUrl = safeReturnUrl,
            ExpiresAtUtc = expiry
        };

        // Opportunistic cleanup of old states.
        foreach (var kv in PendingStates)
        {
            if (kv.Value.ExpiresAtUtc <= DateTime.UtcNow)
            {
                PendingStates.TryRemove(kv.Key, out _);
            }
        }

        Response.Cookies.Append("ebay_oauth_state", state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = expiry
        });

        Response.Cookies.Append("ebay_oauth_return", safeReturnUrl, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = expiry
        });
        
        var queryParams = new List<string>
        {
            $"client_id={System.Web.HttpUtility.UrlEncode(_settings.AppId)}",
            $"redirect_uri={System.Web.HttpUtility.UrlEncode(_settings.RuName)}",
            "response_type=code",
            $"scope={System.Web.HttpUtility.UrlEncode(scope)}",
            $"state={System.Web.HttpUtility.UrlEncode(state)}",
            "prompt=login"
        };

        var url = $"{baseUrl}?{string.Join("&", queryParams)}";
        
        Console.WriteLine($"Generated eBay Login URL: {url}");
        
        return Ok(new LoginUrlResponse { LoginUrl = url });
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, [FromQuery(Name = "error_description")] string? errorDescription)
    {
        var cookieReturnUrl = Request.Cookies["ebay_oauth_return"];
        var cookieState = Request.Cookies["ebay_oauth_state"];

        string returnUrl = "/";
        PendingOAuthState? pending = null;

        if (!string.IsNullOrWhiteSpace(state) && PendingStates.TryRemove(state, out var pendingState))
        {
            pending = pendingState;
            if (pending.ExpiresAtUtc <= DateTime.UtcNow)
            {
                pending = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(cookieReturnUrl) && Uri.IsWellFormedUriString(cookieReturnUrl, UriKind.Relative))
        {
            returnUrl = cookieReturnUrl;
        }
        else if (pending != null && Uri.IsWellFormedUriString(pending.ReturnUrl, UriKind.Relative))
        {
            returnUrl = pending.ReturnUrl;
        }

        // Always remove short-lived oauth cookies once callback is hit.
        Response.Cookies.Delete("ebay_oauth_state");
        Response.Cookies.Delete("ebay_oauth_return");

        string BuildRedirect(string status, string? message = null)
        {
            var sep = returnUrl.Contains("?") ? "&" : "?";
            var url = $"{returnUrl}{sep}ebay={System.Web.HttpUtility.UrlEncode(status)}";
            if (!string.IsNullOrWhiteSpace(message))
            {
                url += $"&message={System.Web.HttpUtility.UrlEncode(message)}";
            }
            return url;
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            return Redirect(BuildRedirect("error", string.IsNullOrWhiteSpace(errorDescription) ? error : errorDescription));
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return Redirect(BuildRedirect("error", "No OAuth authorization code provided by eBay."));
        }

        if (string.IsNullOrWhiteSpace(state) || pending == null)
        {
            return Redirect(BuildRedirect("error", "OAuth state missing or invalid."));
        }

        if (pending.UserId <= 0)
        {
            return Redirect(BuildRedirect("error", "No valid local user context for OAuth callback."));
        }

        // If a state cookie is available, require it to match too (extra defense).
        if (!string.IsNullOrWhiteSpace(cookieState))
        {
            var expectedStateBytes = Encoding.UTF8.GetBytes(cookieState);
            var stateBytes = Encoding.UTF8.GetBytes(state);
            if (!CryptographicOperations.FixedTimeEquals(expectedStateBytes, stateBytes))
            {
                return Redirect(BuildRedirect("error", "OAuth state validation failed."));
            }
        }

        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";
        var authHeader = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_settings.AppId}:{_settings.CertId}"));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/identity/v1/oauth2/token");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "authorization_code"),
            new KeyValuePair<string, string>("code", code),
            new KeyValuePair<string, string>("redirect_uri", _settings.RuName)
        });
        request.Content = content;

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var exchangeError = await response.Content.ReadAsStringAsync();
            return Redirect(BuildRedirect("error", $"eBay token exchange failed: {exchangeError}"));
        }

        var result = await response.Content.ReadFromJsonAsync<EbayOAuthResponse>();
        if (result == null) return Redirect(BuildRedirect("error", "Failed to parse eBay token response."));

        // Save to DB
        var accountName = string.IsNullOrWhiteSpace(pending.AccountName)
            ? $"eBay Account {(await _context.EbayTokens.CountAsync(t => t.UserId == pending.UserId)) + 1}"
            : pending.AccountName!;
        var existingToken = await _context.EbayTokens.FirstOrDefaultAsync(t => t.UserId == pending.UserId && t.Name == accountName);
        if (existingToken == null)
        {
            existingToken = new EbayTokenInfo 
            { 
                UserId = pending.UserId,
                Name = accountName,
                IsDefault = !await _context.EbayTokens.AnyAsync(t => t.UserId == pending.UserId && t.IsDefault)
            };
            _context.EbayTokens.Add(existingToken);
        }

        existingToken.AccessToken = result.access_token;
        existingToken.RefreshToken = result.refresh_token;
        existingToken.ExpiryTime = DateTime.UtcNow.AddSeconds(result.expires_in);
        existingToken.RefreshTokenExpiryTime = DateTime.UtcNow.AddSeconds(result.refresh_token_expires_in);

        await _context.SaveChangesAsync();

        // Redirect back to frontend dashboard with status
        return Redirect(BuildRedirect("connected"));
    }

    [Authorize]
    [HttpGet("status")]
    public async Task<ActionResult<AuthStatusResponse>> GetStatus()
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        var token = await _context.EbayTokens.Where(t => t.UserId == userId.Value).OrderByDescending(t => t.IsDefault).ThenBy(t => t.Id).FirstOrDefaultAsync();
        if (token == null)
        {
            return Ok(new AuthStatusResponse { Connected = false });
        }

        var isConnected = token.ExpiryTime > DateTime.UtcNow;
        return Ok(new AuthStatusResponse
        {
            Connected = isConnected,
            ExpiresAtUtc = token.ExpiryTime
        });
    }

    [Authorize]
    [HttpPost("manual-token")]
    public async Task<IActionResult> SetManualToken([FromBody] ManualTokenRequest request)
    {
        try
        {
            var userId = _currentUserService.UserId;
            if (userId == null) return Unauthorized();

            if (string.IsNullOrEmpty(request.Token)) return BadRequest("Token is required");

            var accountName = NormalizeAccountName(request.AccountName);
            var existingToken = !string.IsNullOrWhiteSpace(accountName)
                ? await _context.EbayTokens.FirstOrDefaultAsync(t => t.UserId == userId.Value && t.Name == accountName)
                : null;
            if (existingToken == null)
            {
                existingToken = new EbayTokenInfo 
                { 
                    UserId = userId.Value,
                    Name = string.IsNullOrWhiteSpace(accountName) ? $"eBay Account {(await _context.EbayTokens.CountAsync(t => t.UserId == userId.Value)) + 1}" : accountName!,
                    IsDefault = !await _context.EbayTokens.AnyAsync(t => t.UserId == userId.Value && t.IsDefault)
                };
                _context.EbayTokens.Add(existingToken);
            }

            existingToken.AccessToken = request.Token;
            existingToken.RefreshToken = "MANUAL";
            existingToken.ExpiryTime = DateTime.UtcNow.AddHours(2); // Manually generated tokens usually last 2 hours or more
            existingToken.RefreshTokenExpiryTime = DateTime.UtcNow.AddYears(1);

            await _context.SaveChangesAsync();
            return Ok();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error saving manual token: {ex}");
            return StatusCode(500, $"Internal Error: {ex.Message}");
        }
    }

    [Authorize]
    [HttpGet("accounts")]
    public async Task<ActionResult<List<EbayAccountDto>>> GetAccounts()
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        var accounts = await _context.EbayTokens
            .Where(t => t.UserId == userId.Value)
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Name)
            .Select(t => new EbayAccountDto
            {
                Id = t.Id,
                Name = t.Name,
                IsDefault = t.IsDefault,
                IsConnected = t.ExpiryTime > DateTime.UtcNow,
                ExpiresAtUtc = t.ExpiryTime
            })
            .ToListAsync();

        return Ok(accounts);
    }

    [Authorize]
    [HttpPost("accounts/{id:int}/default")]
    public async Task<IActionResult> SetDefaultAccount(int id)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();

        var account = await _context.EbayTokens.FirstOrDefaultAsync(t => t.UserId == userId.Value && t.Id == id);
        if (account == null) return NotFound();

        var accounts = await _context.EbayTokens.Where(t => t.UserId == userId.Value).ToListAsync();
        foreach (var item in accounts)
        {
            item.IsDefault = item.Id == id;
        }

        await _context.SaveChangesAsync();
        return NoContent();
    }

    private static string? NormalizeAccountName(string? accountName)
    {
        return string.IsNullOrWhiteSpace(accountName) ? null : accountName.Trim();
    }
}

public class ManualTokenRequest
{
    public string Token { get; set; } = string.Empty;
    public string? AccountName { get; set; }
}

public class EbayOAuthResponse
{
    public string access_token { get; set; } = string.Empty;
    public int expires_in { get; set; }
    public string refresh_token { get; set; } = string.Empty;
    public int refresh_token_expires_in { get; set; }
}

public class LoginUrlResponse
{
    public string LoginUrl { get; set; } = string.Empty;
}

public class AuthStatusResponse
{
    public bool Connected { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}
