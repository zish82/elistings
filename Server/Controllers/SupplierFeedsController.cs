using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SupplierFeedsController : ControllerBase
{
    private readonly AppDbContext _context;

    public SupplierFeedsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<ActionResult<List<SupplierFeedDto>>> GetAll()
    {
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
                IsRecommended = f.IsRecommended,
                CreatedAtUtc = f.CreatedAtUtc
            })
            .ToListAsync();

        return Ok(feeds);
    }

    [HttpPost]
    public async Task<ActionResult<SupplierFeedDto>> Create([FromBody] CreateSupplierFeedRequest request)
    {
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
            IsRecommended = entity.IsRecommended,
            CreatedAtUtc = entity.CreatedAtUtc
        });
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var feed = await _context.SupplierFeeds.FirstOrDefaultAsync(f => f.Id == id);
        if (feed == null)
        {
            return NotFound();
        }

        _context.SupplierFeeds.Remove(feed);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
