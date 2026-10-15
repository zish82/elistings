using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Shared;
using System.Net;
using System.Net.Http.Headers;

namespace Server.Services;

public class ScraperService : IScraperService
{
    private readonly HttpClient _httpClient;
    private readonly string? _proxyUrlTemplate;
    private readonly string? _proxyApiKey;
    private readonly HashSet<string> _proxyFallbackDomains;
    private readonly List<IProductScraper> _scrapers;

    public ScraperService(HttpClient httpClient, IConfiguration configuration, IEnumerable<IProductScraper> scrapers)
    {
        _httpClient = httpClient;
        _proxyUrlTemplate = configuration["Scraper:ProxyUrlTemplate"];
        _proxyApiKey = configuration["Scraper:ProxyApiKey"];
        _proxyFallbackDomains = (configuration["Scraper:ProxyFallbackDomains"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(d => d.ToLowerInvariant())
            .ToHashSet();

        _scrapers = scrapers.ToList();

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/123.0.0.0 Safari/537.36");
        }

        if (!_httpClient.DefaultRequestHeaders.Accept.Any())
        {
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xml", 0.9));
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.8));
        }

        if (!_httpClient.DefaultRequestHeaders.AcceptLanguage.Any())
        {
            _httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9,en-US;q=0.8");
        }

        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Cache-Control", "no-cache");
        _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("Pragma", "no-cache");
    }

    public async Task<ExtractedDetailsDto> ExtractDetailsAsync(string url)
    {
        var html = await FetchHtmlAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var fallback = _scrapers.FirstOrDefault(s => s.IsFallback);
        var primary = _scrapers.FirstOrDefault(s => !s.IsFallback && s.CanHandle(url)) ?? fallback;

        if (primary == null)
        {
            throw new InvalidOperationException("No scraper strategy is registered.");
        }

        var details = await primary.ExtractDetailsAsync(url, doc, html);

        if (fallback != null && !ReferenceEquals(primary, fallback))
        {
            var genericDetails = await fallback.ExtractDetailsAsync(url, doc, html);
            details = Merge(details, genericDetails);
        }

        return details;
    }

    public async Task<List<string>> ExtractProductLinksAsync(string categoryUrl)
    {
        // Shopify stores (e.g. Eden Horticulture) expose every product in a collection via
        // /collections/<handle>/products.json, which is paginated and avoids missing
        // products that are only loaded by "load more"/infinite scroll in the HTML.
        var shopifyLinks = await TryExtractShopifyCollectionLinksAsync(categoryUrl);
        if (shopifyLinks != null && shopifyLinks.Count > 0)
        {
            Console.WriteLine($"[Scraper] Found {shopifyLinks.Count} product links via Shopify collection JSON.");
            return shopifyLinks;
        }

        var html = await FetchHtmlAsync(categoryUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var primary = _scrapers.FirstOrDefault(s => !s.IsFallback && s.CanHandle(categoryUrl));
        if (primary == null) return new List<string>();

        return await primary.ExtractProductLinksAsync(categoryUrl, doc, html);
    }

    private async Task<List<string>?> TryExtractShopifyCollectionLinksAsync(string categoryUrl)
    {
        if (!Uri.TryCreate(categoryUrl, UriKind.Absolute, out var uri)) return null;

        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var idx = Array.FindIndex(segments, s => s.Equals("collections", StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx + 1 >= segments.Length) return null;

        var collectionPath = "/collections/" + segments[idx + 1];
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var links = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        const int maxPages = 40; // 40 x 250 = 10,000 products safety cap
        for (var page = 1; page <= maxPages; page++)
        {
            string json;
            try
            {
                json = await FetchHtmlAsync($"{origin}{collectionPath}/products.json?limit=250&page={page}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Scraper] Shopify collection JSON not available ({ex.Message}); falling back to HTML.");
                return page == 1 ? null : links;
            }

            System.Text.Json.JsonDocument doc;
            try
            {
                doc = System.Text.Json.JsonDocument.Parse(json);
            }
            catch (System.Text.Json.JsonException)
            {
                return page == 1 ? null : links; // not a Shopify JSON endpoint
            }

            using var docScope = doc;
            if (!doc.RootElement.TryGetProperty("products", out var products)
                || products.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return page == 1 ? null : links;
            }

            var countOnPage = 0;
            foreach (var product in products.EnumerateArray())
            {
                if (!product.TryGetProperty("handle", out var handleEl)) continue;
                var handle = handleEl.GetString();
                if (string.IsNullOrWhiteSpace(handle)) continue;
                countOnPage++;
                var link = $"{origin}{collectionPath}/products/{handle}";
                if (seen.Add(link)) links.Add(link);
            }

            if (countOnPage < 250) break;
            await Task.Delay(300); // be polite between pages
        }

        return links;
    }

    private ExtractedDetailsDto Merge(ExtractedDetailsDto primary, ExtractedDetailsDto fallback)
    {
        primary.Title = string.IsNullOrWhiteSpace(primary.Title) ? fallback.Title : primary.Title;
        primary.Description = string.IsNullOrWhiteSpace(primary.Description) ? fallback.Description : primary.Description;
        primary.Category = string.IsNullOrWhiteSpace(primary.Category) ? fallback.Category : primary.Category;
        primary.Brand = string.IsNullOrWhiteSpace(primary.Brand) ? fallback.Brand : primary.Brand;

        primary.Price ??= fallback.Price;
        primary.PriceIncVat ??= fallback.PriceIncVat;
        primary.PriceExVat ??= fallback.PriceExVat;

        if (string.IsNullOrWhiteSpace(primary.ProductCode))
        {
            primary.ProductCode = fallback.ProductCode;
        }

        var mergedImages = new List<string>();
        var mergedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddImage(string? imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return;

            var candidate = imageUrl.Trim().Replace("&amp;", "&");
            if (candidate.StartsWith("//", StringComparison.Ordinal))
            {
                candidate = "https:" + candidate;
            }
            if (candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                candidate = "https://" + candidate.Substring("http://".Length);
            }

            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return;

            var canonicalPathUrl = uri.GetLeftPart(UriPartial.Path);
            var canonicalKey = $"{uri.Host.ToLowerInvariant()}{uri.AbsolutePath}";
            if (!mergedKeys.Add(canonicalKey)) return;

            mergedImages.Add(canonicalPathUrl);
        }

        foreach (var image in primary.ImageUrls ?? new List<string>()) AddImage(image);
        foreach (var image in fallback.ImageUrls ?? new List<string>()) AddImage(image);

        primary.ImageUrls = mergedImages;
        return primary;
    }

    private bool ShouldUseProxyFallback(string url)
    {
        if (_proxyFallbackDomains.Count == 0) return false;

        var host = new Uri(url).Host.ToLowerInvariant();
        return _proxyFallbackDomains.Any(domain =>
            host.Equals(domain, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase));
    }

    private string? BuildProxyUrl(string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(_proxyUrlTemplate)) return null;

        var template = _proxyUrlTemplate;
        if (template.Contains("{key}", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(_proxyApiKey)) return null;
            template = template.Replace("{key}", Uri.EscapeDataString(_proxyApiKey));
        }

        if (!template.Contains("{url}", StringComparison.OrdinalIgnoreCase)) return null;
        return template.Replace("{url}", Uri.EscapeDataString(targetUrl));
    }

    private async Task<string> FetchViaProxyAsync(string url)
    {
        var proxyUrl = BuildProxyUrl(url);
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            throw new InvalidOperationException("Direct fetch was blocked (HTTP 403), and no scraping proxy is configured. Set Scraper:ProxyUrlTemplate (and Scraper:ProxyApiKey if required).\nExample template: https://api.scraperapi.com/?api_key={key}&url={url}&country_code=uk");
        }

        using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, proxyUrl);
        using var proxyResponse = await _httpClient.SendAsync(proxyRequest, HttpCompletionOption.ResponseHeadersRead);
        proxyResponse.EnsureSuccessStatusCode();
        return await proxyResponse.Content.ReadAsStringAsync();
    }

    private async Task<string> FetchHtmlAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var host = new Uri(url).Host;
        request.Headers.Referrer = new Uri($"https://{host}/");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
        {
            if (ShouldUseProxyFallback(url))
            {
                Console.WriteLine($"[Scraper] Direct fetch returned {(int)response.StatusCode} for {url}. Retrying via configured proxy for matched domain.");
                return await FetchViaProxyAsync(url);
            }

            var statusDescription = response.StatusCode == HttpStatusCode.TooManyRequests
                ? "rate limited this request (HTTP 429)"
                : "blocked this request (HTTP 403)";
            throw new InvalidOperationException($"The target website {statusDescription}. Proxy fallback is domain-restricted and this domain is not allowlisted. Add it to Scraper:ProxyFallbackDomains and configure Scraper:ProxyUrlTemplate (and Scraper:ProxyApiKey if required), or use another source URL.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
