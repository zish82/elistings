namespace Server.Services;

/// <summary>
/// OAuth scopes the app asks eBay for. A refresh must use the scopes the account was connected with (eBay rejects
/// extra ones), so each account stores its own; accounts connected before orders/finances were added keep the original set
/// until they're reconnected.
/// </summary>
public static class EbayScopes
{
    private const string Prefix = "https://api.ebay.com/oauth/api_scope";

    /// <summary>What accounts connected before the sales report had.</summary>
    public const string Original = Prefix + " " + Prefix + "/sell.inventory " + Prefix + "/sell.account";

    /// <summary>Listing + reading orders (sales report) + finances (actual fees, payouts).</summary>
    public const string Current = Original + " " + Prefix + "/sell.fulfillment.readonly " + Prefix + "/sell.finances";

    public static bool CanReadOrders(string? scopes)
        => (scopes ?? Original).Split(' ').Any(s => s is Prefix + "/sell.fulfillment.readonly" or Prefix + "/sell.fulfillment");
}
