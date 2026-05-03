using Microsoft.AspNetCore.Components.WebAssembly.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Shared;

namespace Client.Services;

public class ListingService
{
    private readonly HttpClient _http;

    public ListingService(HttpClient http)
    {
        _http = http;
    }

    private async Task<T?> GetAsync<T>(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>();
    }

    private async Task<T?> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        if (body != null)
        {
            request.Content = JsonContent.Create(body);
        }

        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength == 0)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>();
    }

    public async Task<List<ListingDto>> GetListingsAsync()
    {
        return await GetAsync<List<ListingDto>>("api/listings") ?? new List<ListingDto>();
    }

    public async Task<ListingDto> CreateListingAsync(CreateListingRequest request)
    {
        return await SendAsync<ListingDto>(HttpMethod.Post, "api/listings", request) ?? throw new Exception("Failed to deserialize response");
    }

    public async Task<ListingDto> GetListingAsync(int id)
    {
        return await GetAsync<ListingDto>($"api/listings/{id}") ?? throw new Exception("Listing not found");
    }

    public async Task UpdateListingAsync(int id, CreateListingRequest request)
    {
        await SendAsync<object>(HttpMethod.Put, $"api/listings/{id}", request);
    }

    public async Task<ExtractedDetailsDto> ExtractDetailsAsync(string url)
    {
        return await GetAsync<ExtractedDetailsDto>($"api/listings/extract?url={Uri.EscapeDataString(url)}") ?? new ExtractedDetailsDto();
    }

    public async Task<List<EbayPolicyDto>> GetPaymentPoliciesAsync()
    {
        return await GetAsync<List<EbayPolicyDto>>("api/ebaypolicies/payment") ?? new List<EbayPolicyDto>();
    }

    public async Task<List<EbayPolicyDto>> GetFulfillmentPoliciesAsync()
    {
        return await GetAsync<List<EbayPolicyDto>>("api/ebaypolicies/fulfillment") ?? new List<EbayPolicyDto>();
    }

    public async Task<List<EbayPolicyDto>> GetReturnPoliciesAsync()
    {
        return await GetAsync<List<EbayPolicyDto>>("api/ebaypolicies/return") ?? new List<EbayPolicyDto>();
    }

    public async Task<Shared.EbayDefaultPoliciesDto> GetDefaultPoliciesAsync()
    {
        return await GetAsync<Shared.EbayDefaultPoliciesDto>("api/ebay/default-policies") ?? new Shared.EbayDefaultPoliciesDto();
    }

    public async Task SetManualTokenAsync(string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/manual-token")
        {
            Content = JsonContent.Create(new { Token = token })
        };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to save token. Status: {response.StatusCode}. Details: {error}");
        }
    }

    public async Task<string> GetEbayLoginUrlAsync(string returnUrl = "/")
    {
        var response = await GetAsync<LoginUrlResponse>($"api/auth/login-url?returnUrl={Uri.EscapeDataString(returnUrl)}");
        if (response == null || string.IsNullOrWhiteSpace(response.LoginUrl))
        {
            throw new Exception("Failed to get eBay login URL.");
        }
        return response.LoginUrl;
    }

    public async Task<AuthStatusResponse> GetEbayAuthStatusAsync()
    {
        return await GetAsync<AuthStatusResponse>("api/auth/status") ?? new AuthStatusResponse();
    }

    public async Task<ListingDto> PublishListingAsync(int id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/ebay/publish/{id}")
        {
            Content = JsonContent.Create(new { })
        };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to publish listing. Status: {response.StatusCode}. Details: {error}");
        }
        return await response.Content.ReadFromJsonAsync<ListingDto>() ?? throw new Exception("Failed to deserialize response");
    }

    public async Task<List<CategorySuggestionDto>> GetCategorySuggestionsAsync(string title)
    {
        return await GetAsync<List<CategorySuggestionDto>>($"api/ebaypolicies/category-suggestions?title={Uri.EscapeDataString(title)}") ?? new List<CategorySuggestionDto>();
    }

    public async Task<int> BulkExtractAsync(string categoryUrl)
    {
        var result = await SendAsync<BulkResult>(HttpMethod.Post, "api/listings/bulk-extract", categoryUrl);
        return result?.Count ?? 0;
    }

    public async Task<BulkDeleteResult> BulkDeleteAsync(List<int> ids)
    {
        return await SendAsync<BulkDeleteResult>(HttpMethod.Post, "api/listings/bulk-delete", ids) ?? new BulkDeleteResult();
    }

    public async Task<(int Published, int Failed, List<string> Errors)> BulkPublishAsync(List<int> ids)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/listings/bulk-publish")
        {
            Content = JsonContent.Create(ids)
        };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>();
        int published = payload.GetProperty("Published").GetInt32();
        int failed = payload.GetProperty("Failed").GetInt32();
        var errors = new List<string>();
        if (payload.TryGetProperty("Errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errs.EnumerateArray()) errors.Add(e.GetString() ?? "");
        }
        return (published, failed, errors);
    }

    public async Task<JsonElement> GetFeeEstimateAsync(Shared.ListingDto listing)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/ebay/fee-estimate")
        {
            Content = JsonContent.Create(listing)
        };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
            using var stream = await response.Content.ReadAsStreamAsync();
            var doc = await System.Text.Json.JsonDocument.ParseAsync(stream);
            return doc.RootElement.Clone();
    }

    private class BulkResult { public int Count { get; set; } }
}

public class BulkDeleteResult { public int Marked { get; set; } public int Deleted { get; set; } }

public class LoginUrlResponse
{
    public string LoginUrl { get; set; } = string.Empty;
}

public class AuthStatusResponse
{
    public bool Connected { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
}
