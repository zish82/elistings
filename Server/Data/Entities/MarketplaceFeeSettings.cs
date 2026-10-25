using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

/// <summary>Selling-fee settings per marketplace (one row for "eBay"); see Shared.MarketplaceFeeSettingsDto.</summary>
public class MarketplaceFeeSettings
{
    public int Id { get; set; }
    public string Marketplace { get; set; } = "eBay";
    public decimal FinalValueFeePercent { get; set; } = 12.9m;
    public decimal PerOrderFeeSmall { get; set; } = 0.30m;
    public decimal PerOrderFeeLarge { get; set; } = 0.40m;
    public decimal PerOrderFeeThreshold { get; set; } = 10m;
    public decimal RegulatoryFeePercent { get; set; } = 0.35m;
    public bool VatRegistered { get; set; }
    public decimal VatRatePercent { get; set; } = 20m;
    public decimal MinProfit { get; set; } = 1m;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
