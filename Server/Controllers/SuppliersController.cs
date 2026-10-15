using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Services;
using Shared;

namespace Server.Controllers;

/// <summary>Supplier VAT settings and delivery options (managed on /suppliers).</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SuppliersController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly SupplierCatalogService _catalog;
    private readonly ICurrentUserService _currentUserService;

    public SuppliersController(AppDbContext context, SupplierCatalogService catalog, ICurrentUserService currentUserService)
    {
        _context = context;
        _catalog = catalog;
        _currentUserService = currentUserService;
    }

    private bool CanEdit => _currentUserService.CanManageUsers || _currentUserService.CanViewAllListings;

    [HttpGet]
    public async Task<ActionResult<List<SupplierDto>>> GetAll([FromQuery] bool includeInactive = false)
        => Ok(await _catalog.GetAllAsync(includeInactive));

    /// <summary>Supplier + delivery options + suggested option for a listing (used by the listing editor).</summary>
    [HttpGet("for-listing/{listingId:int}")]
    public async Task<ActionResult<ListingSupplierContextDto>> ForListing(int listingId, [FromServices] SupplierStockService stockService)
    {
        var listing = await _context.Listings.FirstOrDefaultAsync(l => l.Id == listingId);
        if (listing == null) return NotFound();
        if (!_currentUserService.CanViewAllListings && listing.OwnerUserId != _currentUserService.UserId) return NotFound();

        var context = await _catalog.GetContextForListingAsync(listing);
        context.SupportsStockCheck = await stockService.CanCheckListingAsync(listing);
        return Ok(context);
    }

    /// <summary>Same as above for a listing that hasn't been saved yet (lookup by source URL).</summary>
    [HttpGet("for-url")]
    public async Task<ActionResult<ListingSupplierContextDto>> ForUrl([FromQuery] string? sourceUrl, [FromServices] SupplierStockService stockService)
    {
        var supplier = await _catalog.FindAsync(null, sourceUrl);
        var context = _catalog.BuildContext(supplier, null);
        context.SupportsStockCheck = stockService.CanCheck(new SupplierStockQuery(sourceUrl, null, null));
        return Ok(context);
    }

    [HttpPost]
    public async Task<ActionResult<SupplierDto>> Create([FromBody] SupplierDto dto)
    {
        if (!CanEdit) return Forbid();
        if (string.IsNullOrWhiteSpace(dto.Code) || string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Code and Name are required.");
        var code = dto.Code.Trim();
        if (await _context.Suppliers.AnyAsync(s => s.Code.ToLower() == code.ToLower())) return Conflict($"Supplier '{code}' already exists.");

        var entity = new Supplier();
        Apply(entity, dto);
        entity.Code = code;
        _context.Suppliers.Add(entity);
        await _context.SaveChangesAsync();
        return Ok(SupplierCatalogService.ToDto(entity, Array.Empty<SupplierDeliveryOption>()));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SupplierDto dto)
    {
        if (!CanEdit) return Forbid();
        var entity = await _context.Suppliers.FirstOrDefaultAsync(s => s.Id == id);
        if (entity == null) return NotFound();
        if (string.IsNullOrWhiteSpace(dto.Name)) return BadRequest("Name is required.");
        Apply(entity, dto); // Code is the link to SupplierProducts.Supplier, so it is not changed here.
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{supplierId:int}/delivery-options")]
    public async Task<ActionResult<SupplierDeliveryOptionDto>> AddOption(int supplierId, [FromBody] SupplierDeliveryOptionDto dto)
    {
        if (!CanEdit) return Forbid();
        if (!await _context.Suppliers.AnyAsync(s => s.Id == supplierId)) return NotFound();
        var error = Validate(dto);
        if (error != null) return BadRequest(error);

        var entity = new SupplierDeliveryOption { SupplierId = supplierId };
        Apply(entity, dto);
        _context.SupplierDeliveryOptions.Add(entity);
        await EnsureSingleDefaultAsync(entity);
        await _context.SaveChangesAsync();
        return Ok(SupplierCatalogService.ToDto(entity));
    }

    [HttpPut("delivery-options/{id:int}")]
    public async Task<IActionResult> UpdateOption(int id, [FromBody] SupplierDeliveryOptionDto dto)
    {
        if (!CanEdit) return Forbid();
        var entity = await _context.SupplierDeliveryOptions.FirstOrDefaultAsync(o => o.Id == id);
        if (entity == null) return NotFound();
        var error = Validate(dto);
        if (error != null) return BadRequest(error);

        Apply(entity, dto);
        await EnsureSingleDefaultAsync(entity);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("delivery-options/{id:int}")]
    public async Task<IActionResult> DeleteOption(int id)
    {
        if (!CanEdit) return Forbid();
        var entity = await _context.SupplierDeliveryOptions.FirstOrDefaultAsync(o => o.Id == id);
        if (entity == null) return NotFound();
        _context.SupplierDeliveryOptions.Remove(entity);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    private async Task EnsureSingleDefaultAsync(SupplierDeliveryOption changed)
    {
        if (!changed.IsDefault) return;
        var others = await _context.SupplierDeliveryOptions
            .Where(o => o.SupplierId == changed.SupplierId && o.Id != changed.Id && o.IsDefault)
            .ToListAsync();
        foreach (var o in others) o.IsDefault = false;
    }

    private static string? Validate(SupplierDeliveryOptionDto dto)
    {
        if (string.IsNullOrWhiteSpace(dto.Name)) return "Name is required.";
        if (dto.Cost < 0) return "Cost cannot be negative.";
        return null;
    }

    private static void Apply(Supplier entity, SupplierDto dto)
    {
        entity.Name = dto.Name.Trim();
        entity.WebsiteHost = string.IsNullOrWhiteSpace(dto.WebsiteHost) ? null : dto.WebsiteHost.Trim().ToLowerInvariant().Replace("www.", "");
        entity.SkuPrefix = SkuService.Normalize(dto.SkuPrefix);
        entity.PricesIncludeVat = dto.PricesIncludeVat;
        entity.VatRatePercent = dto.VatRatePercent < 0 ? 0 : dto.VatRatePercent;
        entity.DefaultMarginPercent = dto.DefaultMarginPercent < 0 ? 0 : dto.DefaultMarginPercent;
        entity.IsActive = dto.IsActive;
    }

    private static void Apply(SupplierDeliveryOption entity, SupplierDeliveryOptionDto dto)
    {
        entity.Name = dto.Name.Trim();
        entity.Description = string.IsNullOrWhiteSpace(dto.Description) ? null : dto.Description.Trim();
        entity.PostageCategories = string.IsNullOrWhiteSpace(dto.PostageCategories) ? null : dto.PostageCategories.Trim();
        entity.Cost = dto.Cost;
        entity.CostIncludesVat = dto.CostIncludesVat;
        entity.IsDefault = dto.IsDefault;
        entity.SortOrder = dto.SortOrder;
        entity.IsActive = dto.IsActive;
    }
}
