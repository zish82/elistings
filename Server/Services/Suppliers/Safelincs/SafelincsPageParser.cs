using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace Server.Services;

/// <summary>One option (variant) in the product page's "listingableId" dropdown, e.g. 1004 = "Foam".</summary>
public sealed record SafelincsOption(long Id, string Label);

/// <summary>What the importer needs from one Safelincs product page (one variant: the page or page?option=id).</summary>
public sealed class SafelincsPage
{
    public string Title { get; set; } = string.Empty;
    public string? DescriptionHtml { get; set; }
    public string? Brand { get; set; }
    public string? ProductCode { get; set; }
    public string? Mpn { get; set; }
    public string? Barcode { get; set; }
    public decimal? PriceIncVat { get; set; }
    public decimal? PriceExVat { get; set; }
    /// <summary>Exact level when shown ("5 In Stock" → 5, "20+ In Stock" → 20); 0 when out of stock; null when in stock without a number.</summary>
    public int? StockLevel { get; set; }
    public bool InStock { get; set; }
    /// <summary>"Available to Order": orderable, but Safelincs doesn't hold stock (dispatch date varies).</summary>
    public bool AvailableToOrder { get; set; }
    public bool IsDiscontinued { get; set; }
    /// <summary>False for pages without a stock/price box (e.g. training courses, services).</summary>
    public bool HasBuyBox { get; set; }
    public string? DispatchText { get; set; }
    /// <summary>Safelincs' id for the variant shown on this page ("listingable").</summary>
    public long? VariantId { get; set; }
    public List<string> Breadcrumbs { get; set; } = new();
    public string? Category => Breadcrumbs.LastOrDefault();
    public List<string> ImageUrls { get; set; } = new();
    public Dictionary<string, string> Specifics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SafelincsOption> Options { get; set; } = new();
    /// <summary>Dropdown label of the variant this page was loaded for (set by the importer).</summary>
    public string? VariantLabel { get; set; }
    /// <summary>Retail "Standard Delivery" charge shown on the page, inc VAT.</summary>
    public decimal? StandardDeliveryIncVat { get; set; }
}

/// <summary>
/// Parses Safelincs product pages (Laravel Livewire, server-rendered). Structure as of Oct 2026:
/// h1 title, div.description, div[itemprop=offers] (div.stock "5 In Stock", p.primary-price inc VAT, p.secondary-price ex VAT),
/// table.technical-data (Product Code, Brand, Manufacturer's Part Number, specs), ul.breadcrumb meta[itemprop=name],
/// div.images meta[itemprop=image], select[wire:model.live=listingableId] (variants; page?option=id renders one).
/// </summary>
public static class SafelincsPageParser
{
    private static readonly string[] IgnoredBreadcrumbs = { "Home", "Products" };
    private static readonly string[] NonSpecificKeys = { "Product Code", "Brand", "Manufacturer's Part Number", "Manufacturer’s Part Number" };

    public static SafelincsPage Parse(string html)
    {
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        var root = doc.DocumentNode;
        // Livewire wraps conditionals in <!--[if BLOCK]> comments; drop them so they never leak into text.
        foreach (var comment in root.SelectNodes("//comment()")?.ToList() ?? new List<HtmlNode>()) comment.Remove();
        var page = new SafelincsPage
        {
            Title = Text(root.SelectSingleNode("//h1")) ?? string.Empty
        };

        var description = root.SelectSingleNode("//div[contains(concat(' ', normalize-space(@class), ' '), ' description ')]");
        page.DescriptionHtml = HtmlSanitizer.Clean(description?.InnerHtml);

        // ---- buy box: price, stock, dispatch, variant id
        var offers = root.SelectSingleNode("//*[@itemprop='offers']");
        if (offers != null)
        {
            page.PriceIncVat = ParsePrice(Text(offers.SelectSingleNode(".//p[contains(@class,'primary-price')]")))
                               ?? ParsePrice(offers.SelectSingleNode(".//meta[@itemprop='price']")?.GetAttributeValue("content", null));
            page.PriceExVat = ParsePrice(Text(offers.SelectSingleNode(".//p[contains(@class,'secondary-price')]")));

            var stock = offers.SelectSingleNode(".//div[contains(concat(' ', normalize-space(@class), ' '), ' stock ')]");
            if (stock != null)
            {
                page.HasBuyBox = true;
                ApplyStock(page, stock);
            }

            // The stock component's Livewire snapshot names the variant: {"data":{"listingableId":297,...}}
            foreach (var node in offers.DescendantsAndSelf())
            {
                var snapshot = node.GetAttributeValue("wire:snapshot", null);
                if (snapshot == null) continue;
                var m = Regex.Match(WebUtility.HtmlDecode(snapshot), "\"listingableId\"\\s*:\\s*(\\d+)");
                if (m.Success && long.TryParse(m.Groups[1].Value, out var id)) { page.VariantId = id; break; }
            }
        }

        // ---- technical data
        var table = root.SelectSingleNode("//table[contains(concat(' ', normalize-space(@class), ' '), ' technical-data ')]");
        if (table != null) ReadTechnicalData(page, table);
        page.ProductCode ??= root.SelectSingleNode("//meta[@itemprop='sku']")?.GetAttributeValue("content", null)?.Trim();
        page.Mpn ??= root.SelectSingleNode("//meta[@itemprop='mpn']")?.GetAttributeValue("content", null)?.Trim();
        page.Barcode = root.SelectSingleNode("//meta[@itemprop='gtin13' or @itemprop='gtin' or @itemprop='gtin12' or @itemprop='gtin14' or @itemprop='gtin8']")
            ?.GetAttributeValue("content", null)?.Trim();
        if (!string.IsNullOrWhiteSpace(page.Mpn)) page.Specifics.TryAdd("MPN", page.Mpn!);

        // ---- breadcrumbs → category
        var crumbs = root.SelectNodes("//ul[contains(concat(' ', normalize-space(@class), ' '), ' breadcrumb ')]//meta[@itemprop='name']");
        if (crumbs != null)
        {
            page.Breadcrumbs = crumbs
                .Select(c => Clean(c.GetAttributeValue("content", "")))
                .Where(c => c.Length > 0 && !IgnoredBreadcrumbs.Contains(c, StringComparer.OrdinalIgnoreCase)
                            && !c.Equals(page.Title, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        // ---- images (gallery only, not related products)
        var gallery = root.SelectSingleNode("//div[contains(concat(' ', normalize-space(@class), ' '), ' images ')]");
        var images = gallery?.SelectNodes(".//meta[@itemprop='image']")
            ?.Select(m => m.GetAttributeValue("content", null))
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (images is { Count: > 0 }) page.ImageUrls = images;
        else
        {
            var og = root.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", null);
            if (!string.IsNullOrWhiteSpace(og)) page.ImageUrls.Add(og);
        }

        // ---- delivery (retail charge shown on the page)
        var shipping = root.SelectNodes("//div[contains(concat(' ', normalize-space(@class), ' '), ' shipping-option ')]");
        if (shipping != null)
        {
            var standard = shipping.FirstOrDefault(s => (Text(s.SelectSingleNode(".//h2")) ?? "").Contains("Standard", StringComparison.OrdinalIgnoreCase))
                           ?? shipping.First();
            page.StandardDeliveryIncVat = ParsePrice(Text(standard.SelectSingleNode(".//p[contains(@class,'shipping-price')]")));
        }

        page.Options = ParseOptions(html);
        return page;
    }

    /// <summary>
    /// Variant dropdown. Read from the raw HTML because HtmlAgilityPack treats &lt;option&gt; as an empty element
    /// (its text would end up outside the node).
    /// </summary>
    internal static List<SafelincsOption> ParseOptions(string html)
    {
        var result = new List<SafelincsOption>();
        var start = html.IndexOf("wire:model.live=\"listingableId\"", StringComparison.Ordinal);
        if (start < 0) return result;
        var end = html.IndexOf("</select>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return result;

        var block = html[start..end];
        foreach (Match m in Regex.Matches(block, "<option[^>]*\\bvalue=\"(\\d+)\"[^>]*>(.*?)</option>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            if (!long.TryParse(m.Groups[1].Value, out var id)) continue;
            var label = Clean(Regex.Replace(m.Groups[2].Value, "<[^>]+>", " "));
            if (label.Length > 0 && result.All(o => o.Id != id)) result.Add(new SafelincsOption(id, label));
        }
        return result;
    }

    private static void ApplyStock(SafelincsPage page, HtmlNode stock)
    {
        var cls = stock.GetAttributeValue("class", "");
        var text = Clean(stock.InnerText);
        var availability = stock.SelectSingleNode(".//link[@itemprop='availability']")?.GetAttributeValue("href", "") ?? "";

        page.InStock = cls.Contains("in-stock", StringComparison.OrdinalIgnoreCase)
                       || availability.EndsWith("/InStock", StringComparison.OrdinalIgnoreCase)
                       || (text.Contains("In Stock", StringComparison.OrdinalIgnoreCase) && !text.Contains("Out of Stock", StringComparison.OrdinalIgnoreCase));

        var level = Regex.Match(text, @"(\d[\d,]*)\s*\+?\s*In Stock", RegexOptions.IgnoreCase);
        page.StockLevel = !page.InStock
            ? 0
            : level.Success && int.TryParse(level.Groups[1].Value.Replace(",", ""), out var n) ? n : null;

        page.IsDiscontinued = availability.EndsWith("/Discontinued", StringComparison.OrdinalIgnoreCase)
                              || text.StartsWith("Discontinued", StringComparison.OrdinalIgnoreCase);
        page.AvailableToOrder = page.InStock && text.Contains("Available to Order", StringComparison.OrdinalIgnoreCase);
        if (page.IsDiscontinued) { page.InStock = false; page.StockLevel = 0; }

        var dispatch = Regex.Match(text, @"Estimated dispatch:\s*(.+)$", RegexOptions.IgnoreCase);
        if (dispatch.Success) page.DispatchText = dispatch.Groups[1].Value.Trim();
    }

    private static void ReadTechnicalData(SafelincsPage page, HtmlNode table)
    {
        // A <th rowspan="2"> covers the next row(s), which then only have a <td>: join those values.
        string? key = null;
        foreach (var row in table.SelectNodes(".//tr") ?? Enumerable.Empty<HtmlNode>())
        {
            var th = row.SelectSingleNode("./th");
            var td = row.SelectSingleNode("./td");
            if (th != null) key = Clean(th.InnerText);
            if (key == null || td == null) continue;
            var value = Clean(td.InnerText);
            if (value.Length == 0) continue;

            if (key.Equals("Product Code", StringComparison.OrdinalIgnoreCase)) { page.ProductCode ??= value; continue; }
            if (key.Equals("Brand", StringComparison.OrdinalIgnoreCase)) { page.Brand ??= value; continue; }
            if (key.StartsWith("Manufacturer", StringComparison.OrdinalIgnoreCase) && key.Contains("Part Number", StringComparison.OrdinalIgnoreCase)) { page.Mpn ??= value; continue; }
            if (NonSpecificKeys.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;

            // eBay item specifics are limited to 65 characters per value.
            page.Specifics[key] = th != null || !page.Specifics.TryGetValue(key, out var existing)
                ? value
                : Truncate($"{existing}, {value}", 65);
        }
    }

    internal static decimal? ParsePrice(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Regex.Match(text, @"(\d[\d,]*(?:\.\d+)?)");
        return m.Success && decimal.TryParse(m.Groups[1].Value.Replace(",", ""), NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? Text(HtmlNode? node)
    {
        if (node == null) return null;
        var text = Clean(node.InnerText);
        return text.Length == 0 ? null : text;
    }

    private static string Clean(string? value)
        => Regex.Replace(WebUtility.HtmlDecode(value ?? string.Empty), @"\s+", " ").Trim();

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd(' ', ',');
}
