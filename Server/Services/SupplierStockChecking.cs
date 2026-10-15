using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>What a stock check needs to know about the product.</summary>
public sealed record SupplierStockQuery(string? SourceUrl, string? SupplierSku, SupplierProduct? Product);

/// <summary>Result of a live stock check. <see cref="StockStatus"/> is InStock / Low / OutOfStock / Unknown.</summary>
public sealed record SupplierStockResult(
    string Supplier,
    bool IsSuccess,
    string StockStatus = "Unknown",
    string? StockText = null,
    int? StockLevel = null,
    decimal? SupplierPrice = null,
    string? ErrorMessage = null);

/// <summary>
/// Strategy for checking current stock at one supplier. Register one per supplier in Program.cs;
/// <see cref="SupplierStockService"/> picks the first that can handle a listing.
/// </summary>
public interface ISupplierStockChecker
{
    bool CanCheck(SupplierStockQuery query);
    Task<SupplierStockResult> CheckAsync(SupplierStockQuery query, CancellationToken ct = default);
}

/// <summary>DropShipMall: reads "Availability" from the public product page.</summary>
public class DropShipMallStockChecker : ISupplierStockChecker
{
    private const int LowStockThreshold = 5;
    private readonly DropShipMallWebsiteClient _website;

    public DropShipMallStockChecker(DropShipMallWebsiteClient website)
    {
        _website = website;
    }

    public bool CanCheck(SupplierStockQuery query)
        => string.Equals(query.Product?.Supplier, DropShipMallWebsiteClient.SupplierCode, StringComparison.OrdinalIgnoreCase)
           || DropShipMallWebsiteClient.IsDropShipMallUrl(query.SourceUrl);

    public async Task<SupplierStockResult> CheckAsync(SupplierStockQuery query, CancellationToken ct = default)
    {
        var sku = query.SupplierSku ?? query.Product?.SupplierSku;
        var page = await _website.FindProductPageAsync(sku, query.Product?.ProductUrl ?? query.SourceUrl, ct);
        if (page == null)
        {
            return new SupplierStockResult(DropShipMallWebsiteClient.SupplierCode, false,
                ErrorMessage: $"Product {sku} was not found on the DropShipMall website.");
        }

        var (status, level) = Interpret(page.Availability);
        // Prices are only shown to logged-in trade accounts, so the feed price is reported instead.
        return new SupplierStockResult(DropShipMallWebsiteClient.SupplierCode, true, status, page.Availability, level, query.Product?.SupplierPrice);
    }

    /// <summary>Maps "More than 150" / "12" / "Out of Stock" to a status and (when exact) a level.</summary>
    internal static (string Status, int? Level) Interpret(string? availability)
    {
        if (string.IsNullOrWhiteSpace(availability)) return ("Unknown", null);
        var text = availability.Trim();

        if (Regex.IsMatch(text, @"out\s+of\s+stock|unavailable|discontinued", RegexOptions.IgnoreCase)) return ("OutOfStock", 0);

        var number = Regex.Match(text, @"\d+");
        if (!number.Success) return ("Unknown", null);

        var value = int.Parse(number.Value);
        var isLowerBound = Regex.IsMatch(text, @"more\s+than|over|\+", RegexOptions.IgnoreCase);
        int? level = isLowerBound ? null : value;

        if (!isLowerBound && value <= 0) return ("OutOfStock", 0);
        if (!isLowerBound && value <= LowStockThreshold) return ("Low", level);
        return ("InStock", level);
    }
}

/// <summary>Eden Horticulture and Safelincs, via the existing website-scraping sync service.</summary>
public class WebsiteStockCheckerAdapter : ISupplierStockChecker
{
    private readonly IEdenSupplierSyncService _legacy;

    public WebsiteStockCheckerAdapter(IEdenSupplierSyncService legacy)
    {
        _legacy = legacy;
    }

    public bool CanCheck(SupplierStockQuery query)
        => query.SourceUrl != null
           && (query.SourceUrl.Contains("edenhorticulture.co.uk", StringComparison.OrdinalIgnoreCase)
               || query.SourceUrl.Contains("safelincs.co.uk", StringComparison.OrdinalIgnoreCase));

    public async Task<SupplierStockResult> CheckAsync(SupplierStockQuery query, CancellationToken ct = default)
    {
        var dto = await _legacy.CheckSourceAsync(query.SourceUrl, query.SupplierSku, "manual");
        return new SupplierStockResult(dto.Supplier, dto.IsSuccess, dto.StockStatus, dto.StockText, null, dto.SupplierPrice, dto.ErrorMessage);
    }
}

/// <summary>
/// Runs a stock check with the right <see cref="ISupplierStockChecker"/>, records it as a snapshot for the
/// listing, and keeps the listing's / product's stock fields up to date.
/// </summary>
public class SupplierStockService
{
    private readonly AppDbContext _context;
    private readonly IEnumerable<ISupplierStockChecker> _checkers;

    public SupplierStockService(AppDbContext context, IEnumerable<ISupplierStockChecker> checkers)
    {
        _context = context;
        _checkers = checkers;
    }

    public bool CanCheck(SupplierStockQuery query) => _checkers.Any(c => c.CanCheck(query));

    public async Task<bool> CanCheckListingAsync(Listing listing)
        => CanCheck(await BuildQueryAsync(listing));

    /// <summary>Checks without saving anything (for listings that haven't been saved yet).</summary>
    public async Task<SupplierStatusDto> CheckAsync(SupplierStockQuery query, CancellationToken ct = default)
    {
        var result = await RunAsync(query, ct);
        return ToDto(0, query.SupplierSku, result, DateTime.UtcNow);
    }

    /// <summary>Checks a saved listing, stores a snapshot and updates stock on the listing and its product.</summary>
    public async Task<SupplierStatusDto> CheckListingAsync(Listing listing, string triggeredBy, CancellationToken ct = default)
    {
        var query = await BuildQueryAsync(listing);
        var result = await RunAsync(query, ct);
        var checkedAt = DateTime.UtcNow;

        _context.SupplierListingSnapshots.Add(new SupplierListingSnapshot
        {
            ListingId = listing.Id,
            Supplier = result.Supplier,
            SupplierSku = query.SupplierSku,
            SupplierPrice = result.SupplierPrice,
            StockStatus = result.StockStatus,
            StockText = result.StockText,
            CheckedAtUtc = checkedAt,
            IsSuccess = result.IsSuccess,
            ErrorMessage = result.ErrorMessage
        });

        if (result.IsSuccess)
        {
            var level = result.StockStatus == "OutOfStock" ? 0 : result.StockLevel;
            if (level.HasValue)
            {
                listing.SourceStockLevel = level;
                if (query.Product != null)
                {
                    query.Product.StockLevel = level;
                    query.Product.UpdatedAtUtc = checkedAt;
                }
            }
        }

        await _context.SaveChangesAsync(ct);
        Console.WriteLine($"[StockCheck] {triggeredBy} listing={listing.Id} supplier={result.Supplier} sku={query.SupplierSku} status={result.StockStatus} ({result.StockText}) ok={result.IsSuccess}");
        return ToDto(listing.Id, query.SupplierSku, result, checkedAt);
    }

    private async Task<SupplierStockResult> RunAsync(SupplierStockQuery query, CancellationToken ct)
    {
        var checker = _checkers.FirstOrDefault(c => c.CanCheck(query));
        if (checker == null)
        {
            return new SupplierStockResult("unknown", false, ErrorMessage: "Stock checks aren't available for this listing's supplier.");
        }

        try
        {
            return await checker.CheckAsync(query, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new SupplierStockResult("unknown", false, ErrorMessage: $"Could not reach the supplier website: {ex.Message}");
        }
    }

    private async Task<SupplierStockQuery> BuildQueryAsync(Listing listing)
    {
        SupplierProduct? product = null;
        if (listing.SupplierProductId.HasValue)
        {
            product = await _context.SupplierProducts.FirstOrDefaultAsync(p => p.Id == listing.SupplierProductId.Value);
        }
        return new SupplierStockQuery(listing.SourceUrl ?? product?.ProductUrl, listing.SourceProductCode ?? product?.SupplierSku, product);
    }

    private static SupplierStatusDto ToDto(int listingId, string? sku, SupplierStockResult result, DateTime checkedAt) => new()
    {
        ListingId = listingId,
        Supplier = result.Supplier,
        SupplierSku = sku,
        SupplierPrice = result.SupplierPrice,
        StockStatus = result.StockStatus,
        StockText = result.StockText,
        StockLevel = result.StockStatus == "OutOfStock" ? 0 : result.StockLevel,
        LastCheckedUtc = checkedAt,
        IsSuccess = result.IsSuccess,
        ErrorMessage = result.ErrorMessage
    };
}
