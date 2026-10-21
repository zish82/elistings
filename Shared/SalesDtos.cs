namespace Shared;

/// <summary>One row of the sales report (an eBay order).</summary>
public class SalesOrderDto
{
    public int Id { get; set; }
    public DateTime DateUtc { get; set; }
    /// <summary>Email account used to place the supplier order (typed in).</summary>
    public string? Mail { get; set; }
    public string EbayOrderId { get; set; } = string.Empty;
    /// <summary>The supplier's order number (typed in).</summary>
    public string? SourceOrderId { get; set; }
    /// <summary>What the supplier charges for the order (items inc VAT + delivery). Null = unknown.</summary>
    public decimal? SupplierPrice { get; set; }
    public bool SupplierPriceIsManual { get; set; }
    /// <summary>What eBay pays you: sale total − refunds − eBay fees.</summary>
    public decimal Earning { get; set; }
    public decimal? Profit { get; set; }
    /// <summary>Profit ÷ supplier price, as a %.</summary>
    public decimal? RoiPercent { get; set; }
    public string? Tracking { get; set; }
    public string? DeliveryCompany { get; set; }

    // extra detail
    public string? Items { get; set; }
    public string? Supplier { get; set; }
    public decimal SaleTotal { get; set; }
    public decimal Refunded { get; set; }
    public decimal? Fees { get; set; }
    public bool FeesAreActual { get; set; }
    public string? Status { get; set; }
    public string? Buyer { get; set; }
    public string? AccountName { get; set; }
    public string? OwnerEmail { get; set; }
}

/// <summary>Totals for one month (orders' local date).</summary>
public class SalesMonthDto
{
    /// <summary>"2026-10".</summary>
    public string Month { get; set; } = string.Empty;
    public int Orders { get; set; }
    public decimal Sales { get; set; }
    public decimal Fees { get; set; }
    public decimal Earning { get; set; }
    /// <summary>Supplier price of the orders where it's known.</summary>
    public decimal SupplierPrice { get; set; }
    public decimal Profit { get; set; }
    public decimal? RoiPercent { get; set; }
    /// <summary>Orders without a known supplier price (left out of profit/ROI).</summary>
    public int OrdersWithoutCost { get; set; }
}

public class SalesReportDto
{
    public List<SalesMonthDto> Months { get; set; } = new();
    /// <summary>The month shown in <see cref="Orders"/>.</summary>
    public string? Month { get; set; }
    public List<SalesOrderDto> Orders { get; set; } = new();
    public int HistoryDays { get; set; }
    public DateTime? LastSyncedAtUtc { get; set; }
    /// <summary>Connected accounts that must be reconnected before their orders can be read.</summary>
    public List<string> AccountsNeedingReconnect { get; set; } = new();
}

public class UpdateSalesOrderRequest
{
    public string? Mail { get; set; }
    public string? SourceOrderId { get; set; }
    /// <summary>Set to enter the supplier price yourself; with <see cref="ResetSupplierPrice"/> it goes back to automatic.</summary>
    public decimal? SupplierPrice { get; set; }
    public bool ResetSupplierPrice { get; set; }
}
