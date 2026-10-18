using Shared;

namespace Server.Services;

public interface IEbayService
{
    Task<EbayListingResponse> CreateListingAsync(ListingDto listing);
    Task<string> GetOAuthTokenAsync(int? accountId = null);
    Task RefreshOAuthTokenAsync(int accountId);
    
    Task<List<EbayPolicyDto>> GetPaymentPoliciesAsync(int? accountId = null);
    Task<List<EbayPolicyDto>> GetFulfillmentPoliciesAsync(int? accountId = null);
    Task<List<EbayPolicyDto>> GetReturnPoliciesAsync(int? accountId = null);
    
    Task<List<CategorySuggestionDto>> GetCategorySuggestionsAsync(string title, int? accountId = null);
    /// <summary>Item specifics eBay defines for a category (required first). Cached; throws <see cref="EbayApiException"/> on failure.</summary>
    Task<List<CategoryAspectDto>> GetCategoryAspectsAsync(string categoryId, int? accountId = null);
    Task<System.Text.Json.Nodes.JsonNode?> GetFeeEstimateAsync(Shared.ListingDto listing);
}
