using Shared;

namespace Server.Services;

public interface IEbayService
{
    Task<EbayListingResponse> CreateListingAsync(ListingDto listing);
    Task<string> GetOAuthTokenAsync(int? accountId = null);
    
    Task<List<EbayPolicyDto>> GetPaymentPoliciesAsync(int? accountId = null);
    Task<List<EbayPolicyDto>> GetFulfillmentPoliciesAsync(int? accountId = null);
    Task<List<EbayPolicyDto>> GetReturnPoliciesAsync(int? accountId = null);
    
    Task<List<CategorySuggestionDto>> GetCategorySuggestionsAsync(string title, int? accountId = null);
    Task<System.Text.Json.Nodes.JsonNode?> GetFeeEstimateAsync(Shared.ListingDto listing);
}
