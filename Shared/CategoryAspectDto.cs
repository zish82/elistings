namespace Shared;

/// <summary>An item specific ("aspect") eBay defines for a category, from the Taxonomy API.</summary>
public class CategoryAspectDto
{
    public string Name { get; set; } = string.Empty;
    /// <summary>eBay rejects the listing if a required aspect is missing.</summary>
    public bool Required { get; set; }
    /// <summary>"RECOMMENDED" or "OPTIONAL" (eBay's aspectUsage).</summary>
    public string Usage { get; set; } = "OPTIONAL";
    /// <summary>True when only the listed <see cref="Values"/> are accepted (eBay's SELECTION_ONLY).</summary>
    public bool SelectionOnly { get; set; }
    /// <summary>True when more than one value is allowed.</summary>
    public bool MultipleValues { get; set; }
    /// <summary>eBay's suggested / allowed values (may be empty for free-text aspects).</summary>
    public List<string> Values { get; set; } = new();
}
