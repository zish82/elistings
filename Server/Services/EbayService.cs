using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Server.Services;

public class EbayService : IEbayService
{
    private readonly EbaySettings _settings;
    private readonly HttpClient _httpClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICurrentUserService _currentUserService;

    public EbayService(IOptions<EbaySettings> settings, HttpClient httpClient, IServiceProvider serviceProvider, ICurrentUserService currentUserService)
    {
        _settings = settings.Value;
        _httpClient = httpClient;
        _serviceProvider = serviceProvider;
        _currentUserService = currentUserService;
    }

    public async Task<System.Text.Json.Nodes.JsonNode?> GetFeeEstimateAsync(Shared.ListingDto listing)
    {
        if (_settings.IsSandbox)
        {
            Console.WriteLine("Fee summary is unavailable in sandbox mode. Returning placeholder response.");
            var placeholder = new System.Text.Json.Nodes.JsonObject
            {
                ["note"] = "Fee estimates are disabled in sandbox environments.",
                ["feeSummaries"] = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["feeType"] = "Sandbox Placeholder",
                        ["fee"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["value"] = "0.00",
                            ["currency"] = "GBP"
                        }
                    }
                },
                ["total"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["value"] = "0.00",
                    ["currency"] = "GBP"
                }
            };
            return placeholder;
        }

        var token = await GetOAuthTokenAsync(listing.EbayAccountId);
        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";

        var payload = new
        {
            listings = new[] {
                new {
                    listing = new {
                        price = new { value = listing.Price.ToString("F2", System.Globalization.CultureInfo.InvariantCulture), currency = "GBP" },
                        categoryId = string.IsNullOrWhiteSpace(listing.CategoryId) ? "57013" : listing.CategoryId,
                        listingQuantity = 1,
                        merchantLocationKey = "main-warehouse",
                        listingPolicies = new {
                            fulfillmentPolicyId = listing.FulfillmentPolicyId,
                            paymentPolicyId = listing.PaymentPolicyId,
                            returnPolicyId = listing.ReturnPolicyId
                        }
                    }
                }
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/sell/fees/v1/fee_summary?marketplace_id=EBAY_GB");
        // Log payload for debugging
        try {
            var dbg = System.Text.Json.JsonSerializer.Serialize(payload);
            Console.WriteLine("[Debug] eBay fee summary request payload: " + dbg);
        } catch { }
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(payload);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync();
            Console.WriteLine("[Error] eBay Fee estimate request URL: " + request.RequestUri);
            Console.WriteLine("[Error] eBay Fee estimate response status: " + (int)response.StatusCode + " " + response.ReasonPhrase);
            Console.WriteLine("[Error] eBay Fee estimate response headers: " + string.Join(";", response.Headers.Select(h => h.Key + ":" + string.Join(",", h.Value))));
            Console.WriteLine("[Error] eBay Fee estimate response body: " + (string.IsNullOrEmpty(err) ? "<empty>" : err));

            var errNode = new System.Text.Json.Nodes.JsonObject
            {
                ["statusCode"] = (int)response.StatusCode,
                ["reason"] = response.ReasonPhrase ?? "",
                ["body"] = string.IsNullOrEmpty(err) ? "" : err,
                ["note"] = "Fee estimate request failed. Confirm that your eBay application has access to the Sell Fees API and that the OAuth token has necessary scopes."
            };
            return errNode;
        }

        var node = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>();
        return node;
    }

    public async Task<EbayListingResponse> CreateListingAsync(ListingDto listing)
    {
        var token = await GetOAuthTokenAsync(listing.EbayAccountId);
        
        // 1. Create/Update Inventory Item
        // Use persistent SKU for the listing if available, else generate one.
        var sku = !string.IsNullOrEmpty(listing.Sku) ? listing.Sku : $"SKU_{listing.Id}";
        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";
        
        Console.WriteLine($"[Listing: {listing.Id}] Using SKU: {sku}");
        
        // 0. Handle Images (Convert external URLs to eBay URLs if needed)
        var finalImageUrls = new List<string>();
        var imageService = _serviceProvider.GetRequiredService<IMarketplaceImageService>();

        if (listing.ImageUrls != null && listing.ImageUrls.Any())
        {
            Console.WriteLine($"[Listing: {listing.Id}] Processing {listing.ImageUrls.Count} images...");
            foreach (var url in listing.ImageUrls)
            {
                if (url.Contains("ebayimg.com"))
                {
                    finalImageUrls.Add(url);
                }
                else
                {
                    try 
                    {
                        var ebayUrl = await imageService.UploadImageFromUrlAsync(url, listing.EbayAccountId);
                        finalImageUrls.Add(ebayUrl);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[Listing: {listing.Id}] Image upload failed for {url}: {ex.Message}");
                    }
                }
            }
        }
        // Ensure at least one image is present, as eBay requires it.
        if (!finalImageUrls.Any())
        {
            finalImageUrls.Add("https://via.placeholder.com/500");
        }

        var inventoryItem = new
        {
            availability = new { shipToLocationAvailability = new { quantity = 1 } },
            condition = "NEW",
            product = new
            {
                title = listing.Title,
                description = listing.Description,
                aspects = new Dictionary<string, string[]>
                {
                    { "Brand", new[] { string.IsNullOrWhiteSpace(listing.Brand) ? "Unbranded" : listing.Brand } },
                    { "Type", new[] { string.IsNullOrWhiteSpace(listing.Type) ? "Fire Extinguisher" : listing.Type } },
                    { "Colour", new[] { string.IsNullOrWhiteSpace(listing.Colour) ? "Red" : listing.Colour } }
                },
                imageUrls = finalImageUrls.ToArray()
            }
        };

        var inventoryItemContent = JsonContent.Create(inventoryItem);
        inventoryItemContent.Headers.Add("Content-Language", "en-GB");

        var request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/sell/inventory/v1/inventory_item/{sku}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = inventoryItemContent;
        
        var createResponse = await _httpClient.SendAsync(request);
        
        if (!createResponse.IsSuccessStatusCode)
        {
            var error = await createResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"eBay Item Creation Failed ({createResponse.StatusCode}): {error}");
            throw new Exception($"eBay Item Creation Failed: {error}");
        }
        Console.WriteLine("eBay Item Created/Updated successfully.");

        // 2. Ensure Location Exists (Crucial for Offers)
        var locationKey = "main-warehouse";
        await CreateDefaultLocationAsync(locationKey, token, baseUrl);

        string? offerId = listing.EbayOfferId;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        
        // 3. Create or Update Offer
        if (string.IsNullOrEmpty(offerId))
        {
            // First check if an offer already exists for this SKU on eBay
            var getOfferRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/sell/inventory/v1/offer?sku={sku}&marketplace_id=EBAY_GB");
            getOfferRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            var getOfferResponse = await _httpClient.SendAsync(getOfferRequest);
            
            if (getOfferResponse.IsSuccessStatusCode)
            {
                var existingOffers = await getOfferResponse.Content.ReadFromJsonAsync<JsonNode>();
                var firstOffer = existingOffers?["offers"]?.AsArray()?.FirstOrDefault();
                if (firstOffer != null)
                {
                    offerId = firstOffer["offerId"]?.GetValue<string>();
                    Console.WriteLine($"Found existing eBay Offer: {offerId}. Will update it with latest details.");
                }
            }
        }

        var targetCategory = string.IsNullOrWhiteSpace(listing.CategoryId) ? "57013" : listing.CategoryId.Trim();
        
        var offerRequestData = new
        {
            sku = sku,
            marketplaceId = "EBAY_GB", 
            format = "FIXED_PRICE",
            availableQuantity = 1,
            categoryId = targetCategory, 
            listingDescription = listing.Description,
            listingPolicies = new
            {
                fulfillmentPolicyId = listing.FulfillmentPolicyId,
                paymentPolicyId = listing.PaymentPolicyId,
                returnPolicyId = listing.ReturnPolicyId
            },
            pricingSummary = new
            {
                price = new { value = listing.Price.ToString("F2", culture), currency = "GBP" } 
            },
            merchantLocationKey = locationKey
        };

        var offerJson = System.Text.Json.JsonSerializer.Serialize(offerRequestData);
        Console.WriteLine($"[SKU: {sku}] Final Category ID: {targetCategory}");
        Console.WriteLine($"[SKU: {sku}] Offer Request JSON: {offerJson}");

        if (string.IsNullOrEmpty(offerId))
        {
            // Create New Offer
            Console.WriteLine($"[SKU: {sku}] Creating new eBay Offer with price {listing.Price.ToString("F2", culture)} GBP...");
            var offerContent = JsonContent.Create(offerRequestData);
            offerContent.Headers.Add("Content-Language", "en-GB");

            var offerRequestMessage = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/sell/inventory/v1/offer");
            offerRequestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            offerRequestMessage.Content = offerContent;

            var offerResponse = await _httpClient.SendAsync(offerRequestMessage);
            if (!offerResponse.IsSuccessStatusCode)
            {
                var error = await offerResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"eBay Offer Creation Failed ({offerResponse.StatusCode}): {error}");
                throw new Exception($"eBay Offer Creation Failed: {error}");
            }
            
            var offerData = await offerResponse.Content.ReadFromJsonAsync<EbayOfferResponse>();
            offerId = offerData?.offerId ?? throw new Exception("Failed to get offerId after creation");
            Console.WriteLine($"eBay Offer Created successfully: {offerId}");
        }
        else
        {
            // Update Existing Offer
            Console.WriteLine($"[SKU: {sku}] Updating existing eBay Offer {offerId} with price {listing.Price.ToString("F2", culture)} GBP...");
            var updateContent = JsonContent.Create(offerRequestData);
            updateContent.Headers.Add("Content-Language", "en-GB");

            var updateRequestMessage = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/sell/inventory/v1/offer/{offerId}");
            updateRequestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            updateRequestMessage.Content = updateContent;

            var updateResponse = await _httpClient.SendAsync(updateRequestMessage);
            if (!updateResponse.IsSuccessStatusCode)
            {
                var error = await updateResponse.Content.ReadAsStringAsync();
                Console.WriteLine($"eBay Offer Update Failed ({updateResponse.StatusCode}): {error}");
                throw new Exception($"eBay Offer Update Failed: {error}");
            }
            Console.WriteLine($"eBay Offer {offerId} updated successfully.");
        }

        // 4. Publish Offer (with a small delay for eventual consistency)
        // Production environment can sometimes take longer to index new inventory items.
        Console.WriteLine($"[SKU: {sku}] [Offer: {offerId}] Waiting 5 seconds for inventory indexing before publishing...");
        await Task.Delay(5000);

        HttpResponseMessage? publishResponse = null;
        bool published = false;
        int maxRetries = 3;
        int currentRetry = 0;

        while (currentRetry <= maxRetries && !published)
        {
            var publishContent = JsonContent.Create(new { });
            publishContent.Headers.Add("Content-Language", "en-GB");

            var publishRequestMessage = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/sell/inventory/v1/offer/{offerId}/publish");
            publishRequestMessage.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            publishRequestMessage.Content = publishContent;

            publishResponse = await _httpClient.SendAsync(publishRequestMessage);
            
            if (publishResponse.IsSuccessStatusCode)
            {
                published = true;
                break;
            }

            var error = await publishResponse.Content.ReadAsStringAsync();
            if (error.Contains("25604") && currentRetry < maxRetries)
            {
                currentRetry++;
                Console.WriteLine($"[Attempt {currentRetry}/{maxRetries + 1}] Product not found (25604). Retrying in 4 seconds...");
                await Task.Delay(4000);
            }
            else
            {
                // Permanent failure or ran out of retries
                Console.WriteLine($"eBay Listing Publication Failed ({publishResponse.StatusCode}): {error}");
                throw new Exception($"eBay Listing Publication Failed: {error}");
            }
        }

        Console.WriteLine($"eBay Listing Published successfully for SKU: {sku}");
 
        var publishData = await publishResponse!.Content.ReadFromJsonAsync<EbayPublishResponse>();
        return new EbayListingResponse
        {
            Sku = sku,
            OfferId = offerId!,
            ListingId = publishData?.listingId ?? $"LISTING-SUCCESS-{offerId}"
        };
    }

    public async Task<List<EbayPolicyDto>> GetPaymentPoliciesAsync(int? accountId = null) 
        => await FetchPoliciesAsync("payment", "paymentPolicies", accountId);

    public async Task<List<EbayPolicyDto>> GetFulfillmentPoliciesAsync(int? accountId = null)
        => await FetchPoliciesAsync("fulfillment", "fulfillmentPolicies", accountId);

    public async Task<List<EbayPolicyDto>> GetReturnPoliciesAsync(int? accountId = null)
        => await FetchPoliciesAsync("return", "returnPolicies", accountId);

    private async Task<List<EbayPolicyDto>> FetchPoliciesAsync(string urlPart, string jsonKey, int? accountId)
    {
        string token;
        try
        {
            token = await GetOAuthTokenAsync(accountId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"eBay {urlPart} policy fetch skipped: {ex.Message}");
            return new List<EbayPolicyDto>();
        }

        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";
        
        var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/sell/account/v1/{urlPart}_policy?marketplace_id=EBAY_GB");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        
        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"eBay {urlPart} Policy Fetch Failed: {error}");
            // We return empty for now but log the error
            return new List<EbayPolicyDto>();
        }

        var data = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>();
        var policies = data?[jsonKey]?.AsArray();
        
        if (policies == null) return new List<EbayPolicyDto>();

        var result = new List<EbayPolicyDto>();
        foreach (var p in policies)
        {
            result.Add(new EbayPolicyDto
            {
                PolicyId = p?[$"{urlPart}PolicyId"]?.GetValue<string>()?.Trim() ?? "",
                Name = p?["name"]?.GetValue<string>()?.Trim() ?? "",
                Description = p?["description"]?.GetValue<string>()?.Trim() ?? ""
            });
        }
        return result;
    }

    private async Task CreateDefaultLocationAsync(string locationKey, string token, string baseUrl)
    {
        var locationRequest = new
        {
            location = new
            {
                address = new
                {
                    addressLine1 = "123 Main St",
                    city = "London",
                    postalCode = "W1A 1AA",
                    country = "GB"
                }
            },
            name = "Main Warehouse",
            locationTypes = new[] { "STORE" }
        };

        var content = JsonContent.Create(locationRequest);
        content.Headers.Add("Content-Language", "en-GB");

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/sell/inventory/v1/location/{locationKey}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        request.Content = content;

        var response = await _httpClient.SendAsync(request);
        // We ignore 409 Conflict (meaning it already exists)
        if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            var error = await response.Content.ReadAsStringAsync();
            // We don't throw here to avoid blocking if the error is just "already exists" in a different format
            Console.WriteLine($"Warning: Could not ensure location exists: {error}");
        }
    }

    public async Task<string> GetOAuthTokenAsync(int? accountId = null)
    {
        // 1. Try manual override from appsettings (for dev/temp use)
        if (!string.IsNullOrEmpty(_settings.UserToken) && _settings.UserToken != "PASTE_YOUR_USER_TOKEN_HERE")
        {
            return _settings.UserToken;
        }

        var userId = _currentUserService.UserId;
        if (userId == null)
        {
            throw new Exception("No authenticated user context found for eBay token.");
        }

        // 2. Load from Database
        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<Server.Data.AppDbContext>();
        var tokenInfo = await ResolveTokenInfoAsync(context, userId.Value, accountId);

        if (tokenInfo == null)
            throw new Exception("eBay account not connected. Please login first.");

        // 3. If valid, return it
        if (tokenInfo.ExpiryTime > DateTime.UtcNow.AddMinutes(5))
        {
            return tokenInfo.AccessToken;
        }

        // 4. If expired, try refresh
        return await RefreshTokenAsync(tokenInfo, context);
    }

    public async Task RefreshOAuthTokenAsync(int accountId)
    {
        var userId = _currentUserService.UserId;
        if (userId == null)
        {
            throw new Exception("No authenticated user context found for eBay token.");
        }

        using var scope = _serviceProvider.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<Server.Data.AppDbContext>();
        var tokenInfo = await ResolveTokenInfoAsync(context, userId.Value, accountId);
        if (tokenInfo == null)
        {
            throw new Exception("eBay account not found.");
        }

        await RefreshTokenAsync(tokenInfo, context);
    }

    private static async Task<Server.Data.EbayTokenInfo?> ResolveTokenInfoAsync(Server.Data.AppDbContext context, int userId, int? accountId)
    {
        if (accountId.HasValue)
        {
            return await context.EbayTokens.FirstOrDefaultAsync(t => t.UserId == userId && t.Id == accountId.Value);
        }

        return await context.EbayTokens
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.IsDefault)
            .ThenBy(t => t.Id)
            .FirstOrDefaultAsync();
    }

    private async Task<string> RefreshTokenAsync(Server.Data.EbayTokenInfo tokenInfo, Server.Data.AppDbContext context)
    {
        if (string.IsNullOrWhiteSpace(tokenInfo.RefreshToken) || string.Equals(tokenInfo.RefreshToken, "MANUAL", StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception("This account cannot be refreshed automatically. Please reconnect the eBay account.");
        }

        if (tokenInfo.RefreshTokenExpiryTime <= DateTime.UtcNow)
        {
            throw new Exception("The eBay refresh token has expired. Please reconnect the eBay account.");
        }

        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";
        var authHeader = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{_settings.AppId}:{_settings.CertId}"));

        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/identity/v1/oauth2/token");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authHeader);
        
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("grant_type", "refresh_token"),
            new KeyValuePair<string, string>("refresh_token", tokenInfo.RefreshToken),
            new KeyValuePair<string, string>("scope", "https://api.ebay.com/oauth/api_scope https://api.ebay.com/oauth/api_scope/sell.inventory https://api.ebay.com/oauth/api_scope/sell.account")
        });
        request.Content = content;

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to refresh eBay token: {error}");
        }

        var data = await response.Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonNode>();
        tokenInfo.AccessToken = data?["access_token"]?.GetValue<string>() ?? throw new Exception("No access token in refresh response");
        tokenInfo.ExpiryTime = DateTime.UtcNow.AddSeconds(data?["expires_in"]?.GetValue<int>() ?? 0);

        var refreshedRefreshToken = data?["refresh_token"]?.GetValue<string>();
        if (!string.IsNullOrWhiteSpace(refreshedRefreshToken))
        {
            tokenInfo.RefreshToken = refreshedRefreshToken;
        }

        var refreshTokenExpiresIn = data?["refresh_token_expires_in"]?.GetValue<int>();
        if (refreshTokenExpiresIn.HasValue && refreshTokenExpiresIn.Value > 0)
        {
            tokenInfo.RefreshTokenExpiryTime = DateTime.UtcNow.AddSeconds(refreshTokenExpiresIn.Value);
        }
        
        await context.SaveChangesAsync();
        return tokenInfo.AccessToken;
    }

    public async Task<List<CategorySuggestionDto>> GetCategorySuggestionsAsync(string title, int? accountId = null)
    {
        if (string.IsNullOrWhiteSpace(title)) return new List<CategorySuggestionDto>();

        string token;
        try
        {
            token = await GetOAuthTokenAsync(accountId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"eBay category suggestions skipped: {ex.Message}");
            return new List<CategorySuggestionDto>();
        }

        // UK Category Tree ID is 3.
        var treeId = "3"; 
        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";
        var url = $"{baseUrl}/commerce/taxonomy/v1/category_tree/{treeId}/get_category_suggestions?q={Uri.EscapeDataString(title)}";

        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            Console.WriteLine($"eBay Category Suggestions Failed: {error}");
            return new List<CategorySuggestionDto>();
        }

        var node = await response.Content.ReadFromJsonAsync<JsonNode>();
        var suggestions = new List<CategorySuggestionDto>();

        var categorySuggestions = node?["categorySuggestions"]?.AsArray();
        if (categorySuggestions != null)
        {
            foreach (var s in categorySuggestions)
            {
                var category = s?["category"];
                if (category == null) continue;

                var suggestion = new CategorySuggestionDto
                {
                    CategoryId = category["categoryId"]?.GetValue<string>() ?? "",
                    CategoryName = category["categoryName"]?.GetValue<string>() ?? "",
                    Confidence = 1.0 
                };

                var ancestors = s?["categoryTreeNodeAncestors"]?.AsArray();
                if (ancestors != null)
                {
                    foreach (var a in ancestors)
                    {
                        suggestion.CategoryPath.Insert(0, a?["categoryName"]?.GetValue<string>() ?? "");
                    }
                }
                suggestion.CategoryPath.Add(suggestion.CategoryName);
                suggestions.Add(suggestion);
            }
        }

        return suggestions;
    }
}

public class EbayTokenResponse
{
    public string access_token { get; set; } = string.Empty;
    public int expires_in { get; set; }
    public string refresh_token { get; set; } = string.Empty;
}

public class EbayOfferResponse
{
    public string offerId { get; set; } = string.Empty;
}

public class EbayPublishResponse
{
    public string listingId { get; set; } = string.Empty;
}
