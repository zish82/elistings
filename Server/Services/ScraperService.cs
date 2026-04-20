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
        var html = await FetchHtmlAsync(categoryUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var primary = _scrapers.FirstOrDefault(s => !s.IsFallback && s.CanHandle(categoryUrl));
        if (primary == null) return new List<string>();

        return await primary.ExtractProductLinksAsync(categoryUrl, doc, html);
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

        var mergedImages = (primary.ImageUrls ?? new List<string>())
            .Concat(fallback.ImageUrls ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct()
            .ToList();

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
        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            if (ShouldUseProxyFallback(url))
            {
                Console.WriteLine($"[Scraper] Direct fetch blocked for {url}. Retrying via configured proxy for matched domain.");
                return await FetchViaProxyAsync(url);
            }

            throw new InvalidOperationException("The target website blocked this request (HTTP 403). Proxy fallback is domain-restricted and this domain is not allowlisted. Add it to Scraper:ProxyFallbackDomains or use another source URL.");
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }
}
