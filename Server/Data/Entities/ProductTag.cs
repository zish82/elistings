using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>A supplier's product tag/collection, e.g. Eden "Carbon Filters". Per supplier, because names mean different things.</summary>
public class ProductTag
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// <summary>Lower case, "&amp;" = "and", punctuation removed: "Pots, Tanks &amp; Trays" = "pots tanks and trays".</summary>
    public string NormalizedName { get; set; } = string.Empty;
    /// <summary>"type" (what the product is: used for filtering), "brand", "promo" (Sale, Best Sellers) or "ignore".</summary>
    public string Type { get; set; } = ProductTagTypes.Type;
}

public static class ProductTagTypes
{
    public const string Type = "type";
    public const string Brand = "brand";
    public const string Promo = "promo";
    public const string Ignore = "ignore";
    public static readonly string[] All = { Type, Brand, Promo, Ignore };
}
