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

    /// <summary>
    /// Sets the available quantity of published listings (inventory item + offer), up to 25 per call.
    /// Returns an error message per SKU that failed (SKUs not in the result succeeded).
    /// </summary>
    Task<Dictionary<string, string>> UpdateQuantitiesAsync(int? accountId, IReadOnlyList<MarketplaceQuantityUpdate> updates);

    /// <summary>
    /// One page of orders (Fulfillment API getOrders) as raw JSON. <paramref name="filter"/> is eBay's filter, e.g.
    /// "creationdate:[2026-06-01T00:00:00.000Z..]" or "lastmodifieddate:[...]".
    /// </summary>
    Task<string> GetOrdersJsonAsync(int accountId, string filter, int offset, int limit = 200);

    /// <summary>The order's shipping fulfillments (tracking number, carrier) as raw JSON.</summary>
    Task<string> GetShippingFulfillmentsJsonAsync(int accountId, string orderId);

    /// <summary>The whole eBay UK category tree (flattened), from the Taxonomy API.</summary>
    Task<List<EbayCategoryNode>> GetCategoryTreeAsync(int? accountId = null);
}

public sealed record EbayCategoryNode(string CategoryId, string? ParentCategoryId, string Name, int Level);

public sealed record MarketplaceQuantityUpdate(string Sku, string OfferId, int Quantity);
