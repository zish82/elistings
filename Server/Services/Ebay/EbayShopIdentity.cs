using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using Server.Data;

namespace Server.Services;

/// <summary>
/// Which eBay shop (seller user ID) a token belongs to – so the same eListings user can't connect the same shop twice.
/// Uses the Trading API GetUser call, which works with the scopes the app already asks for.
/// </summary>
public static class EbayShopIdentity
{
    /// <summary>The eBay user ID of the token's account, or null if eBay doesn't say (the connection then isn't blocked).</summary>
    public static async Task<string?> GetEbayUserIdAsync(HttpClient http, string accessToken, bool sandbox)
    {
        try
        {
            var url = sandbox ? "https://api.sandbox.ebay.com/ws/api.dll" : "https://api.ebay.com/ws/api.dll";
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(@"<?xml version=""1.0"" encoding=""utf-8""?>
<GetUserRequest xmlns=""urn:ebay:apis:eBLBaseComponents""><DetailLevel>ReturnSummary</DetailLevel></GetUserRequest>",
                    System.Text.Encoding.UTF8, "text/xml")
            };
            request.Headers.Add("X-EBAY-API-CALL-NAME", "GetUser");
            request.Headers.Add("X-EBAY-API-SITEID", "3");
            request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", "1193");
            request.Headers.Add("X-EBAY-API-IAF-TOKEN", accessToken);
            using var response = await http.SendAsync(request);
            return ParseUserId(await response.Content.ReadAsStringAsync());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EbayShopIdentity] Could not read the eBay user ID: {ex.Message}");
            return null;
        }
    }

    internal static string? ParseUserId(string xml)
    {
        try
        {
            XNamespace ns = "urn:ebay:apis:eBLBaseComponents";
            var id = XDocument.Parse(xml).Root?.Element(ns + "User")?.Element(ns + "UserID")?.Value;
            return string.IsNullOrWhiteSpace(id) ? null : id.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Another of this eListings user's accounts already connected to the same eBay shop (null = none).
    /// Accounts saved before shop IDs were recorded get theirs looked up now, while their access token still works.
    /// </summary>
    public static async Task<EbayTokenInfo?> FindSameShopAsync(AppDbContext context, HttpClient http, bool sandbox, int userId, string ebayUserId, int? exceptAccountId)
    {
        var others = await context.EbayTokens.Where(t => t.UserId == userId && t.Id != (exceptAccountId ?? 0)).ToListAsync();
        foreach (var other in others.Where(o => o.EbayUserId == null && o.ExpiryTime > DateTime.UtcNow && !string.IsNullOrEmpty(o.AccessToken)))
        {
            other.EbayUserId = await GetEbayUserIdAsync(http, other.AccessToken, sandbox);
        }
        return others.FirstOrDefault(o => string.Equals(o.EbayUserId, ebayUserId, StringComparison.OrdinalIgnoreCase));
    }
}
