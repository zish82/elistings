using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Server.Services;
using Shared;

namespace Server.Controllers;

/// <summary>My categories (/categories): the tree, supplier → my category mappings, tag types, and channel (eBay) categories.</summary>
[ApiController]
[Route("api/[controller]")]
[Authorize]
public class CatalogCategoriesController : ControllerBase
{
    private const int MaxNameLength = 100;
    private readonly AppDbContext _context;
    private readonly CatalogCategoryService _categories;
    private readonly ICurrentUserService _currentUserService;

    public CatalogCategoriesController(AppDbContext context, CatalogCategoryService categories, ICurrentUserService currentUserService)
    {
        _context = context;
        _categories = categories;
        _currentUserService = currentUserService;
    }

    private bool CanEdit => _currentUserService.CanManageUsers || _currentUserService.CanViewAllListings;
    private ObjectResult Forbidden() => StatusCode(StatusCodes.Status403Forbidden, "Only Admin or Manager users can change categories.");

    [HttpGet]
    public async Task<ActionResult<List<CatalogCategoryDto>>> GetAll() => Ok(await _categories.GetAllAsync());

    [HttpPost]
    public async Task<ActionResult<CatalogCategoryDto>> Create([FromBody] SaveCatalogCategoryRequest request)
    {
        if (!CanEdit) return Forbidden();
        var error = ValidateName(request.Name) ?? await _categories.ValidateParentAsync(null, request.ParentId);
        if (error != null) return BadRequest(error);
        if (await NameTakenAsync(request.Name, request.ParentId, null)) return Conflict($"'{request.Name.Trim()}' already exists there.");

        var category = new CatalogCategory { Name = request.Name.Trim(), ParentId = request.ParentId, SortOrder = request.SortOrder };
        _context.CatalogCategories.Add(category);
        await _context.SaveChangesAsync();
        return Ok((await _categories.GetAllAsync()).First(c => c.Id == category.Id));
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] SaveCatalogCategoryRequest request)
    {
        if (!CanEdit) return Forbidden();
        var category = await _context.CatalogCategories.FirstOrDefaultAsync(c => c.Id == id);
        if (category == null) return NotFound();
        var error = ValidateName(request.Name) ?? await _categories.ValidateParentAsync(id, request.ParentId);
        if (error != null) return BadRequest(error);
        if (await NameTakenAsync(request.Name, request.ParentId, id)) return Conflict($"'{request.Name.Trim()}' already exists there.");

        var depthChanged = category.ParentId != request.ParentId;
        category.Name = request.Name.Trim();
        category.ParentId = request.ParentId;
        category.SortOrder = request.SortOrder;
        await _context.SaveChangesAsync();

        // Moving a category changes depths, which decide between a product's category and tag mappings.
        if (depthChanged)
        {
            foreach (var supplier in await _context.SupplierCategoryMappings.Select(m => m.Supplier).Distinct().ToListAsync())
                await _categories.RecomputeAsync(supplier);
        }
        return NoContent();
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        if (!CanEdit) return Forbidden();
        if (!await _context.CatalogCategories.AnyAsync(c => c.Id == id)) return NotFound();
        await _categories.DeleteAsync(id);
        return NoContent();
    }

    /// <summary>Sets (or with an empty id, removes) the category on a sales channel, e.g. eBay.</summary>
    [HttpPut("{id:int}/channels/{channel}")]
    public async Task<IActionResult> SetChannelCategory(int id, string channel, [FromBody] ChannelCategoryRequest request)
    {
        if (!CanEdit) return Forbidden();
        if (!new[] { SalesChannels.Ebay, SalesChannels.NopCommerce, SalesChannels.Amazon }.Contains(channel)) return BadRequest($"Unknown channel '{channel}'.");
        if (!await _context.CatalogCategories.AnyAsync(c => c.Id == id)) return NotFound();
        var externalId = request.ExternalId?.Trim();
        if (channel == SalesChannels.Ebay && !string.IsNullOrEmpty(externalId) && !externalId.All(char.IsDigit))
            return BadRequest("An eBay category ID is a number, e.g. 46782.");

        var mapping = await _context.ChannelCategoryMappings.FirstOrDefaultAsync(m => m.CatalogCategoryId == id && m.Channel == channel);
        if (string.IsNullOrEmpty(externalId))
        {
            if (mapping != null) _context.ChannelCategoryMappings.Remove(mapping);
        }
        else
        {
            mapping ??= _context.ChannelCategoryMappings.Add(new ChannelCategoryMapping { CatalogCategoryId = id, Channel = channel }).Entity;
            mapping.ExternalId = externalId;
            mapping.ExternalName = string.IsNullOrWhiteSpace(request.ExternalName) ? null : request.ExternalName.Trim();
        }
        await _context.SaveChangesAsync();
        return NoContent();
    }

    // ------------------------------------------------------------------ supplier mappings and tags

    [HttpGet("sources")]
    public async Task<ActionResult<List<SupplierCategorySourceDto>>> GetSources([FromQuery] string? supplier)
        => Ok(string.IsNullOrWhiteSpace(supplier) ? new List<SupplierCategorySourceDto>() : await _categories.GetSourcesAsync(supplier));

    [HttpPut("mappings")]
    public async Task<IActionResult> SaveMapping([FromBody] SaveCategoryMappingRequest request)
    {
        if (!CanEdit) return Forbidden();
        if (string.IsNullOrWhiteSpace(request.Supplier) || string.IsNullOrWhiteSpace(request.SourceValue)
            || (request.SourceType != CategorySourceTypes.Category && request.SourceType != CategorySourceTypes.Tag))
            return BadRequest("Supplier, source type (category/tag) and value are required.");
        if (request.CatalogCategoryId.HasValue && !await _context.CatalogCategories.AnyAsync(c => c.Id == request.CatalogCategoryId.Value))
            return BadRequest("That category doesn't exist.");

        await _categories.SaveMappingAsync(request);
        return NoContent();
    }

    /// <summary>Sets a tag's type (type / brand / promo / ignore). Only "type" tags are used for filters and categories.</summary>
    [HttpPut("tags/{id:int}/type")]
    public async Task<IActionResult> SetTagType(int id, [FromBody] string type)
    {
        if (!CanEdit) return Forbidden();
        if (!ProductTagTypes.All.Contains(type)) return BadRequest("Type must be type, brand, promo or ignore.");
        var tag = await _context.ProductTags.FirstOrDefaultAsync(t => t.Id == id);
        if (tag == null) return NotFound();
        tag.Type = type;
        await _context.SaveChangesAsync();
        await _categories.RecomputeAsync(tag.Supplier);
        return NoContent();
    }

    /// <summary>Creates my categories from a supplier's categories (and type tags), and maps them.</summary>
    [HttpPost("import-from-supplier")]
    public async Task<ActionResult<CategoryImportResultDto>> ImportFromSupplier([FromBody] ImportSupplierCategoriesRequest request)
    {
        if (!CanEdit) return Forbidden();
        if (string.IsNullOrWhiteSpace(request.Supplier)) return BadRequest("Choose a supplier.");
        if (request.ParentId.HasValue && !await _context.CatalogCategories.AnyAsync(c => c.Id == request.ParentId.Value))
            return BadRequest("The parent category doesn't exist.");
        return Ok(await _categories.ImportFromSupplierAsync(request));
    }

    private static string? ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Give the category a name.";
        if (name.Trim().Length > MaxNameLength) return $"Category names can be at most {MaxNameLength} characters.";
        if (name.Contains('›')) return "Category names can't contain '›' (it separates levels).";
        return null;
    }

    private async Task<bool> NameTakenAsync(string name, int? parentId, int? exceptId)
    {
        var normalized = TagText.Normalize(name);
        var siblings = await _context.CatalogCategories.Where(c => c.ParentId == parentId && c.Id != exceptId).Select(c => c.Name).ToListAsync();
        return siblings.Any(s => TagText.Normalize(s) == normalized);
    }
}
