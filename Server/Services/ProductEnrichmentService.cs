using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>
/// Fills in what supplier feeds don't provide, so a listing created from a supplier product is
/// ready to publish: description/specifics/barcode (from the supplier's website), all images,
/// VAT-inclusive suggested price and the default delivery option.
/// </summary>
public class ProductEnrichmentService
{
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromDays(7);

    private readonly AppDbContext _context;
    private readonly IEnumerable<ISupplierProductEnricher> _enrichers;
    private readonly SupplierCatalogService _catalog;

    public ProductEnrichmentService(AppDbContext context, IEnumerable<ISupplierProductEnricher> enrichers, SupplierCatalogService catalog)
    {
        _context = context;
        _enrichers = enrichers;
        _catalog = catalog;
    }

    public bool HasEnricherFor(string? supplierCode)
        => !string.IsNullOrWhiteSpace(supplierCode)
           && _enrichers.Any(e => e.SupplierCode.Equals(supplierCode, StringComparison.OrdinalIgnoreCase));

    /// <summary>Fetches page details for the product (unless fetched recently). Returns a status message.</summary>
    public async Task<(bool Fetched, string Message)> EnrichProductAsync(SupplierProduct product, bool force = false, CancellationToken ct = default)
    {
        var enricher = _enrichers.FirstOrDefault(e => e.SupplierCode.Equals(product.Supplier, StringComparison.OrdinalIgnoreCase));
        if (enricher == null) return (false, $"No product-page fetcher for supplier '{product.Supplier}'.");

        if (!force && product.DetailsFetchedAtUtc.HasValue && DateTime.UtcNow - product.DetailsFetchedAtUtc.Value < RefreshAfter)
        {
            return (false, "Details already up to date.");
        }

        var details = await enricher.FetchAsync(product, ct);
        if (details == null)
        {
            // Not marked as fetched, so the next listing creation / button press retries.
            return (false, $"Product page not found on supplier website for SKU {product.SupplierSku}.");
        }
        product.DetailsFetchedAtUtc = DateTime.UtcNow;

        if (!string.IsNullOrWhiteSpace(details.ProductUrl)) product.ProductUrl = details.ProductUrl;
        if (!string.IsNullOrWhiteSpace(details.DescriptionHtml)) product.Description = details.DescriptionHtml;
        if (details.Specifics.Count > 0) product.SpecificsJson = JsonSerializer.Serialize(details.Specifics);
        if (string.IsNullOrWhiteSpace(product.Barcode) && !string.IsNullOrWhiteSpace(details.Barcode)) product.Barcode = details.Barcode;
        if (string.IsNullOrWhiteSpace(product.ImageUrlsJson) && details.ImageUrls.Count > 0)
        {
            product.ImageUrlsJson = JsonSerializer.Serialize(details.ImageUrls);
            product.ImageUrl ??= details.ImageUrls[0];
        }
        product.UpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync(ct);
        return (true, "Fetched description and specifications from the supplier website.");
    }

    /// <summary>
    /// Copies product data onto the listing. Only empty / placeholder listing fields are filled,
    /// so anything the user already edited is kept.
    /// </summary>
    public async Task FillListingFromProductAsync(Listing listing, SupplierProduct product)
    {
        if (string.IsNullOrWhiteSpace(listing.Title)) listing.Title = product.Title;
        listing.SourceTitle ??= product.Title;
        // The rich-text editor saves an empty description as "<p><br></p>", so check the text, not the HTML.
        if (IsBlankHtml(listing.Description) && !IsBlankHtml(product.Description))
        {
            listing.Description = product.Description!;
        }

        var listingImages = ReadList(listing.ImageUrlsJson);
        if (listingImages.Count == 0)
        {
            var productImages = ReadList(product.ImageUrlsJson);
            if (productImages.Count == 0 && !string.IsNullOrWhiteSpace(product.ImageUrl)) productImages.Add(product.ImageUrl);
            if (productImages.Count > 0) listing.ImageUrlsJson = JsonSerializer.Serialize(productImages.Take(12).ToList()); // eBay max 24; keep it light
        }

        var specifics = ListingMapping.ReadSpecifics(product.SpecificsJson);
        // Variant options (e.g. Size: 1L) become item specifics, so per-size listings are distinct on eBay.
        foreach (var option in ListingMapping.ReadSpecifics(product.VariantOptionsJson))
        {
            specifics.TryAdd(option.Key, option.Value);
        }
        if (string.IsNullOrWhiteSpace(listing.ItemSpecificsJson) && specifics.Count > 0)
        {
            listing.ItemSpecificsJson = ListingMapping.WriteSpecifics(specifics);
        }
        if (IsPlaceholder(listing.Type, "Not Specified") && specifics.TryGetValue("Type", out var type)) listing.Type = type;
        if (IsPlaceholder(listing.Colour, "Multicoloured")
            && (specifics.TryGetValue("Colour", out var colour) || specifics.TryGetValue("Color", out colour)))
        {
            listing.Colour = colour;
        }
        if (IsPlaceholder(listing.Brand, "Unbranded") && !string.IsNullOrWhiteSpace(product.Brand)) listing.Brand = product.Brand;

        listing.Barcode ??= product.Barcode;
        listing.SourceUrl ??= product.ProductUrl;
        listing.SourceProductCode ??= product.SupplierSku;
        listing.SourcePrice = product.SupplierPrice ?? listing.SourcePrice;
        listing.SourceStockLevel = product.StockLevel ?? listing.SourceStockLevel;

        // Pricing and delivery (only when not set yet).
        var supplier = await _catalog.FindAsync(product.Supplier, product.ProductUrl);
        if (listing.DeliveryCost == null && supplier != null)
        {
            var option = SupplierCatalogService.SuggestDeliveryOption(supplier, product.PostageCategory);
            if (option != null)
            {
                listing.DeliveryOptionName = option.Name;
                listing.DeliveryCost = PricingMath.DeliveryIncVat(option, supplier);
            }
        }
        listing.IncludeDeliveryInPrice = true;

        if (listing.Price <= 0m || listing.Price == 0.99m)
        {
            var cost = PricingMath.CostIncVat(product.SupplierPrice, supplier);
            var suggested = PricingMath.SuggestItemPrice(cost, supplier?.DefaultMarginPercent ?? 20m);
            if (suggested.HasValue) listing.Price = suggested.Value;
        }
    }

    /// <summary>Enriches the product behind a listing, then fills the listing's missing fields.</summary>
    public async Task<(bool Ok, string Message)> EnrichListingAsync(Listing listing, bool force, CancellationToken ct = default)
    {
        if (!listing.SupplierProductId.HasValue) return (false, "This listing isn't linked to a supplier product.");
        var product = await _context.SupplierProducts.FirstOrDefaultAsync(p => p.Id == listing.SupplierProductId.Value, ct);
        if (product == null) return (false, "The linked supplier product no longer exists.");

        string message;
        try
        {
            (_, message) = await EnrichProductAsync(product, force, ct);
        }
        catch (Exception ex)
        {
            message = $"Could not fetch supplier page: {ex.Message}";
        }

        await FillListingFromProductAsync(listing, product);
        await _context.SaveChangesAsync(ct);
        return (true, message);
    }

    private static bool IsBlankHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return true;
        var text = System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "));
        return string.IsNullOrWhiteSpace(text.Replace(' ', ' '));
    }

    private static bool IsPlaceholder(string? value, string placeholder)
        => string.IsNullOrWhiteSpace(value) || value.Equals(placeholder, StringComparison.OrdinalIgnoreCase);

    private static List<string> ReadList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch (JsonException) { return new List<string>(); }
    }
}
