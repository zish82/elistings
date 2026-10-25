using HtmlAgilityPack;
using System.Net;
using System.Text.RegularExpressions;

namespace Server.Services;

/// <summary>Data read from one DropShipMall product page.</summary>
public sealed class DropShipMallProductPage
{
    public required string Url { get; init; }
    public string? Sku { get; init; }
    public string? DescriptionHtml { get; init; }
    public IReadOnlyDictionary<string, string> Specifics { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> ImageUrls { get; init; } = Array.Empty<string>();
    public string? Barcode { get; init; }
    /// <summary>Raw "Availability" value, e.g. "More than 150", "12", "Out of Stock".</summary>
    public string? Availability { get; init; }
}

/// <summary>
/// Single place that knows how to find and read DropShipMall product pages (public, no login).
/// Used by the product enricher (description/specifics) and the stock checker (availability).
/// </summary>
public class DropShipMallWebsiteClient
{
    public const string SupplierCode = "DropShipMall";
    public const string Host = "dropshipmall.co.uk";
    private const string BaseUrl = "https://www.dropshipmall.co.uk";
    private const int MaxSearchCandidates = 5;

    private static readonly Regex ProductLinkPattern = new(@"/p\d+\.html$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;

    public DropShipMallWebsiteClient(HttpClient http)
    {
        _http = http;
        _http.Timeout = TimeSpan.FromSeconds(30);
        if (!_http.DefaultRequestHeaders.UserAgent.Any())
        {
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        }
    }

    public static bool IsDropShipMallUrl(string? url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Host.Equals(Host, StringComparison.OrdinalIgnoreCase)
               || uri.Host.EndsWith("." + Host, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Returns the product page for <paramref name="sku"/>: tries <paramref name="knownUrl"/> first, then the
    /// site search. Pages whose product code doesn't match the SKU are ignored (search can return related items).
    /// </summary>
    public async Task<DropShipMallProductPage?> FindProductPageAsync(string? sku, string? knownUrl, CancellationToken ct = default)
    {
        sku = string.IsNullOrWhiteSpace(sku) ? null : sku.Trim();

        var candidates = new List<string>();
        if (IsDropShipMallUrl(knownUrl)) candidates.Add(knownUrl!);
        if (sku != null) candidates.AddRange(await SearchProductUrlsAsync(sku, ct));

        foreach (var url in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            HtmlDocument doc;
            try
            {
                doc = await LoadAsync(url, ct);
            }
            catch (HttpRequestException)
            {
                continue; // stale URL / 404: try the next candidate
            }

            var page = Parse(doc, url);
            if (sku == null || page.Sku == null || page.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase))
            {
                return page;
            }
        }

        return null;
    }

    private async Task<IEnumerable<string>> SearchProductUrlsAsync(string sku, CancellationToken ct)
    {
        // The site's "show all results" URL. (/search?q= renders the home page and ignores the query.)
        var doc = await LoadAsync($"{BaseUrl}/?c=ps&search={Uri.EscapeDataString(sku)}", ct);
        return doc.DocumentNode.SelectNodes("//a[@href]")?
                   .Select(a => a.GetAttributeValue("href", ""))
                   .Where(h => ProductLinkPattern.IsMatch(h))
                   .Select(h => h.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? h : BaseUrl + "/" + h.TrimStart('/'))
                   .Distinct(StringComparer.OrdinalIgnoreCase)
                   .Take(MaxSearchCandidates)
               ?? Enumerable.Empty<string>();
    }

    private async Task<HtmlDocument> LoadAsync(string url, CancellationToken ct)
    {
        var html = await _http.GetStringAsync(url, ct);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    private static DropShipMallProductPage Parse(HtmlDocument doc, string url)
    {
        var root = doc.DocumentNode;

        var sku = root.SelectSingleNode("//td[@itemprop='identifier']")?.GetAttributeValue("content", "")?.Replace("sku:", "").Trim();

        var description = root.SelectSingleNode("//section[h4[contains(normalize-space(.),'Product Description')]]//div[contains(@class,'mt10')]")
                          ?? root.SelectSingleNode("//*[@itemprop='description']/..");

        // "Product Code / Barcode / Availability" table (not the delivery price table).
        var info = ReadTable(root.SelectNodes("//table[contains(@class,'table-striped') and not(contains(@class,'priceBreaks'))]//tr"));
        var specifics = ReadTable(root.SelectNodes("//section[h4[contains(normalize-space(.),'Product Specifications')]]//table//tr"));

        var images = (root.SelectNodes("//ul[contains(@class,'product-content__gallery')]//a[@href]")
                      ?? root.SelectNodes("//figure[contains(@class,'product-content__image')]//a[@href]"))?
                     .Select(a => a.GetAttributeValue("href", ""))
                     .Where(h => h.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .ToList()
                     ?? new List<string>();

        return new DropShipMallProductPage
        {
            Url = url,
            Sku = string.IsNullOrWhiteSpace(sku) ? info.GetValueOrDefault("Product Code") : sku,
            DescriptionHtml = description?.InnerHtml,
            Specifics = specifics,
            ImageUrls = images,
            Barcode = info.GetValueOrDefault("Barcode"),
            Availability = info.GetValueOrDefault("Availability")
        };
    }

    private static Dictionary<string, string> ReadTable(HtmlNodeCollection? rows)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (rows == null) return values;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./td");
            if (cells == null || cells.Count < 2) continue;
            var key = Clean(cells[0].InnerText).TrimEnd(':');
            var value = Clean(cells[1].InnerText);
            if (key.Length > 0 && value.Length > 0) values[key] = value;
        }
        return values;
    }

    private static string Clean(string text) => Regex.Replace(WebUtility.HtmlDecode(text), @"\s+", " ").Trim();
}
