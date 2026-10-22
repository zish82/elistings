using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Services;
using Server.Services.Jobs;
using Shared;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SupplierFeedsController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly ICurrentUserService _currentUserService;

    public SupplierFeedsController(AppDbContext context, ICurrentUserService currentUserService)
    {
        _context = context;
        _currentUserService = currentUserService;
    }

    private SupplierAccessService Access => new(_context, _currentUserService);

    private bool CanEdit => _currentUserService.CanManageUsers || _currentUserService.CanViewAllListings;

    /// <summary>Suppliers that take an uploaded trade price list (no trade prices on their website).</summary>
    private static readonly string[] PriceListSuppliers = { SafelincsCatalogImportService.Code };

    [HttpGet]
    public async Task<ActionResult<List<SupplierFeedDto>>> GetAll()
    {
        var disabled = await _context.Suppliers.Where(s => !s.IsActive).Select(s => s.Code.ToLower()).ToListAsync();
        var feeds = await _context.SupplierFeeds
            .OrderBy(f => f.Supplier)
            .ThenByDescending(f => f.IsRecommended)
            .ThenBy(f => f.FeedType)
            .Select(f => new SupplierFeedDto
            {
                Id = f.Id,
                Supplier = f.Supplier,
                FeedType = f.FeedType,
                Url = f.Url,
                Description = f.Description,
                HasCookieHeader = !string.IsNullOrWhiteSpace(f.CookieHeader),
                IsRecommended = f.IsRecommended,
                CreatedAtUtc = f.CreatedAtUtc
            })
            .ToListAsync();

        foreach (var feed in feeds) feed.SupplierEnabled = !disabled.Contains(feed.Supplier.ToLower());
        // Only the suppliers this user may see (Admins see all).
        var allowed = await Access.AllowedCodesAsync();
        if (allowed != null) feeds = feeds.Where(f => allowed.Contains(f.Supplier)).ToList();
        return Ok(feeds);
    }

    // ------------------------------------------------------------------ suppliers: enable/disable, price list

    [HttpGet("suppliers")]
    public async Task<ActionResult<List<SupplierOverviewDto>>> GetSuppliers([FromServices] SupplierPriceListService priceLists)
    {
        var allowedCodes = await Access.AllowedCodesAsync();
        var suppliers = (await _context.Suppliers.AsNoTracking().OrderBy(s => s.Name).ToListAsync())
            .Where(s => allowedCodes == null || allowedCodes.Contains(s.Code))
            .ToList();
        var result = new List<SupplierOverviewDto>();
        foreach (var s in suppliers)
        {
            var listings = SupplierCatalogService.ListingsOf(_context.Listings, _context, s.Code, s.WebsiteHost);
            var usesPriceList = PriceListSuppliers.Contains(s.Code, StringComparer.OrdinalIgnoreCase);
            result.Add(new SupplierOverviewDto
            {
                Id = s.Id,
                Code = s.Code,
                Name = s.Name,
                IsActive = s.IsActive,
                Products = await _context.SupplierProducts.CountAsync(p => p.Supplier == s.Code && p.IsActive && p.RemovedFromSupplierAtUtc == null),
                Listings = await listings.CountAsync(l => l.Status != "Deleted"),
                PublishedListings = await listings.CountAsync(l => l.Status == "Published"),
                ZeroedListings = await listings.CountAsync(l => l.Status == "Published" && l.QuantityZeroedAtUtc != null),
                UsesPriceList = usesPriceList,
                PriceList = usesPriceList ? await priceLists.GetStatusAsync(s.Code) : null
            });
        }
        return Ok(result);
    }

    /// <summary>
    /// Enables/disables a supplier. Disabled: no imports or syncs, its products and listings are hidden in the app.
    /// eBay listings are not touched (use marketplace-quantity for that).
    /// </summary>
    [HttpPut("suppliers/{code}/enabled")]
    public async Task<IActionResult> SetEnabled(string code, [FromBody] bool enabled)
    {
        if (!CanEdit) return StatusCode(StatusCodes.Status403Forbidden, SuppliersController.OnlyAdminsMessage);
        var supplier = await _context.Suppliers.FirstOrDefaultAsync(s => s.Code == code);
        if (supplier == null || !await Access.IsAllowedAsync(supplier.Code)) return NotFound();
        supplier.IsActive = enabled;
        await _context.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Sets the eBay quantity of the supplier's published listings to 0, or restores it (background job).</summary>
    [HttpPost("suppliers/{code}/marketplace-quantity")]
    public async Task<ActionResult<BulkJobStartedDto>> SetMarketplaceQuantity(string code, [FromQuery] bool restore, [FromServices] BulkJobService jobs)
    {
        if (!CanEdit) return StatusCode(StatusCodes.Status403Forbidden, SuppliersController.OnlyAdminsMessage);
        var userId = _currentUserService.UserId;
        if (userId == null) return Unauthorized();
        var supplier = await _context.Suppliers.FirstOrDefaultAsync(s => s.Code == code);
        if (supplier == null || !await Access.IsAllowedAsync(supplier.Code)) return NotFound();
        if (restore && !supplier.IsActive) return BadRequest($"Enable {supplier.Name} before restoring its eBay quantities.");

        var latest = await jobs.FindLatestAsync("supplier-quantity", supplier.Code);
        if (BulkJobService.IsActive(latest)) return Ok(new BulkJobStartedDto { JobId = latest!.Id });

        return Ok(new BulkJobStartedDto { JobId = await jobs.EnqueueSupplierQuantityAsync(userId.Value, supplier.Code, restore) });
    }

    /// <summary>Uploads the supplier's trade price list (.xlsx/.csv), replacing the previous one, and re-prices its products.</summary>
    [HttpPost("suppliers/{code}/price-list")]
    [RequestSizeLimit(20_000_000)]
    public async Task<ActionResult<PriceListUploadResultDto>> UploadPriceList(string code, IFormFile file, [FromServices] SupplierPriceListService priceLists)
    {
        if (!CanEdit) return StatusCode(StatusCodes.Status403Forbidden, SuppliersController.OnlyAdminsMessage);
        var supplier = await _context.Suppliers.FirstOrDefaultAsync(s => s.Code == code);
        if (supplier == null || !await Access.IsAllowedAsync(supplier.Code)) return NotFound();
        if (file == null || file.Length == 0) return BadRequest("Choose the price list file to upload.");

        List<PriceListRow> rows;
        List<string> warnings;
        try
        {
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream);
            stream.Position = 0;
            (rows, warnings) = SupplierPriceListParser.Parse(stream, file.FileName);
        }
        catch (Exception ex) // any unreadable/corrupt file (ClosedXML/OpenXML throw several exception types)
        {
            return BadRequest($"Could not read the price list: {ex.Message}");
        }

        if (rows.Count == 0)
        {
            return BadRequest("No prices found in the file. " + string.Join(" ", warnings));
        }

        var result = await priceLists.ReplaceAsync(supplier.Code, rows);
        result.Notes.InsertRange(0, warnings);
        return Ok(result);
    }

    [HttpPost]
    public async Task<ActionResult<SupplierFeedDto>> Create([FromBody] CreateSupplierFeedRequest request)
    {
        if (!CanEdit) return StatusCode(StatusCodes.Status403Forbidden, SuppliersController.OnlyAdminsMessage);
        if (request == null
            || string.IsNullOrWhiteSpace(request.Supplier)
            || string.IsNullOrWhiteSpace(request.FeedType)
            || string.IsNullOrWhiteSpace(request.Url))
        {
            return BadRequest("Supplier, FeedType and Url are required.");
        }

        if (!Uri.TryCreate(request.Url, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest("Url must be a valid http/https URL.");
        }

        var supplier = request.Supplier.Trim();
        if (!await Access.IsAllowedAsync(supplier)) return StatusCode(StatusCodes.Status403Forbidden, "You don't have access to that supplier.");
        var feedType = request.FeedType.Trim();
        var normalizedUrl = request.Url.Trim();

        var exists = await _context.SupplierFeeds.AnyAsync(f =>
            f.Supplier.ToLower() == supplier.ToLower()
            && f.FeedType.ToLower() == feedType.ToLower()
            && f.Url.ToLower() == normalizedUrl.ToLower());

        if (exists)
        {
            return Conflict("This supplier feed already exists.");
        }

        var entity = new SupplierFeed
        {
            Supplier = supplier,
            FeedType = feedType,
            Url = normalizedUrl,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(),
            CookieHeader = string.IsNullOrWhiteSpace(request.CookieHeader) ? null : request.CookieHeader.Trim(),
            IsRecommended = request.IsRecommended,
            CreatedAtUtc = DateTime.UtcNow
        };

        _context.SupplierFeeds.Add(entity);
        await _context.SaveChangesAsync();

        return Ok(new SupplierFeedDto
        {
            Id = entity.Id,
            Supplier = entity.Supplier,
            FeedType = entity.FeedType,
            Url = entity.Url,
            Description = entity.Description,
            HasCookieHeader = !string.IsNullOrWhiteSpace(entity.CookieHeader),
            IsRecommended = entity.IsRecommended,
            CreatedAtUtc = entity.CreatedAtUtc
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!CanEdit) return StatusCode(StatusCodes.Status403Forbidden, SuppliersController.OnlyAdminsMessage);
        var feed = await _context.SupplierFeeds.FirstOrDefaultAsync(f => f.Id == id);
        if (feed == null || !await Access.IsAllowedAsync(feed.Supplier))
        {
            return NotFound();
        }

        _context.SupplierFeeds.Remove(feed);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
