using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Server.Data;
using Shared;

namespace Server.Services;

/// <summary>Reading, validating and saving a listing's variations.</summary>
public static class ListingVariants
{
    public static ListingVariantDto ToDto(ListingVariant v) => new()
    {
        Id = v.Id, Sku = v.Sku, Options = ReadOptions(v.OptionsJson), Price = v.Price, Quantity = v.Quantity,
        ImageUrl = v.ImageUrl, SourcePrice = v.SourcePrice, SupplierProductId = v.SupplierProductId, OfferId = v.OfferId
    };

    public static Dictionary<string, string> ReadOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new();
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new(); }
        catch (JsonException) { return new(); }
    }

    public static async Task<List<ListingVariantDto>> LoadAsync(AppDbContext context, int listingId)
        => (await context.ListingVariants.AsNoTracking().Where(v => v.ListingId == listingId).OrderBy(v => v.SortOrder).ThenBy(v => v.Id).ToListAsync())
            .Select(ToDto).ToList();

    /// <summary>An error message when the variations can't be saved; null when they are fine. An empty list (no variations) is valid.</summary>
    public static string? Validate(IReadOnlyList<ListingVariantDto> variants)
    {
        if (variants.Count == 0) return null;
        if (variants.Count < 2) return "Add at least 2 variations, or remove them all.";
        if (variants.Count > 250) return "eBay allows at most 250 variations.";
        var names = variants[0].Options.Keys.Select(k => k.Trim()).Where(k => k.Length > 0).ToList();
        if (names.Count == 0) return "Give the variations an option name (e.g. Size) and a value for each.";
        if (names.Count > 5) return "Use at most 5 option names (e.g. Size, Colour).";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in variants)
        {
            foreach (var n in names)
            {
                var value = v.Options.FirstOrDefault(o => o.Key.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)).Value;
                if (string.IsNullOrWhiteSpace(value)) return $"Every variation needs a value for \"{n}\".";
                if (value.Trim().Length > 65) return $"\"{value.Trim()[..20]}…\" is too long (65 characters max).";
            }
            if (v.Options.Count != names.Count) return "Every variation must use the same option names.";
            if (v.Price < 0m) return "Prices can't be negative.";
            if (v.Quantity < 0) return "Quantity can't be negative.";
            if (!seen.Add(string.Join("|", names.Select(n => v.Options.First(o => o.Key.Trim().Equals(n, StringComparison.OrdinalIgnoreCase)).Value.Trim())))) return "Two variations have the same values.";
        }
        return null;
    }
}
