namespace Shared;

/// <summary>One of "my categories" (flat list; the tree is ParentId + Path).</summary>
public class CatalogCategoryDto
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public int SortOrder { get; set; }
    /// <summary>Full path, e.g. "Garden › Hydroponics › Carbon Filters".</summary>
    public string Path { get; set; } = string.Empty;
    public int Depth { get; set; }
    /// <summary>Products placed directly in this category.</summary>
    public int ProductCount { get; set; }
    /// <summary>eBay category (ChannelCategoryMapping "eBay"), when mapped.</summary>
    public string? EbayCategoryId { get; set; }
    public string? EbayCategoryName { get; set; }
}

public class SaveCatalogCategoryRequest
{
    public string Name { get; set; } = string.Empty;
    public int? ParentId { get; set; }
    public int SortOrder { get; set; }
}

public class ChannelCategoryRequest
{
    /// <summary>Empty removes the mapping.</summary>
    public string? ExternalId { get; set; }
    public string? ExternalName { get; set; }
}

public static class CategorySourceTypes
{
    public const string Category = "category";
    public const string Tag = "tag";
}

/// <summary>A supplier category or tag on the mapping screen.</summary>
public class SupplierCategorySourceDto
{
    /// <summary><see cref="CategorySourceTypes"/>.</summary>
    public string SourceType { get; set; } = string.Empty;
    /// <summary>Category path, or the tag's normalised name (what the mapping stores).</summary>
    public string Value { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public int ProductCount { get; set; }
    public int? TagId { get; set; }
    /// <summary>Tag type (type/brand/promo/ignore); null for categories.</summary>
    public string? TagType { get; set; }
    public int? CatalogCategoryId { get; set; }
}

public class SaveCategoryMappingRequest
{
    public string Supplier { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceValue { get; set; } = string.Empty;
    /// <summary>Null removes the mapping.</summary>
    public int? CatalogCategoryId { get; set; }
}

public class ImportSupplierCategoriesRequest
{
    public string Supplier { get; set; } = string.Empty;
    /// <summary>Create the supplier's categories under this category (null = top level).</summary>
    public int? ParentId { get; set; }
    /// <summary>Also create sub-categories from the supplier's type tags (e.g. Eden "Carbon Filters" under "Ventilation").</summary>
    public bool IncludeTags { get; set; } = true;
}

public class CategoryImportResultDto
{
    public int CategoriesCreated { get; set; }
    public int MappingsCreated { get; set; }
    public int ProductsCategorised { get; set; }
}

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
    public const string MyCategory = "mine";
    public const string Tag = "tag";
}
