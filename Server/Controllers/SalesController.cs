using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Services;
using Server.Services.Jobs;
using Shared;

namespace Server.Controllers;

/// <summary>Sales report (/sales): eBay orders with cost of sales, earnings, profit and ROI, by month.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SalesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public SalesController(AppDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    /// <summary>Orders this user may see: their own; Managers/Admins see everyone's.</summary>
    private async Task<IQueryable<EbayOrder>> VisibleOrders()
        => _context.EbayOrders.VisibleTo(await UserScope.VisibleUserIdsAsync(_context, _currentUserService));

    [HttpGet]
    public async Task<ActionResult<SalesReportDto>> Get([FromQuery] string? month, [FromServices] EbayOrderSyncService sync)
    {
        if (_currentUserService.UserId == null) return Unauthorized();
        var orders = await (await VisibleOrders()).AsNoTracking().OrderByDescending(o => o.CreatedAtUtc).ToListAsync();

        var report = new SalesReportDto
        {
            Months = SalesReport.Months(orders),
            HistoryDays = sync.HistoryDays
        };
        report.Month = string.IsNullOrWhiteSpace(month) ? report.Months.FirstOrDefault()?.Month : month;

        var shown = orders.Where(o => SalesReport.MonthOf(o.CreatedAtUtc) == report.Month).ToList();
        report.Orders = await ToDtosAsync(shown);

        var accounts = await sync.AccountsForAsync(_currentUserService);
        report.LastSyncedAtUtc = accounts.Max(a => a.OrdersLastSyncedAtUtc);
        report.AccountsNeedingReconnect = accounts
            .Where(a => a.RefreshToken != "MANUAL" && !EbayScopes.CanReadOrders(a.Scopes))
            .Select(a => a.Name)
            .ToList();
        return Ok(report);
    }

    /// <summary>Saves what you type in: mail, supplier order number, supplier price (or back to automatic).</summary>
    [HttpPut("{id:int}")]
    public async Task<ActionResult<SalesOrderDto>> Update(int id, [FromBody] UpdateSalesOrderRequest request)
    {
        var order = await (await VisibleOrders()).FirstOrDefaultAsync(o => o.Id == id);
        if (order == null) return NotFound();
        if (request.SupplierPrice is < 0m) return BadRequest("The supplier price can't be negative.");

        order.SourceEmail = Clean(request.Mail, 200);
        order.SourceOrderId = Clean(request.SourceOrderId, 100);
        if (request.ResetSupplierPrice)
        {
            order.SupplierPriceIsManual = false;
            order.Supplier = null;
            var lines = await _context.EbayOrderLines.Where(l => l.EbayOrderId == order.Id).ToListAsync();
            foreach (var line in lines) { line.UnitCost = null; line.DeliveryCost = null; } // re-match with today's costs
            var matcher = await OrderCostMatcher.CreateAsync(_context, order.OwnerUserId);
            await matcher.ApplyAsync(order, lines);
        }
        else if (request.SupplierPrice.HasValue)
        {
            order.SupplierPrice = Math.Round(request.SupplierPrice.Value, 2, MidpointRounding.AwayFromZero);
            order.SupplierPriceIsManual = true;
        }
        await _context.SaveChangesAsync();
        return Ok((await ToDtosAsync(new List<EbayOrder> { order })).Single());
    }

    /// <summary>Imports new/changed orders from eBay now (background job).</summary>
    [HttpPost("sync")]
    public async Task<ActionResult<BulkJobStartedDto>> Sync([FromServices] BulkJobService jobs)
    {
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();
        var latest = await jobs.FindLatestAsync("sales-sync", userId.Value.ToString(CultureInfo.InvariantCulture));
        if (BulkJobService.IsActive(latest)) return Ok(new BulkJobStartedDto { JobId = latest!.Id });
        return Ok(new BulkJobStartedDto { JobId = await jobs.EnqueueSalesSyncAsync(userId.Value) });
    }

    /// <summary>The month (or everything) as CSV, for Excel.</summary>
    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? month)
    {
        var orders = await (await VisibleOrders()).AsNoTracking().OrderBy(o => o.CreatedAtUtc).ToListAsync();
        if (!string.IsNullOrWhiteSpace(month)) orders = orders.Where(o => SalesReport.MonthOf(o.CreatedAtUtc) == month).ToList();
        var rows = await ToDtosAsync(orders);

        var csv = new StringBuilder();
        csv.AppendLine("Date,Mail,eBay Order ID,Source Order ID,Sold Price,Supplier Price,Earning,Profit,ROI,Items,Status,Tracking,Delivery Company,Refunded,eBay Fees,Fees,Supplier,Buyer");
        foreach (var r in rows)
        {
            csv.AppendLine(string.Join(",", new[]
            {
                SalesReport.ToLocal(r.DateUtc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                Csv(r.Mail), Csv(r.EbayOrderId), Csv(r.SourceOrderId),
                Money(r.SaleTotal), Money(r.SupplierPrice), Money(r.Earning), Money(r.Profit),
                r.RoiPercent.HasValue ? r.RoiPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "",
                Csv(r.Items), Csv(r.Status), Csv(r.Tracking), Csv(r.DeliveryCompany),
                Money(r.Refunded), Money(r.Fees), r.FeesAreActual ? "actual" : "estimated", Csv(r.Supplier), Csv(r.Buyer)
            }));
        }
        var name = $"ebay-sales-{(string.IsNullOrWhiteSpace(month) ? "all" : month)}.csv";
        return File(Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(csv.ToString())).ToArray(), "text/csv", name);
    }

    private async Task<List<SalesOrderDto>> ToDtosAsync(List<EbayOrder> orders)
    {
        var ids = orders.Select(o => o.Id).ToList();
        var lines = (await _context.EbayOrderLines.AsNoTracking().Where(l => ids.Contains(l.EbayOrderId)).ToListAsync()).ToLookup(l => l.EbayOrderId);
        var accountIds = orders.Select(o => o.EbayAccountId).Distinct().ToList();
        var accounts = await _context.EbayTokens.AsNoTracking().Where(t => accountIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name);
        var ownerIds = orders.Select(o => o.OwnerUserId).Distinct().ToList();
        var owners = await _context.Users.AsNoTracking().Where(u => ownerIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email);
        return orders.Select(o => SalesReport.ToDto(o, lines[o.Id].ToList(), accounts.GetValueOrDefault(o.EbayAccountId), owners.GetValueOrDefault(o.OwnerUserId))).ToList();
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }

    private static string Money(decimal? value) => value.HasValue ? value.Value.ToString("0.00", CultureInfo.InvariantCulture) : "";

    /// <summary>CSV-safe text (quotes; and a leading ' for values Excel would treat as formulas).</summary>
    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        if ("=+-@".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

/// <summary>Earnings / profit / ROI and month totals for the sales report.</summary>
public static class SalesReport
{
    private static readonly TimeZoneInfo UkTime = FindUkTime();

    private static TimeZoneInfo FindUkTime()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/London"); }
        catch (Exception ex)
        {
            Console.WriteLine($"[Sales] UK time zone not found ({ex.Message}); months are grouped in UTC. Install tzdata.");
            return TimeZoneInfo.Utc;
        }
    }

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), UkTime);

    /// <summary>"2026-10" in UK time.</summary>
    public static string MonthOf(DateTime utc) => ToLocal(utc).ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>What eBay pays you for the order: sale total − refunds − fees (cancelled orders: £0).</summary>
    public static decimal Earning(EbayOrder o) => EbayOrderSyncService.IsCancelled(o) ? 0m : o.SaleTotal - o.RefundedTotal - (o.Fees ?? 0m);

    public static decimal? Profit(EbayOrder o) => o.SupplierPrice.HasValue ? Earning(o) - o.SupplierPrice.Value : null;

    public static decimal? RoiPercent(decimal? profit, decimal? supplierPrice)
        => profit.HasValue && supplierPrice is > 0m ? Math.Round(profit.Value / supplierPrice.Value * 100m, 1) : null;

    public static SalesOrderDto ToDto(EbayOrder o, List<EbayOrderLine> lines, string? accountName, string? ownerEmail)
    {
        var profit = Profit(o);
        return new SalesOrderDto
        {
            Id = o.Id,
            DateUtc = o.CreatedAtUtc,
            Mail = o.SourceEmail,
            EbayOrderId = o.OrderId,
            SourceOrderId = o.SourceOrderId,
            SupplierPrice = o.SupplierPrice,
            SupplierPriceIsManual = o.SupplierPriceIsManual,
            Earning = Earning(o),
            Profit = profit,
            RoiPercent = RoiPercent(profit, o.SupplierPrice),
            Tracking = o.TrackingNumber,
            DeliveryCompany = o.DeliveryCompany,
            Items = string.Join("; ", lines.Select(l => (l.Quantity > 1 ? $"{l.Quantity} × " : "") + l.Title + (string.IsNullOrWhiteSpace(l.Sku) ? "" : $" [{l.Sku}]"))),
            Supplier = o.Supplier,
            SaleTotal = o.SaleTotal,
            Refunded = o.RefundedTotal,
            Fees = o.Fees,
            FeesAreActual = o.FeesAreActual,
            Status = Status(o),
            Buyer = o.BuyerUsername,
            AccountName = accountName,
            OwnerEmail = ownerEmail
        };
    }

    public static string Status(EbayOrder o)
    {
        if (EbayOrderSyncService.IsCancelled(o)) return "Cancelled";
        if (o.RefundedTotal > 0 && o.RefundedTotal >= o.SaleTotal) return "Refunded";
        if (o.RefundedTotal > 0) return "Part refunded";
        return o.FulfillmentStatus switch
        {
            "FULFILLED" => "Dispatched",
            "IN_PROGRESS" => "Part dispatched",
            "NOT_STARTED" => "To dispatch",
            _ => o.FulfillmentStatus ?? "-"
        };
    }

    public static List<SalesMonthDto> Months(IEnumerable<EbayOrder> orders)
        => orders
            .GroupBy(o => MonthOf(o.CreatedAtUtc))
            .Select(g =>
            {
                var costed = g.Where(o => o.SupplierPrice.HasValue).ToList();
                var supplier = costed.Sum(o => o.SupplierPrice!.Value);
                var profit = costed.Sum(o => Profit(o)!.Value);
                return new SalesMonthDto
                {
                    Month = g.Key,
                    Orders = g.Count(o => !EbayOrderSyncService.IsCancelled(o)),
                    Sales = g.Where(o => !EbayOrderSyncService.IsCancelled(o)).Sum(o => o.SaleTotal - o.RefundedTotal),
                    Fees = g.Sum(o => o.Fees ?? 0m),
                    Earning = g.Sum(Earning),
                    SupplierPrice = supplier,
                    Profit = profit,
                    RoiPercent = RoiPercent(costed.Count > 0 ? profit : null, supplier),
                    OrdersWithoutCost = g.Count(o => !o.SupplierPrice.HasValue)
                };
            })
            .OrderByDescending(m => m.Month)
            .ToList();
}
