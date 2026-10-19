using System.Text.Json;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>Mapping helpers for the delivery/pricing/specifics fields shared by several endpoints.</summary>
public static class ListingMapping
{
    public static Dictionary<string, string> ReadSpecifics(string? json)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            if (parsed != null) foreach (var kv in parsed) result[kv.Key] = kv.Value;
        }
        catch (JsonException)
        {
            // ignore malformed data
        }
        return result;
    }

    public static string? WriteSpecifics(Dictionary<string, string>? specifics)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in specifics ?? new Dictionary<string, string>())
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || string.IsNullOrWhiteSpace(kv.Value)) continue;
            clean[kv.Key.Trim()] = kv.Value.Trim();
        }
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }

    /// <summary>Copies the delivery / marketplace / specifics fields from a create/update request.</summary>
    public static void ApplyExtras(Listing listing, CreateListingRequest request)
    {
        listing.Marketplace = string.IsNullOrWhiteSpace(request.Marketplace) ? "eBay" : request.Marketplace.Trim();
        listing.DeliveryOptionName = string.IsNullOrWhiteSpace(request.DeliveryOptionName) ? null : request.DeliveryOptionName.Trim();
        listing.DeliveryCost = request.DeliveryCost.HasValue && request.DeliveryCost.Value < 0 ? 0 : request.DeliveryCost;
        // Separate postage is not implemented yet, so delivery is always folded into the price.
        listing.IncludeDeliveryInPrice = true;
        listing.ItemSpecificsJson = WriteSpecifics(request.ItemSpecifics);
        listing.Barcode = string.IsNullOrWhiteSpace(request.Barcode) ? null : request.Barcode.Trim();
        // The original title is write-once, so later edits can't overwrite the tracking copy.
        if (string.IsNullOrWhiteSpace(listing.SourceTitle) && !string.IsNullOrWhiteSpace(request.SourceTitle))
        {
            listing.SourceTitle = request.SourceTitle.Trim();
        }
        // Supplier category comes from the supplier; keep the stored one when the request doesn't send it.
        if (!string.IsNullOrWhiteSpace(request.SupplierCategory)) listing.SupplierCategory = request.SupplierCategory.Trim();
        listing.CatalogCategoryId = request.CatalogCategoryId;
        listing.CatalogCategoryIsManual = request.CatalogCategoryIsManual && request.CatalogCategoryId.HasValue;
    }

    /// <summary>Copies the delivery / marketplace / specifics fields onto a DTO.</summary>
    public static ListingDto WithExtras(this ListingDto dto, Listing listing)
    {
        dto.Marketplace = listing.Marketplace;
        dto.DeliveryOptionName = listing.DeliveryOptionName;
        dto.DeliveryCost = listing.DeliveryCost;
        dto.IncludeDeliveryInPrice = listing.IncludeDeliveryInPrice;
        dto.ItemSpecifics = ReadSpecifics(listing.ItemSpecificsJson);
        dto.Barcode = listing.Barcode;
        dto.SourceTitle = listing.SourceTitle;
        dto.SupplierCategory = listing.SupplierCategory;
        dto.QuantityZeroedAtUtc = listing.QuantityZeroedAtUtc;
        dto.CatalogCategoryId = listing.CatalogCategoryId;
        dto.CatalogCategoryIsManual = listing.CatalogCategoryIsManual;
        return dto;
    }
}
