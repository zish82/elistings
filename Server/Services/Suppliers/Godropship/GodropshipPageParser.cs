using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Server.Services;

/// <summary>What the importer / stock checker needs from one Godropship product page.</summary>
public sealed class GodropshipPage
{
    public string Title { get; set; } = string.Empty;
    public string? DescriptionHtml { get; set; }
    /// <summary>Godropship's item code, e.g. "EPG-76816".</summary>
    public string? ItemCode { get; set; }
    /// <summary>The price Godropship charges (UK delivery included).</summary>
    public decimal? Price { get; set; }
    /// <summary>"UK Stock : 100"; null when the page shows no figure.</summary>
    public int? StockLevel { get; set; }
    public bool InStock { get; set; }
    public decimal? WeightGrams { get; set; }
    public List<string> Breadcrumbs { get; set; } = new();
    public string? Category => Breadcrumbs.LastOrDefault();
    public List<string> ImageUrls { get; set; } = new();

    /// <summary>False for pages that loaded but aren't a product (unknown URLs return the site's page with status 200).</summary>
    public bool IsProduct => !string.IsNullOrWhiteSpace(Title) && !string.IsNullOrWhiteSpace(ItemCode);
}

/// <summary>
/// Parses Godropship product pages. Structure as of Nov 2026:
/// a schema.org Product JSON-LD block (name, image[], description HTML, sku = item code, offers.price / availability),
/// plus span#change_child_price ("£66.88"), span#change_child_inventory ("100" = UK stock),
/// span#change_child_weight (grams; the same id is reused for "HOLD"), and div.crumbs span.crumbs-order (breadcrumb).
/// Variants (sizes/colours) are separate product pages with their own item codes.
/// </summary>
public static class GodropshipPageParser
{
    public static GodropshipPage Parse(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = doc.DocumentNode;
        var page = new GodropshipPage();

        ReadJsonLd(root, page);

        // The visible buy box wins over the JSON-LD (it's what Godropship charges right now).
        var price = ParseDecimal(root.SelectSingleNode("//*[@id='change_child_price']")?.InnerText);
        if (price.HasValue) page.Price = price;

        var stockText = Clean(root.SelectSingleNode("//*[@id='change_child_inventory']")?.InnerText);
        if (int.TryParse(Regex.Match(stockText ?? string.Empty, @"-?\d+").Value, out var stock))
        {
            page.StockLevel = Math.Max(0, stock);
            page.InStock = stock > 0;
        }

        page.WeightGrams = root.SelectNodes("//*[@id='change_child_weight']")
            ?.Select(n => ParseDecimal(n.InnerText)).FirstOrDefault(w => w.HasValue);

        page.Breadcrumbs = root.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' crumbs ')]//span[contains(@class,'crumbs-order')]")
            ?.Select(n => Clean(n.InnerText) ?? string.Empty)
            .Where(n => n.Length > 0 && !n.Equals("Home", StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<string>();

        if (string.IsNullOrWhiteSpace(page.Title))
            page.Title = Clean(root.SelectSingleNode("//h1")?.InnerText) ?? string.Empty;

        return page;
    }

    private static void ReadJsonLd(HtmlNode root, GodropshipPage page)
    {
        var scripts = root.SelectNodes("//script[@type='application/ld+json']");
        if (scripts == null) return;
        foreach (var script in scripts)
        {
            JsonElement product;
            try
            {
                using var json = JsonDocument.Parse(script.InnerText);
                if (!TryFindProduct(json.RootElement, out product)) continue;
                product = product.Clone();
            }
            catch (JsonException)
            {
                continue;
            }

            page.Title = Clean(Str(product, "name")) ?? page.Title;
            page.ItemCode = Clean(Str(product, "sku")) ?? Clean(Str(product, "mpn"));
            var description = Str(product, "description");
            if (!string.IsNullOrWhiteSpace(description)) page.DescriptionHtml = description.Trim();

            if (product.TryGetProperty("image", out var images))
            {
                var list = images.ValueKind == JsonValueKind.Array
                    ? images.EnumerateArray().Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null)
                    : new[] { images.ValueKind == JsonValueKind.String ? images.GetString() : null };
                page.ImageUrls = list.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u!.Trim()).Distinct().ToList();
            }

            if (product.TryGetProperty("offers", out var offers))
            {
                if (offers.ValueKind == JsonValueKind.Array) offers = offers.EnumerateArray().FirstOrDefault();
                if (offers.ValueKind == JsonValueKind.Object)
                {
                    page.Price = ParseDecimal(Str(offers, "price"));
                    var availability = Str(offers, "availability") ?? string.Empty;
                    page.InStock = availability.Contains("InStock", StringComparison.OrdinalIgnoreCase);
                }
            }
            return;
        }
    }

    private static bool TryFindProduct(JsonElement element, out JsonElement product)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryFindProduct(item, out product)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Object
                 && string.Equals(Str(element, "@type"), "Product", StringComparison.OrdinalIgnoreCase))
        {
            product = element;
            return true;
        }
        product = default;
        return false;
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    private static string? Clean(string? text)
    {
        if (text == null) return null;
        var cleaned = Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    internal static decimal? ParseDecimal(string? text)
    {
        var match = Regex.Match(WebUtility.HtmlDecode(text ?? string.Empty).Replace(",", ""), @"\d+(\.\d+)?");
        return match.Success && decimal.TryParse(match.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Product page URLs from /sitemapproducts.xml (every &lt;loc&gt; on godropship.co.uk).</summary>
    public static List<string> ParseSitemap(string xml)
        => Regex.Matches(xml, @"<loc>\s*([^<\s]+)\s*</loc>", RegexOptions.IgnoreCase)
            .Select(m => WebUtility.HtmlDecode(m.Groups[1].Value).Trim())
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) && IsGodropshipHost(uri.Host))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsGodropshipHost(string host)
        => host.Equals("godropship.co.uk", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".godropship.co.uk", StringComparison.OrdinalIgnoreCase);

    public static bool IsGodropshipUrl(string? url)
        => url != null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsGodropshipHost(uri.Host);

    public static string Slug(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return path.TrimEnd('/').Split('/').Last();
    }
}
