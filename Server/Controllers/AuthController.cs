using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Server.Data;
using Microsoft.EntityFrameworkCore;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly EbaySettings _settings;
    private readonly AppDbContext _context;
    private readonly HttpClient _httpClient;

    public AuthController(IOptions<EbaySettings> settings, AppDbContext context, HttpClient httpClient)
    {
        _settings = settings.Value;
        _context = context;
        _httpClient = httpClient;
    }

    [HttpGet("login-url")]
    public ActionResult<LoginUrlResponse> GetLoginUrl([FromQuery] string? returnUrl = "/")
    {
        // For eBay OAuth 2.0, the redirect_uri parameter MUST be the RuName string itself
        var baseUrl = _settings.IsSandbox ? "https://auth.sandbox.ebay.com/oauth2/authorize" : "https://auth.ebay.com/oauth2/authorize";
        var scope = "https://api.ebay.com/oauth/api_scope https://api.ebay.com/oauth/api_scope/sell.inventory https://api.ebay.com/oauth/api_scope/sell.account";
        var state = Guid.NewGuid().ToString("N");

        var safeReturnUrl = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
        if (!Uri.IsWellFormedUriString(safeReturnUrl, UriKind.Relative))
        {
            safeReturnUrl = "/";
        }

        Response.Cookies.Append("ebay_oauth_state", state, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(15)
        });

        Response.Cookies.Append("ebay_oauth_return", safeReturnUrl, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            Expires = DateTimeOffset.UtcNow.AddMinutes(15)
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

    [HttpGet("callback")]
    public async Task<IActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, [FromQuery(Name = "error_description")] string? errorDescription)
    {
        var returnUrl = Request.Cookies["ebay_oauth_return"] ?? "/";
        if (!Uri.IsWellFormedUriString(returnUrl, UriKind.Relative))
        {
            returnUrl = "/";
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

        var expectedState = Request.Cookies["ebay_oauth_state"];
        if (string.IsNullOrWhiteSpace(expectedState) || string.IsNullOrWhiteSpace(state))
        {
            return Redirect(BuildRedirect("error", "OAuth state missing or invalid."));
        }

        var expectedStateBytes = Encoding.UTF8.GetBytes(expectedState);
        var stateBytes = Encoding.UTF8.GetBytes(state);
        if (!CryptographicOperations.FixedTimeEquals(expectedStateBytes, stateBytes))
        {
            return Redirect(BuildRedirect("error", "OAuth state validation failed."));
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
        var existingToken = await _context.EbayTokens.FirstOrDefaultAsync();
        if (existingToken == null)
        {
            existingToken = new EbayTokenInfo();
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

    [HttpGet("status")]
    public async Task<ActionResult<AuthStatusResponse>> GetStatus()
    {
        var token = await _context.EbayTokens.FirstOrDefaultAsync();
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

    [HttpPost("manual-token")]
    public async Task<IActionResult> SetManualToken([FromBody] ManualTokenRequest request)
    {
        try
        {
            if (string.IsNullOrEmpty(request.Token)) return BadRequest("Token is required");

            var existingToken = await _context.EbayTokens.FirstOrDefaultAsync();
            if (existingToken == null)
            {
                existingToken = new EbayTokenInfo();
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
}

public class ManualTokenRequest
{
    public string Token { get; set; } = string.Empty;
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
