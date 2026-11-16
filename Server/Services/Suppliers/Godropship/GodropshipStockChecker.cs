namespace Server.Services;

/// <summary>Godropship: reads "UK Stock" and the price from the public product page.</summary>
public class GodropshipStockChecker : ISupplierStockChecker
{
    private const int LowStockThreshold = 5;
    private readonly GodropshipWebsiteClient _website;

    public GodropshipStockChecker(GodropshipWebsiteClient website)
    {
        _website = website;
    }

    public bool CanCheck(SupplierStockQuery query)
        => string.Equals(query.Product?.Supplier, GodropshipWebsiteClient.SupplierCode, StringComparison.OrdinalIgnoreCase)
           || GodropshipPageParser.IsGodropshipUrl(query.SourceUrl);

    public async Task<SupplierStockResult> CheckAsync(SupplierStockQuery query, CancellationToken ct = default)
    {
        var url = query.Product?.ProductUrl ?? query.SourceUrl;
        if (!GodropshipPageParser.IsGodropshipUrl(url))
            return new SupplierStockResult(GodropshipWebsiteClient.SupplierCode, false, ErrorMessage: "No Godropship product page is known for this listing.");

        GodropshipPage page;
        try
        {
            page = await _website.GetProductAsync(url!, ct);
        }
        catch (InvalidOperationException ex)
        {
            return new SupplierStockResult(GodropshipWebsiteClient.SupplierCode, false, ErrorMessage: ex.Message);
        }
        if (!page.IsProduct)
            return new SupplierStockResult(GodropshipWebsiteClient.SupplierCode, false, ErrorMessage: "The Godropship page no longer shows this product.");

        var (status, level, text) = Interpret(page);
        // The page price is Godropship's live price (what we pay), so it's saved back like a trade price.
        return new SupplierStockResult(GodropshipWebsiteClient.SupplierCode, true, status, text, level, page.Price, IsTradePrice: page.Price.HasValue);
    }

    internal static (string Status, int? Level, string Text) Interpret(GodropshipPage page)
    {
        if (page.StockLevel.HasValue)
        {
            var level = page.StockLevel.Value;
            var text = $"UK Stock: {level}";
            if (level <= 0) return ("OutOfStock", 0, text);
            return (level <= LowStockThreshold ? "Low" : "InStock", level, text);
        }
        return page.InStock ? ("InStock", null, "In stock") : ("OutOfStock", 0, "Out of stock");
    }
}
