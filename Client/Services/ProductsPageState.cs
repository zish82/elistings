namespace Client.Services;

/// <summary>
/// Remembers the Products page (supplier, filters, view, open categories, scroll position) when you open a product,
/// so "Back to products" (or the browser's Back) returns to exactly the same place. Kept in memory until the app is reloaded.
/// </summary>
public class ProductsPageState
{
    public bool HasState { get; private set; }

    public string Supplier { get; set; } = string.Empty;
    public string Search { get; set; } = string.Empty;
    public string Sort { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public decimal? MinPrice { get; set; }
    public decimal? MaxPrice { get; set; }
    public bool InStockOnly { get; set; }
    public bool HideAdded { get; set; }
    public bool EstimatesOnly { get; set; }
    public int? TagId { get; set; }
    public string? GroupKey { get; set; }
    public string GroupLabel { get; set; } = string.Empty;

    public string View { get; set; } = "list";
    public string Grouping { get; set; } = string.Empty;
    public bool CategoriesInStockOnly { get; set; }
    public List<string> ExpandedCategories { get; set; } = new();

    public double ScrollY { get; set; }

    public void MarkSaved() => HasState = true;
}
