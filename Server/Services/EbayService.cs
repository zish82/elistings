using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Caching.Memory;

namespace Server.Services;

public class EbayService : IEbayService
{
    private readonly EbaySettings _settings;
    private readonly HttpClient _httpClient;
    private readonly IServiceProvider _serviceProvider;
    private readonly ICurrentUserService _currentUserService;
    private readonly IMemoryCache _cache;

    public EbayService(IOptions<EbaySettings> settings, HttpClient httpClient, IServiceProvider serviceProvider, ICurrentUserService currentUserService, IMemoryCache cache)
    {
        _settings = settings.Value;
        _httpClient = httpClient;
        _serviceProvider = serviceProvider;
        _currentUserService = currentUserService;
        _cache = cache;
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
                        listingQuantity = listing.Quantity < 1 ? 1 : listing.Quantity,
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
        var quantity = listing.Quantity < 1 ? 1 : listing.Quantity;

        Console.WriteLine($"[Listing: {listing.Id}] Using SKU: {sku}, Quantity: {quantity}");
        
        // 0. Handle Images (Convert external URLs to eBay URLs if needed)
        var finalImageUrls = new List<string>();
        var imageErrors = new List<string>();
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
                        var label = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? "uploaded image" : url;
                        Console.WriteLine($"[Listing: {listing.Id}] Image upload failed for {label}: {ex.Message}");
                        imageErrors.Add($"{label}: {ex.Message}");
                    }
                }
            }
        }
        // eBay requires at least one image; fail clearly instead of publishing a placeholder.
        if (!finalImageUrls.Any())
        {
            throw new Exception(imageErrors.Any()
                ? "All image uploads to eBay failed. " + string.Join(" | ", imageErrors)
                : "At least one image is required to publish on eBay.");
        }

        var inventoryItem = new
        {
            availability = new { shipToLocationAvailability = new { quantity = quantity } },
            condition = "NEW",
            product = BuildProduct(listing, finalImageUrls)
        };

        var inventoryItemJson = System.Text.Json.JsonSerializer.Serialize(inventoryItem);

        // PUT inventory_item is idempotent, so eBay's generic system errors (25001 / HTTP 5xx) are retried.
        using (var createResponse = await SendWithRetryAsync(() =>
               {
                   var request = new HttpRequestMessage(HttpMethod.Put, $"{baseUrl}/sell/inventory/v1/inventory_item/{Uri.EscapeDataString(sku)}");
                   request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                   request.Content = new StringContent(inventoryItemJson, System.Text.Encoding.UTF8, "application/json");
                   request.Content.Headers.Add("Content-Language", "en-GB");
                   return request;
               }, $"inventory item {sku}"))
        {
            if (!createResponse.IsSuccessStatusCode)
            {
                var error = await EbayApiException.FromResponseAsync(createResponse, $"Creating eBay inventory item {sku}");
                // Log the payload so a persistent failure can be reproduced / sent to eBay support.
                Console.WriteLine($"[eBay] {error.Message}\n[eBay] Inventory item payload for {sku}: {inventoryItemJson}");
                throw error;
            }
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

        if (string.IsNullOrWhiteSpace(listing.CategoryId))
        {
            throw new Exception("An eBay category is required to publish.");
        }
        var targetCategory = listing.CategoryId.Trim();
        
        var offerRequestData = new
        {
            sku = sku,
            marketplaceId = "EBAY_GB", 
            format = "FIXED_PRICE",
            availableQuantity = quantity,
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

    // Brand defaults to "Unbranded" (accepted by eBay). Type/Colour are only sent when known:
    // the old "Fire Extinguisher"/"Red" defaults were wrong for most products. If a category
    // requires them, eBay returns a clear "item specific missing" error to fix on the listing.
    // "Not Specified" (the DTO placeholder for Type) is treated as unknown.
    private static Dictionary<string, string[]> BuildAspects(ListingDto listing)
    {
        static bool IsKnown(string? v) => !string.IsNullOrWhiteSpace(v)
            && !v.Equals("Not Specified", StringComparison.OrdinalIgnoreCase);

        var aspects = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            { "Brand", new[] { string.IsNullOrWhiteSpace(listing.Brand) ? "Unbranded" : listing.Brand.Trim() } }
        };
        // Supplier specifics (e.g. Material, Type) first; explicit Brand/Type/Colour fields win.
        var ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Unit of Measure", "Product Code", "Barcode", "Availability", "Brand" };
        foreach (var kv in listing.ItemSpecifics ?? new Dictionary<string, string>())
        {
            if (ignored.Contains(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            var name = kv.Key.Trim();
            if (name.Length > 65) continue; // eBay aspect name limit
            aspects[name] = new[] { kv.Value.Trim().Length > 65 ? kv.Value.Trim()[..65] : kv.Value.Trim() };
        }

        if (IsKnown(listing.Type)) aspects["Type"] = new[] { listing.Type!.Trim() };
        if (IsKnown(listing.Colour)) aspects["Colour"] = new[] { listing.Colour!.Trim() };
        return aspects;
    }

    /// <summary>
    /// Sends an idempotent request, retrying eBay's transient failures: HTTP 5xx/429 or error 25001
    /// ("A system error has occurred"). A fresh request is built for each attempt.
    /// </summary>
    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> createRequest, string what)
    {
        var delays = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };
        for (var attempt = 0; ; attempt++)
        {
            using var request = createRequest();
            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode || attempt >= delays.Length || !await IsTransientAsync(response))
            {
                return response;
            }

            Console.WriteLine($"[eBay] Transient error for {what} ({(int)response.StatusCode}); retrying in {delays[attempt].TotalSeconds}s...");
            response.Dispose();
            await Task.Delay(delays[attempt]);
        }
    }

    private static async Task<bool> IsTransientAsync(HttpResponseMessage response)
    {
        if ((int)response.StatusCode >= 500 || (int)response.StatusCode == 429) return true;
        await response.Content.LoadIntoBufferAsync(); // keep the body readable for the caller
        var body = await response.Content.ReadAsStringAsync();
        return body.Contains("\"errorId\":25001", StringComparison.Ordinal);
    }

    private static Dictionary<string, object> BuildProduct(ListingDto listing, List<string> imageUrls)
    {
        var product = new Dictionary<string, object>
        {
            ["title"] = listing.Title,
            ["description"] = listing.Description,
            ["aspects"] = BuildAspects(listing),
            ["imageUrls"] = imageUrls.ToArray()
        };

        // EAN/UPC lets eBay match the product catalogue. Only send well-formed GTINs.
        var barcode = listing.Barcode?.Trim();
        if (!string.IsNullOrEmpty(barcode) && barcode.All(char.IsDigit))
        {
            if (barcode.Length == 12) product["upc"] = new[] { barcode };
            else if (barcode.Length is 8 or 13 or 14) product["ean"] = new[] { barcode };
        }

        return product;
    }

    public async Task<Dictionary<string, string>> UpdateQuantitiesAsync(int? accountId, IReadOnlyList<MarketplaceQuantityUpdate> updates)
    {
        var failures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (updates.Count == 0) return failures;
        if (updates.Count > 25) throw new ArgumentException("eBay accepts at most 25 quantity updates per call.", nameof(updates));

        var token = await GetOAuthTokenAsync(accountId);
        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";
        var payload = System.Text.Json.JsonSerializer.Serialize(new
        {
            requests = updates.Select(u => new
            {
                sku = u.Sku,
                shipToLocationAvailability = new { quantity = Math.Max(0, u.Quantity) },
                offers = new[] { new { offerId = u.OfferId, availableQuantity = Math.Max(0, u.Quantity) } }
            })
        });

        // Setting a quantity is idempotent, so transient eBay errors are retried.
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/sell/inventory/v1/bulk_update_price_quantity");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
            request.Content.Headers.Add("Content-Language", "en-GB");
            return request;
        }, "quantity update");

        if (!response.IsSuccessStatusCode && (int)response.StatusCode != 207)
        {
            var error = await EbayApiException.FromResponseAsync(response, "Updating eBay quantities");
            foreach (var u in updates) failures[u.Sku] = error.Message;
            return failures;
        }

        // 200/207: one entry per inventory item and per offer; any entry with an error status fails that SKU.
        var body = await response.Content.ReadAsStringAsync();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("responses", out var responses) && responses.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var entry in responses.EnumerateArray())
                {
                    var status = entry.TryGetProperty("statusCode", out var sc) && sc.TryGetInt32(out var code) ? code : 200;
                    if (status < 400) continue;
                    var sku = entry.TryGetProperty("sku", out var s) ? s.GetString() : null;
                    if (sku == null)
                    {
                        var offerId = entry.TryGetProperty("offerId", out var o) ? o.GetString() : null;
                        sku = updates.FirstOrDefault(u => u.OfferId == offerId)?.Sku;
                    }
                    if (sku == null) continue;
                    var message = entry.TryGetProperty("errors", out var errs) && errs.ValueKind == System.Text.Json.JsonValueKind.Array && errs.GetArrayLength() > 0
                        ? (errs[0].TryGetProperty("message", out var m) ? m.GetString() : null)
                        : null;
                    failures[sku] = $"eBay returned {status}{(message == null ? "" : ": " + message)}";
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 200 without a readable body: treat as success
        }
        return failures;
    }

    public async Task<List<EbayPolicyDto>> GetPaymentPoliciesAsync(int? accountId = null)
        => await FetchPoliciesAsync("payment", "paymentPolicies", accountId);

    public async Task<List<EbayPolicyDto>> GetFulfillmentPoliciesAsync(int? accountId = null)
        => await FetchPoliciesAsync("fulfillment", "fulfillmentPolicies", accountId);

    public async Task<List<EbayPolicyDto>> GetReturnPoliciesAsync(int? accountId = null)
        => await FetchPoliciesAsync("return", "returnPolicies", accountId);

    private async Task<List<EbayPolicyDto>> FetchPoliciesAsync(string urlPart, string jsonKey, int? accountId)
    {
        // Errors are thrown (not swallowed) so the listing page can show why no policies are listed.
        string token;
        try
        {
            token = await GetOAuthTokenAsync(accountId);
        }
        catch (Exception ex)
        {
            throw new EbayApiException($"Can't load eBay {urlPart} policies: {ex.Message}", inner: ex);
        }

        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/sell/account/v1/{urlPart}_policy?marketplace_id=EBAY_GB");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await EbayApiException.FromResponseAsync(response, $"Loading eBay {urlPart} policies");
            Console.WriteLine($"[eBay] {error.Message}");
            throw error;
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

    // Category aspects rarely change; caching avoids an eBay call every time a listing is opened.
    private static readonly TimeSpan AspectCacheDuration = TimeSpan.FromHours(24);
    private const string UkCategoryTreeId = "3";

    public async Task<List<CategoryAspectDto>> GetCategoryAspectsAsync(string categoryId, int? accountId = null)
    {
        categoryId = categoryId?.Trim() ?? string.Empty;
        if (categoryId.Length == 0 || !categoryId.All(char.IsDigit))
        {
            throw new EbayApiException($"'{categoryId}' is not a valid eBay category ID.");
        }

        var cacheKey = $"ebay-aspects:{(_settings.IsSandbox ? "sandbox" : "prod")}:{UkCategoryTreeId}:{categoryId}";
        if (_cache.TryGetValue(cacheKey, out List<CategoryAspectDto>? cached) && cached != null)
        {
            return cached;
        }

        string token;
        try
        {
            token = await GetOAuthTokenAsync(accountId);
        }
        catch (Exception ex)
        {
            throw new EbayApiException($"Can't load item specifics for category {categoryId}: {ex.Message}", inner: ex);
        }

        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com" : "https://api.ebay.com";
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{baseUrl}/commerce/taxonomy/v1/category_tree/{UkCategoryTreeId}/get_item_aspects_for_category?category_id={Uri.EscapeDataString(categoryId)}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            throw await EbayApiException.FromResponseAsync(response, $"Loading item specifics for category {categoryId}");
        }

        List<CategoryAspectDto> aspects;
        try
        {
            aspects = ParseAspects(await response.Content.ReadFromJsonAsync<JsonNode>());
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
        {
            throw new EbayApiException($"eBay returned item specifics for category {categoryId} in an unexpected format: {ex.Message}", inner: ex);
        }
        _cache.Set(cacheKey, aspects, AspectCacheDuration);
        return aspects;
    }

    internal static List<CategoryAspectDto> ParseAspects(JsonNode? node)
    {
        var result = new List<CategoryAspectDto>();
        var aspects = node?["aspects"]?.AsArray();
        if (aspects == null) return result;

        foreach (var a in aspects)
        {
            var name = AsString(a?["localizedAspectName"]);
            if (string.IsNullOrWhiteSpace(name)) continue;

            var constraint = a?["aspectConstraint"];
            result.Add(new CategoryAspectDto
            {
                Name = name,
                Required = constraint?["aspectRequired"] is JsonValue req && req.TryGetValue<bool>(out var isRequired) && isRequired,
                Usage = AsString(constraint?["aspectUsage"]) ?? "OPTIONAL",
                SelectionOnly = string.Equals(AsString(constraint?["aspectMode"]), "SELECTION_ONLY", StringComparison.OrdinalIgnoreCase),
                MultipleValues = string.Equals(AsString(constraint?["itemToAspectCardinality"]), "MULTI", StringComparison.OrdinalIgnoreCase),
                Values = (a?["aspectValues"] as JsonArray)?
                    .Select(v => AsString(v?["localizedValue"]))
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Select(v => v!)
                    .ToList() ?? new List<string>()
            });
        }

        static string? AsString(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        // Required first, then recommended, then optional; keep eBay's order within each group.
        return result
            .Select((aspect, index) => (aspect, index))
            .OrderBy(x => x.aspect.Required ? 0 : x.aspect.Usage == "RECOMMENDED" ? 1 : 2)
            .ThenBy(x => x.index)
            .Select(x => x.aspect)
            .ToList();
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
