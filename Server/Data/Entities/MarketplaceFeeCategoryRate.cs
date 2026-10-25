using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>Final value fee % for one marketplace category (overrides the default).</summary>
public class MarketplaceFeeCategoryRate
{
    public int Id { get; set; }
    public string Marketplace { get; set; } = "eBay";
    public string CategoryId { get; set; } = string.Empty;
    public string? CategoryName { get; set; }
    /// <summary>% up to <see cref="Threshold1"/> (or on the whole sale).</summary>
    public decimal FinalValueFeePercent { get; set; }
    public decimal? Threshold1 { get; set; }
    public decimal? Percent2 { get; set; }
    public decimal? Threshold2 { get; set; }
    public decimal? Percent3 { get; set; }
    /// <summary>Reduced per-order fee for small orders in this category (e.g. 10p).</summary>
    public decimal? PerOrderFeeSmall { get; set; }
}
