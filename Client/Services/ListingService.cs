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
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(await ExtractErrorMessageAsync(response));
        }
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
        if (!response.IsSuccessStatusCode)
        {
            throw new Exception(await ExtractErrorMessageAsync(response));
        }
        if (response.Content.Headers.ContentLength == 0)
        {
            return default;
        }

        return await response.Content.ReadFromJsonAsync<T>();
    }

    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response)
    {
        var fallback = $"Request failed: {(int)response.StatusCode} {response.ReasonPhrase}";

        string? raw = null;
        try
        {
            raw = await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return fallback;
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("errorMessage", out var errorMessage) && errorMessage.ValueKind == JsonValueKind.String)
                {
                    var value = errorMessage.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value!;
                }

                if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                {
                    var value = detail.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value!;
                }

                if (root.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                {
                    var value = title.GetString();
                    if (!string.IsNullOrWhiteSpace(value)) return value!;
                }
            }
        }
        catch
        {
            // Not JSON, fall back to raw text.
        }

        return raw.Trim();
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

    public async Task<ListingDto> ResetListingFromSourceAsync(int id)
    {
        return await SendAsync<ListingDto>(HttpMethod.Post, $"api/listings/{id}/reset-from-source")
               ?? throw new Exception("Failed to reset listing from source.");
    }

    public async Task<SupplierStatusDto> GetSupplierStatusAsync(int id)
    {
        return await GetAsync<SupplierStatusDto>($"api/listings/{id}/supplier-status") ?? new SupplierStatusDto { ListingId = id };
    }

    public async Task<SupplierStatusDto> RefreshSupplierStatusAsync(int id)
    {
        return await SendAsync<SupplierStatusDto>(HttpMethod.Post, $"api/listings/{id}/supplier-status/refresh")
               ?? new SupplierStatusDto { ListingId = id, IsSuccess = false, ErrorMessage = "No response from server." };
    }

    public async Task<SupplierStatusDto> CheckSupplierStatusAsync(string? sourceUrl, string? supplierSku)
    {
        return await SendAsync<SupplierStatusDto>(HttpMethod.Post, "api/listings/supplier-status/check", new { SourceUrl = sourceUrl, SupplierSku = supplierSku })
               ?? new SupplierStatusDto { IsSuccess = false, ErrorMessage = "No response from server." };
    }

    public async Task<List<SupplierFeedDto>> GetSupplierFeedsAsync()
    {
        return await GetAsync<List<SupplierFeedDto>>("api/supplierfeeds") ?? new List<SupplierFeedDto>();
    }

    public async Task<SupplierFeedDto> CreateSupplierFeedAsync(CreateSupplierFeedRequest request)
    {
        return await SendAsync<SupplierFeedDto>(HttpMethod.Post, "api/supplierfeeds", request)
               ?? throw new Exception("Failed to create supplier feed.");
    }

    public async Task DeleteSupplierFeedAsync(int id)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/supplierfeeds/{id}");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new Exception(error);
        }

        throw new Exception($"Failed to delete supplier feed. Status: {response.StatusCode}");
    }

    public async Task<FeedSyncResult> RunCatalogueSyncAsync()
    {
        return await SendAsync<FeedSyncResult>(HttpMethod.Post, "api/supplierproducts/sync/catalogue")
               ?? new FeedSyncResult { ErrorMessage = "No response from server." };
    }

    public async Task<FeedSyncResult> RunStockSyncAsync()
    {
        return await SendAsync<FeedSyncResult>(HttpMethod.Post, "api/supplierproducts/sync/stock")
               ?? new FeedSyncResult { ErrorMessage = "No response from server." };
    }

    public async Task<SupplierImportStatusDto> GetSupplierImportStatusAsync(string supplier)
        => await GetAsync<SupplierImportStatusDto>($"api/supplierproducts/import-status?supplier={Uri.EscapeDataString(supplier)}") ?? new SupplierImportStatusDto();

    public async Task<FeedSyncResult> SyncSupplierFeedAsync(int feedId)
    {
        return await SendAsync<FeedSyncResult>(HttpMethod.Post, $"api/supplierproducts/sync/feed/{feedId}")
               ?? new FeedSyncResult { ErrorMessage = "No response from server." };
    }

    public async Task DeleteSupplierProductAsync(int productId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/supplierproducts/{productId}");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new Exception(await ExtractErrorMessageAsync(response));
    }

    public async Task<int> DeleteSupplierProductsAsync(List<int> productIds)
    {
        var result = await SendAsync<BulkProductDeleteResult>(HttpMethod.Post, "api/supplierproducts/delete", productIds);
        return result?.Deleted ?? 0;
    }

    public async Task<List<SupplierProductListItemDto>> GetSupplierProductsAsync(SupplierProductQuery q)
    {
        var query = new List<string>();
        void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) query.Add($"{key}={Uri.EscapeDataString(value)}"); }
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        Add("supplier", q.Supplier);
        Add("search", q.Search);
        Add("category", q.Category);
        Add("minPrice", q.MinPrice?.ToString(inv));
        Add("maxPrice", q.MaxPrice?.ToString(inv));
        if (q.InStockOnly) Add("inStockOnly", "true");
        if (q.HideAdded) Add("hideAdded", "true");
        Add("groupKey", q.GroupKey);
        Add("catalogCategoryId", q.CatalogCategoryId?.ToString(inv));
        if (!q.IncludeSubcategories) Add("includeSubcategories", "false");
        Add("tagId", q.TagId?.ToString(inv));
        Add("sort", q.Sort);
        Add("limit", q.Limit.ToString(inv));

        return await GetAsync<List<SupplierProductListItemDto>>("api/supplierproducts?" + string.Join("&", query)) ?? new List<SupplierProductListItemDto>();
    }

    public async Task<List<string>> GetSupplierProductCategoriesAsync(string? supplier)
        => await GetAsync<List<string>>($"api/supplierproducts/categories?supplier={Uri.EscapeDataString(supplier ?? string.Empty)}") ?? new List<string>();

    public async Task<List<SupplierProductListItemDto>> GetSupplierProductsAsync(string? supplier = null, string? search = null)
    {
        var url = "api/supplierproducts";
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(supplier)) query.Add($"supplier={Uri.EscapeDataString(supplier)}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (query.Any()) url += "?" + string.Join("&", query);

        return await GetAsync<List<SupplierProductListItemDto>>(url) ?? new List<SupplierProductListItemDto>();
    }

    public async Task<SupplierProductDetailDto> GetSupplierProductAsync(int id)
    {
        return await GetAsync<SupplierProductDetailDto>($"api/supplierproducts/{id}")
               ?? throw new Exception("Supplier product not found.");
    }

    public async Task<AddSupplierProductsResult> AddSupplierProductsToMyListingsAsync(List<int> productIds, bool allowDuplicates = false)
    {
        return await SendAsync<AddSupplierProductsResult>(HttpMethod.Post, "api/supplierproducts/add-to-my-listings", new AddSupplierProductsRequest
        {
            ProductIds = productIds,
            AllowDuplicates = allowDuplicates
        }) ?? new AddSupplierProductsResult();
    }

    public async Task<ExtractedDetailsDto> ExtractDetailsAsync(string url)
    {
        return await GetAsync<ExtractedDetailsDto>($"api/listings/extract?url={Uri.EscapeDataString(url)}") ?? new ExtractedDetailsDto();
    }

    public async Task<List<EbayAccountDto>> GetEbayAccountsAsync()
    {
        return await GetAsync<List<EbayAccountDto>>("api/auth/accounts") ?? new List<EbayAccountDto>();
    }

    public async Task SetDefaultEbayAccountAsync(int accountId)
    {
        await SendAsync<object>(HttpMethod.Post, $"api/auth/accounts/{accountId}/default");
    }

    public async Task RefreshEbayAccountAsync(int accountId)
    {
        await SendAsync<object>(HttpMethod.Post, $"api/auth/accounts/{accountId}/refresh");
    }

    public async Task DeleteEbayAccountAsync(int accountId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, $"api/auth/accounts/{accountId}");
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);

        using var response = await _http.SendAsync(request);
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var error = await response.Content.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(error))
        {
            throw new Exception(error);
        }

        throw new Exception($"Failed to delete eBay account. Status: {response.StatusCode}");
    }

    public async Task<List<CategoryAspectDto>> GetCategoryAspectsAsync(string categoryId, int? accountId = null)
    {
        var url = $"api/ebaypolicies/category-aspects?categoryId={Uri.EscapeDataString(categoryId)}";
        if (accountId.HasValue) url += $"&accountId={accountId.Value}";
        return await GetAsync<List<CategoryAspectDto>>(url) ?? new List<CategoryAspectDto>();
    }

    public async Task<List<EbayPolicyDto>> GetPaymentPoliciesAsync(int? accountId = null)
    {
        var suffix = accountId.HasValue ? $"?accountId={accountId.Value}" : string.Empty;
        return await GetAsync<List<EbayPolicyDto>>($"api/ebaypolicies/payment{suffix}") ?? new List<EbayPolicyDto>();
    }

    public async Task<List<EbayPolicyDto>> GetFulfillmentPoliciesAsync(int? accountId = null)
    {
        var suffix = accountId.HasValue ? $"?accountId={accountId.Value}" : string.Empty;
        return await GetAsync<List<EbayPolicyDto>>($"api/ebaypolicies/fulfillment{suffix}") ?? new List<EbayPolicyDto>();
    }

    public async Task<List<EbayPolicyDto>> GetReturnPoliciesAsync(int? accountId = null)
    {
        var suffix = accountId.HasValue ? $"?accountId={accountId.Value}" : string.Empty;
        return await GetAsync<List<EbayPolicyDto>>($"api/ebaypolicies/return{suffix}") ?? new List<EbayPolicyDto>();
    }

    public async Task<Shared.EbayDefaultPoliciesDto> GetDefaultPoliciesAsync()
    {
        return await GetAsync<Shared.EbayDefaultPoliciesDto>("api/ebay/default-policies") ?? new Shared.EbayDefaultPoliciesDto();
    }

    public async Task SetManualTokenAsync(string token, string? accountName = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/auth/manual-token")
        {
            Content = JsonContent.Create(new { Token = token, AccountName = accountName })
        };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync();
            throw new Exception($"Failed to save token. Status: {response.StatusCode}. Details: {error}");
        }
    }

    public async Task<string> GetEbayLoginUrlAsync(string returnUrl = "/", string? accountName = null)
    {
        var url = $"api/auth/login-url?returnUrl={Uri.EscapeDataString(returnUrl)}";
        if (!string.IsNullOrWhiteSpace(accountName))
        {
            url += $"&accountName={Uri.EscapeDataString(accountName)}";
        }
        var response = await GetAsync<LoginUrlResponse>(url);
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
            throw new Exception(await ExtractErrorMessageAsync(response));
        }
        return await response.Content.ReadFromJsonAsync<ListingDto>() ?? throw new Exception("Failed to deserialize response");
    }

    public async Task<List<CategorySuggestionDto>> GetCategorySuggestionsAsync(string title, int? accountId = null)
    {
        var url = $"api/ebaypolicies/category-suggestions?title={Uri.EscapeDataString(title)}";
        if (accountId.HasValue)
        {
            url += $"&accountId={accountId.Value}";
        }
        return await GetAsync<List<CategorySuggestionDto>>(url) ?? new List<CategorySuggestionDto>();
    }

    public async Task<string> StartBulkExtractAsync(string categoryUrl)
    {
        var result = await SendAsync<BulkJobStartedDto>(HttpMethod.Post, "api/listings/bulk-extract", categoryUrl);
        return result?.JobId ?? throw new Exception("Server did not return a job id.");
    }

    public async Task<BulkJobDto> GetBulkJobAsync(string jobId)
    {
        return await GetAsync<BulkJobDto>($"api/listings/bulk-jobs/{Uri.EscapeDataString(jobId)}")
               ?? throw new Exception("Job not found.");
    }

    public async Task<BulkDeleteResult> BulkDeleteAsync(List<int> ids)
    {
        return await SendAsync<BulkDeleteResult>(HttpMethod.Post, "api/listings/bulk-delete", ids) ?? new BulkDeleteResult();
    }

    // ---- Supplier enable/disable, eBay quantity, trade price lists (/supplier-feeds)

    public async Task<List<SupplierOverviewDto>> GetSupplierOverviewAsync()
        => await GetAsync<List<SupplierOverviewDto>>("api/supplierfeeds/suppliers") ?? new List<SupplierOverviewDto>();

    public async Task SetSupplierEnabledAsync(string code, bool enabled)
        => await SendAsync<object>(HttpMethod.Put, $"api/supplierfeeds/suppliers/{Uri.EscapeDataString(code)}/enabled", enabled);

    /// <summary>Starts the background job that sets eBay quantity to 0 (or restores it) for the supplier's listings.</summary>
    public async Task<string> SetSupplierMarketplaceQuantityAsync(string code, bool restore)
    {
        var result = await SendAsync<BulkJobStartedDto>(HttpMethod.Post,
            $"api/supplierfeeds/suppliers/{Uri.EscapeDataString(code)}/marketplace-quantity?restore={restore.ToString().ToLowerInvariant()}");
        return result?.JobId ?? throw new Exception("Server did not return a job id.");
    }

    public async Task<PriceListUploadResultDto> UploadPriceListAsync(string code, Stream file, string fileName)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new StreamContent(file);
        content.Add(fileContent, "file", fileName);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/supplierfeeds/suppliers/{Uri.EscapeDataString(code)}/price-list") { Content = content };
        request.SetBrowserRequestCredentials(BrowserRequestCredentials.Include);
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) throw new Exception(await ExtractErrorMessageAsync(response));
        return await response.Content.ReadFromJsonAsync<PriceListUploadResultDto>() ?? new PriceListUploadResultDto();
    }

    public async Task<List<SupplierCategorySummaryDto>> GetSupplierCategorySummaryAsync(string? supplier, string groupBy = ProductGrouping.SupplierCategory)
        => await GetAsync<List<SupplierCategorySummaryDto>>($"api/supplierproducts/category-summary?supplier={Uri.EscapeDataString(supplier ?? string.Empty)}&groupBy={Uri.EscapeDataString(groupBy)}")
           ?? new List<SupplierCategorySummaryDto>();

    public async Task<List<ProductTagDto>> GetSupplierTagsAsync(string supplier)
        => await GetAsync<List<ProductTagDto>>($"api/supplierproducts/tags?supplier={Uri.EscapeDataString(supplier)}") ?? new List<ProductTagDto>();

    // ---- My categories (/categories)

    public async Task<List<CatalogCategoryDto>> GetCatalogCategoriesAsync()
        => await GetAsync<List<CatalogCategoryDto>>("api/catalogcategories") ?? new List<CatalogCategoryDto>();

    public async Task<CatalogCategoryDto?> CreateCatalogCategoryAsync(SaveCatalogCategoryRequest request)
        => await SendAsync<CatalogCategoryDto>(HttpMethod.Post, "api/catalogcategories", request);

    public async Task UpdateCatalogCategoryAsync(int id, SaveCatalogCategoryRequest request)
        => await SendAsync<object>(HttpMethod.Put, $"api/catalogcategories/{id}", request);

    public async Task DeleteCatalogCategoryAsync(int id)
        => await SendAsync<object>(HttpMethod.Delete, $"api/catalogcategories/{id}");

    public async Task SetChannelCategoryAsync(int id, string channel, ChannelCategoryRequest request)
        => await SendAsync<object>(HttpMethod.Put, $"api/catalogcategories/{id}/channels/{Uri.EscapeDataString(channel)}", request);

    public async Task<List<SupplierCategorySourceDto>> GetCategorySourcesAsync(string supplier)
        => await GetAsync<List<SupplierCategorySourceDto>>($"api/catalogcategories/sources?supplier={Uri.EscapeDataString(supplier)}") ?? new List<SupplierCategorySourceDto>();

    public async Task SaveCategoryMappingAsync(SaveCategoryMappingRequest request)
        => await SendAsync<object>(HttpMethod.Put, "api/catalogcategories/mappings", request);

    public async Task SetTagTypeAsync(int tagId, string type)
        => await SendAsync<object>(HttpMethod.Put, $"api/catalogcategories/tags/{tagId}/type", type);

    public async Task<CategoryImportResultDto> ImportCategoriesFromSupplierAsync(ImportSupplierCategoriesRequest request)
        => await SendAsync<CategoryImportResultDto>(HttpMethod.Post, "api/catalogcategories/import-from-supplier", request) ?? new CategoryImportResultDto();

    // ---- Suppliers (VAT + delivery options)

    public async Task<List<SupplierDto>> GetSuppliersAsync(bool includeInactive = false)
        => await GetAsync<List<SupplierDto>>($"api/suppliers?includeInactive={includeInactive.ToString().ToLowerInvariant()}") ?? new List<SupplierDto>();

    public async Task<SupplierDto?> CreateSupplierAsync(SupplierDto supplier)
        => await SendAsync<SupplierDto>(HttpMethod.Post, "api/suppliers", supplier);

    public async Task UpdateSupplierAsync(SupplierDto supplier)
        => await SendAsync<object>(HttpMethod.Put, $"api/suppliers/{supplier.Id}", supplier);

    public async Task<SupplierDeliveryOptionDto?> AddDeliveryOptionAsync(int supplierId, SupplierDeliveryOptionDto option)
        => await SendAsync<SupplierDeliveryOptionDto>(HttpMethod.Post, $"api/suppliers/{supplierId}/delivery-options", option);

    public async Task UpdateDeliveryOptionAsync(SupplierDeliveryOptionDto option)
        => await SendAsync<object>(HttpMethod.Put, $"api/suppliers/delivery-options/{option.Id}", option);

    public async Task DeleteDeliveryOptionAsync(int optionId)
        => await SendAsync<object>(HttpMethod.Delete, $"api/suppliers/delivery-options/{optionId}");

    public async Task<ListingSupplierContextDto> GetSupplierContextForListingAsync(int listingId)
        => await GetAsync<ListingSupplierContextDto>($"api/suppliers/for-listing/{listingId}") ?? new ListingSupplierContextDto();

    public async Task<ListingSupplierContextDto> GetSupplierContextForUrlAsync(string? sourceUrl)
        => await GetAsync<ListingSupplierContextDto>($"api/suppliers/for-url?sourceUrl={Uri.EscapeDataString(sourceUrl ?? string.Empty)}") ?? new ListingSupplierContextDto();

    public async Task<EnrichResultDto> FetchSupplierDetailsAsync(int listingId)
        => await SendAsync<EnrichResultDto>(HttpMethod.Post, $"api/listings/{listingId}/fetch-supplier-details") ?? new EnrichResultDto();

    public async Task<string> StartBulkPublishAsync(List<int> ids)
    {
        var result = await SendAsync<BulkJobStartedDto>(HttpMethod.Post, "api/listings/bulk-publish", ids);
        return result?.JobId ?? throw new Exception("Server did not return a job id.");
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
