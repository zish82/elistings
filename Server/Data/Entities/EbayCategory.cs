using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>eBay UK category tree (downloaded from eBay's Taxonomy API), used to find a category's top-level fee rate.</summary>
public class EbayCategory
{
    public int Id { get; set; }
    public string CategoryId { get; set; } = string.Empty;
    public string? ParentCategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
