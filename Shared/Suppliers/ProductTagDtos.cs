namespace Shared;

/// <summary>A supplier tag (e.g. Eden "Carbon Filters") with its product count, for the Products page tag filter.</summary>
public class ProductTagDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "type";
    public int ProductCount { get; set; }
}

/// <summary>How the Products page "Categories" view groups products.</summary>
public static class ProductGrouping
{
    public const string SupplierCategory = "supplier";
    public const string Tag = "tag";
}
