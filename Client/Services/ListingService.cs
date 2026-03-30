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

    public async Task<List<ListingDto>> GetListingsAsync()
    {
        return await _http.GetFromJsonAsync<List<ListingDto>>("api/listings") ?? new List<ListingDto>();
    }

    public async Task<ListingDto> CreateListingAsync(CreateListingRequest request)
    {
        var response = await _http.PostAsJsonAsync("api/listings", request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ListingDto>() ?? throw new Exception("Failed to deserialize response");
    }

    public async Task<ListingDto> GetListingAsync(int id)
    {
        return await _http.GetFromJsonAsync<ListingDto>($"api/listings/{id}") ?? throw new Exception("Listing not found");
    }

    public async Task UpdateListingAsync(int id, CreateListingRequest request)
    {
        var response = await _http.PutAsJsonAsync($"api/listings/{id}", request);
        response.EnsureSuccessStatusCode();
    }

    public async Task<ExtractedDetailsDto> ExtractDetailsAsync(string url)
    {
        return await _http.GetFromJsonAsync<ExtractedDetailsDto>($"api/listings/extract?url={Uri.EscapeDataString(url)}") ?? new ExtractedDetailsDto();
    }

    public async Task<List<EbayPolicyDto>> GetPaymentPoliciesAsync()
    {
        return await _http.GetFromJsonAsync<List<EbayPolicyDto>>("api/ebaypolicies/payment") ?? new List<EbayPolicyDto>();
    }

    public async Task<List<EbayPolicyDto>> GetFulfillmentPoliciesAsync()
    {
        return await _http.GetFromJsonAsync<List<EbayPolicyDto>>("api/ebaypolicies/fulfillment") ?? new List<EbayPolicyDto>();
    }

    public async Task<List<EbayPolicyDto>> GetReturnPoliciesAsync()
    {
        return await _http.GetFromJsonAsync<List<EbayPolicyDto>>("api/ebaypolicies/return") ?? new List<EbayPolicyDto>();
    }

    public async Task<Shared.EbayDefaultPoliciesDto> GetDefaultPoliciesAsync()
    {
        return await _http.GetFromJsonAsync<Shared.EbayDefaultPoliciesDto>("api/ebay/default-policies") ?? new Shared.EbayDefaultPoliciesDto();
    }

    public async Task SetManualTokenAsync(string token)
    {
        var response = await _http.PostAsJsonAsync("api/auth/manual-token", new { Token = token });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to save token. Status: {response.StatusCode}. Details: {error}");
        }
    }

    public async Task<string> GetEbayLoginUrlAsync(string returnUrl = "/")
    {
        var response = await _http.GetFromJsonAsync<LoginUrlResponse>($"api/auth/login-url?returnUrl={Uri.EscapeDataString(returnUrl)}");
        if (response == null || string.IsNullOrWhiteSpace(response.LoginUrl))
        {
            throw new Exception("Failed to get eBay login URL.");
        }
        return response.LoginUrl;
    }

    public async Task<AuthStatusResponse> GetEbayAuthStatusAsync()
    {
        return await _http.GetFromJsonAsync<AuthStatusResponse>("api/auth/status") ?? new AuthStatusResponse();
    }

    public async Task<ListingDto> PublishListingAsync(int id)
    {
        var response = await _http.PostAsJsonAsync($"api/ebay/publish/{id}", new { });
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to publish listing. Status: {response.StatusCode}. Details: {error}");
        }
        return await response.Content.ReadFromJsonAsync<ListingDto>() ?? throw new Exception("Failed to deserialize response");
    }

    public async Task<List<CategorySuggestionDto>> GetCategorySuggestionsAsync(string title)
    {
        return await _http.GetFromJsonAsync<List<CategorySuggestionDto>>($"api/ebaypolicies/category-suggestions?title={Uri.EscapeDataString(title)}") ?? new List<CategorySuggestionDto>();
    }

    public async Task<int> BulkExtractAsync(string categoryUrl)
    {
        var response = await _http.PostAsJsonAsync("api/listings/bulk-extract", categoryUrl);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BulkResult>();
        return result?.Count ?? 0;
    }

    public async Task<BulkDeleteResult> BulkDeleteAsync(List<int> ids)
    {
        var response = await _http.PostAsJsonAsync("api/listings/bulk-delete", ids);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BulkDeleteResult>() ?? new BulkDeleteResult();
    }

    public async Task<(int Published, int Failed, List<string> Errors)> BulkPublishAsync(List<int> ids)
    {
        var response = await _http.PostAsJsonAsync("api/listings/bulk-publish", ids);
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
        var response = await _http.PostAsJsonAsync("api/ebay/fee-estimate", listing);
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
