namespace Server.Services;

/// <summary>
/// Reads Godropship's public website (no login needed: price and UK stock are shown to everyone).
/// Identifies itself honestly (no browser user agent) and only reads the product sitemap and product pages, never the
/// download-queue or cart URLs. Every request goes through the per-host throttle (RequestThrottle:godropship.co.uk).
/// NOTE: Godropship's robots.txt and terms (3.2) ask automated clients to get written permission first; permission
/// was requested in Nov 2026. Background sync for this supplier is off by default for that reason.
/// </summary>
public class GodropshipWebsiteClient
{
    public const string SupplierCode = "Godropship";
    public const string BaseUrl = "https://www.godropship.co.uk";
    public const string SitemapUrl = BaseUrl + "/sitemapproducts.xml";
    private const string UserAgent = "eListingsSupplierSync/1.0 (stock and price check for products we resell)";

    private readonly HttpClient _httpClient;

    public GodropshipWebsiteClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(60);
    }

    public async Task<List<string>> GetProductUrlsAsync(CancellationToken ct = default)
        => GodropshipPageParser.ParseSitemap(await GetStringAsync(SitemapUrl, ct));

    /// <summary>The parsed product page. Throws when it can't be loaded; check <see cref="GodropshipPage.IsProduct"/>.</summary>
    public async Task<GodropshipPage> GetProductAsync(string url, CancellationToken ct = default)
    {
        if (!GodropshipPageParser.IsGodropshipUrl(url)) throw new InvalidOperationException($"Not a Godropship URL: {url}");
        return GodropshipPageParser.Parse(await GetStringAsync(url, ct));
    }

    private async Task<string> GetStringAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en;q=0.9");
        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length < 500) throw new InvalidOperationException($"Godropship returned an empty page for {url}.");
        return body;
    }
}
