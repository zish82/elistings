using Shared;

namespace Server.Services;

public interface IEbayService
{
    Task<EbayListingResponse> CreateListingAsync(ListingDto listing);
    Task<string> GetOAuthTokenAsync();
    
    Task<List<EbayPolicyDto>> GetPaymentPoliciesAsync();
    Task<List<EbayPolicyDto>> GetFulfillmentPoliciesAsync();
    Task<List<EbayPolicyDto>> GetReturnPoliciesAsync();
    
    Task<List<CategorySuggestionDto>> GetCategorySuggestionsAsync(string title);
    Task<System.Text.Json.Nodes.JsonNode?> GetFeeEstimateAsync(Shared.ListingDto listing);
}
