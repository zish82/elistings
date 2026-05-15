using Hangfire;
using HtmlAgilityPack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Server.Data;
using Shared;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace Server.Services;

public class EdenSupplierSyncService : IEdenSupplierSyncService
{
    private const string EdenSupplierName = "edenhorticulture";
    private const string SafelincsSupplierName = "safelincs";
    private const string MultiSupplierName = "multi";

    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly EdenSupplierSettings _settings;

    public EdenSupplierSyncService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IOptions<EdenSupplierSettings> settings)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _settings = settings.Value;
    }

    [AutomaticRetry(Attempts = 1)]
    public async Task RunPublishedSyncAsync()
    {
        var run = new SupplierSyncRun
        {
            Supplier = MultiSupplierName,
            TriggeredBy = "scheduler",
            StartedAtUtc = DateTime.UtcNow
        };

        _context.SupplierSyncRuns.Add(run);
        await _context.SaveChangesAsync();

        var listings = await _context.Listings
            .Where(l => l.Status == "Published"
                        && !string.IsNullOrWhiteSpace(l.SourceUrl)
                        && (EF.Functions.Like(l.SourceUrl!, "%edenhorticulture.co.uk%")
                            || EF.Functions.Like(l.SourceUrl!, "%safelincs.co.uk%")))
            .ToListAsync();

        run.ProcessedCount = listings.Count;

        foreach (var listing in listings)
        {
            var snapshot = await SyncListingInternalAsync(listing, run.Id, "scheduler");
            if (snapshot.IsSuccess) run.SuccessCount++;
            else if (ShouldIgnoreHangfireFailure(snapshot))
            {
                Console.WriteLine($"[SupplierSync] Ignoring Safelincs failure for listing={listing.Id}: {snapshot.ErrorMessage}");
            }
            else run.FailedCount++;
        }

        run.FinishedAtUtc = DateTime.UtcNow;
        await _context.SaveChangesAsync();
    }

    public async Task<SupplierStatusDto> SyncListingAsync(int listingId, string triggeredBy = "manual")
    {
        var listing = await _context.Listings.FirstOrDefaultAsync(l => l.Id == listingId);
        if (listing == null)
        {
            return new SupplierStatusDto
            {
                ListingId = listingId,
                Supplier = "unknown",
                IsSuccess = false,
                ErrorMessage = "Listing not found."
            };
        }

        var snapshot = await SyncListingInternalAsync(listing, null, triggeredBy);
        return ToDto(snapshot);
    }

    public async Task<SupplierStatusDto> CheckSourceAsync(string? sourceUrl, string? supplierSku, string triggeredBy = "manual")
    {
        var snapshot = await BuildSnapshotAsync(0, sourceUrl, supplierSku, null, triggeredBy, persist: false);
        return ToDto(snapshot);
    }

    public async Task<SupplierStatusDto?> GetLatestSnapshotAsync(int listingId)
    {
        var latest = await _context.SupplierListingSnapshots
            .Where(s => s.ListingId == listingId)
            .OrderByDescending(s => s.CheckedAtUtc)
            .FirstOrDefaultAsync();

        return latest == null ? null : ToDto(latest);
    }

    private async Task<SupplierListingSnapshot> SyncListingInternalAsync(Listing listing, int? runId, string triggeredBy)
    {
        return await BuildSnapshotAsync(listing.Id, listing.SourceUrl, listing.SourceProductCode, runId, triggeredBy, persist: true);
    }

    private async Task<SupplierListingSnapshot> BuildSnapshotAsync(int listingId, string? sourceUrl, string? supplierSku, int? runId, string triggeredBy, bool persist)
    {
        var supplierName = ResolveSupplierName(sourceUrl);
        var snapshot = new SupplierListingSnapshot
        {
            ListingId = listingId,
            SyncRunId = runId,
            Supplier = supplierName,
            SupplierSku = supplierSku,
            CheckedAtUtc = DateTime.UtcNow,
            StockStatus = "Unknown",
            IsSuccess = false
        };

        try
        {
            if (string.IsNullOrWhiteSpace(sourceUrl))
            {
                snapshot.ErrorMessage = "Listing source URL is missing.";
                if (persist)
                {
                    _context.SupplierListingSnapshots.Add(snapshot);
                    await _context.SaveChangesAsync();
                }
                return snapshot;
            }

            string html;
            if (string.Equals(supplierName, EdenSupplierName, StringComparison.OrdinalIgnoreCase))
            {
                if (!_settings.Enabled)
                {
                    snapshot.ErrorMessage = "Eden supplier sync is disabled in configuration.";
                    if (persist)
                    {
                        _context.SupplierListingSnapshots.Add(snapshot);
                        await _context.SaveChangesAsync();
                    }
                    return snapshot;
                }

                var edenClient = _httpClientFactory.CreateClient("eden-auth");
                await EnsureLoggedInAsync(edenClient);

                var edenProductUrl = BuildAbsoluteEdenUrl(sourceUrl);
                html = await FetchSupplierProductHtmlAsync(edenClient, edenProductUrl);
            }
            else if (string.Equals(supplierName, SafelincsSupplierName, StringComparison.OrdinalIgnoreCase))
            {
                var safelincsClient = _httpClientFactory.CreateClient();
                var safelincsProductUrl = BuildAbsoluteSafelincsUrl(sourceUrl);
                html = await FetchPublicProductHtmlAsync(safelincsClient, safelincsProductUrl);
            }
            else
            {
                snapshot.ErrorMessage = "Unsupported supplier source URL. Supported suppliers: Eden Horticulture, Safelincs.";
                if (persist)
                {
                    _context.SupplierListingSnapshots.Add(snapshot);
                    await _context.SaveChangesAsync();
                }
                return snapshot;
            }

            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            snapshot.SupplierPrice = ExtractPrice(doc, html);

            var stockText = ExtractStockText(doc, html);
            snapshot.StockText = stockText;
            snapshot.StockStatus = MapStockStatus(stockText, html);
            snapshot.IsSuccess = true;

            if (persist)
            {
                _context.SupplierListingSnapshots.Add(snapshot);
                await _context.SaveChangesAsync();
            }

            Console.WriteLine($"[SupplierSync] {triggeredBy} supplier={snapshot.Supplier} listing={listingId} sku={supplierSku} price={(snapshot.SupplierPrice?.ToString("F2") ?? "n/a")} stock={snapshot.StockStatus}");
        }
        catch (Exception ex)
        {
            snapshot.ErrorMessage = ex.Message;
            snapshot.StockStatus = "Unknown";
            snapshot.IsSuccess = false;
            if (persist)
            {
                _context.SupplierListingSnapshots.Add(snapshot);
                await _context.SaveChangesAsync();
            }
            Console.WriteLine($"[SupplierSync] Failed listing={listingId}: {ex.Message}");
        }

        return snapshot;
    }

    private static string ResolveSupplierName(string? sourceUrl)
    {
        if (string.IsNullOrWhiteSpace(sourceUrl))
        {
            return "unknown";
        }

        if (sourceUrl.Contains("edenhorticulture.co.uk", StringComparison.OrdinalIgnoreCase))
        {
            return EdenSupplierName;
        }

        if (sourceUrl.Contains("safelincs.co.uk", StringComparison.OrdinalIgnoreCase))
        {
            return SafelincsSupplierName;
        }

        return "unknown";
    }

    private static bool ShouldIgnoreHangfireFailure(SupplierListingSnapshot snapshot)
    {
        if (snapshot.IsSuccess)
        {
            return false;
        }

        if (!string.Equals(snapshot.Supplier, SafelincsSupplierName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var error = snapshot.ErrorMessage ?? string.Empty;
        if (string.IsNullOrWhiteSpace(error))
        {
            return false;
        }

        return error.Contains("403", StringComparison.OrdinalIgnoreCase)
               || error.Contains("401", StringComparison.OrdinalIgnoreCase)
               || error.Contains("forbidden", StringComparison.OrdinalIgnoreCase)
               || error.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
               || error.Contains("access denied", StringComparison.OrdinalIgnoreCase)
               || error.Contains("captcha", StringComparison.OrdinalIgnoreCase)
               || error.Contains("cloudflare", StringComparison.OrdinalIgnoreCase)
               || error.Contains("too many requests", StringComparison.OrdinalIgnoreCase)
               || error.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || error.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || error.Contains("connection", StringComparison.OrdinalIgnoreCase)
               || error.Contains("dns", StringComparison.OrdinalIgnoreCase)
               || error.Contains("name or service not known", StringComparison.OrdinalIgnoreCase)
               || error.Contains("ssl", StringComparison.OrdinalIgnoreCase);
    }

    private async Task EnsureLoggedInAsync(HttpClient client)
    {
        if (string.IsNullOrWhiteSpace(_settings.Username) || string.IsNullOrWhiteSpace(_settings.Password))
        {
            throw new InvalidOperationException("Eden supplier credentials are not configured.");
        }

        using (var accountCheck = new HttpRequestMessage(HttpMethod.Get, "https://edenhorticulture.co.uk/account"))
        {
            AddBrowserHeaders(accountCheck);
            var accountResponse = await client.SendAsync(accountCheck);
            var accountHtml = await accountResponse.Content.ReadAsStringAsync();
            if (accountResponse.IsSuccessStatusCode && LooksAuthenticated(accountHtml))
            {
                return;
            }
        }

        var loginPageUrl = new Uri("https://edenhorticulture.co.uk/account/login", UriKind.Absolute);
        string loginPageHtml;
        using (var loginPageRequest = new HttpRequestMessage(HttpMethod.Get, loginPageUrl))
        {
            AddBrowserHeaders(loginPageRequest);
            var loginPageResponse = await client.SendAsync(loginPageRequest);
            loginPageHtml = await loginPageResponse.Content.ReadAsStringAsync();
            if (!loginPageResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Eden login page request failed with status {(int)loginPageResponse.StatusCode}.");
            }
        }

        var loginDoc = new HtmlDocument();
        loginDoc.LoadHtml(loginPageHtml);

        var loginForm = loginDoc.DocumentNode.SelectSingleNode("//form[.//input[@name='customer[email]'] and .//input[@name='customer[password]']]")
                        ?? loginDoc.DocumentNode.SelectSingleNode("//form[contains(@action,'/account/login')]");

        var loginAction = loginForm?.GetAttributeValue("action", null);
        var loginPath = string.IsNullOrWhiteSpace(loginAction) ? _settings.LoginPath : loginAction;

        var loginUrl = BuildAbsoluteEdenUrl(loginPath);

        var formValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (loginForm != null)
        {
            var inputNodes = loginForm.SelectNodes(".//input[@name]");
            if (inputNodes != null)
            {
                foreach (var input in inputNodes)
                {
                    var name = input.GetAttributeValue("name", string.Empty);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var value = input.GetAttributeValue("value", string.Empty);
                    if (!formValues.ContainsKey(name))
                    {
                        formValues[name] = value;
                    }
                }
            }
        }

        formValues["form_type"] = "customer_login";
        formValues["utf8"] = "✓";
        formValues["customer[email]"] = _settings.Username;
        formValues["customer[password]"] = _settings.Password;

        var loginResponse = await SendLoginRequestAsync(client, loginUrl, loginPageUrl, formValues);
        var loginHtml = await loginResponse.Content.ReadAsStringAsync();

        if (loginResponse.StatusCode == HttpStatusCode.Forbidden)
        {
            var fallbackLoginUrl = BuildAbsoluteEdenUrl("/account/login?return_url=%2Faccount");
            if (fallbackLoginUrl != loginUrl)
            {
                loginResponse.Dispose();
                loginResponse = await SendLoginRequestAsync(client, fallbackLoginUrl, loginPageUrl, formValues);
                loginHtml = await loginResponse.Content.ReadAsStringAsync();
            }
        }

        if (!loginResponse.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Eden login failed with status {(int)loginResponse.StatusCode}.");
        }

        if (!LooksAuthenticated(loginHtml))
        {
            using var accountRequest = new HttpRequestMessage(HttpMethod.Get, "https://edenhorticulture.co.uk/account");
            AddBrowserHeaders(accountRequest);
            var accountResponse = await client.SendAsync(accountRequest);
            var accountHtml = await accountResponse.Content.ReadAsStringAsync();
            if (!LooksAuthenticated(accountHtml))
            {
                throw new InvalidOperationException("Eden login appears unsuccessful. Check credentials or portal login flow.");
            }
        }
    }

    private static async Task<HttpResponseMessage> SendLoginRequestAsync(HttpClient client, Uri loginUrl, Uri referrer, Dictionary<string, string> formValues)
    {
        using var loginRequest = new HttpRequestMessage(HttpMethod.Post, loginUrl)
        {
            Content = new FormUrlEncodedContent(formValues)
        };
        AddBrowserHeaders(loginRequest);
        loginRequest.Headers.Referrer = referrer;
        loginRequest.Headers.TryAddWithoutValidation("Origin", "https://edenhorticulture.co.uk");
        return await client.SendAsync(loginRequest);
    }

    private async Task<string> FetchSupplierProductHtmlAsync(HttpClient client, Uri productUrl)
    {
        using var request = CreateProductPageRequest(productUrl);
        using var response = await client.SendAsync(request);

        if (response.StatusCode == HttpStatusCode.Forbidden || response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await EnsureLoggedInAsync(client);

            using var retryRequest = CreateProductPageRequest(productUrl);
            using var retryResponse = await client.SendAsync(retryRequest);

            if (retryResponse.StatusCode == HttpStatusCode.Forbidden || retryResponse.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException($"Eden product page request failed with status {(int)retryResponse.StatusCode} after re-login.");
            }

            retryResponse.EnsureSuccessStatusCode();
            return await retryResponse.Content.ReadAsStringAsync();
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task<string> FetchPublicProductHtmlAsync(HttpClient client, Uri productUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, productUrl);
        AddBrowserHeaders(request);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "none");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static HttpRequestMessage CreateProductPageRequest(Uri productUrl)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, productUrl);
        AddBrowserHeaders(request);
        request.Headers.Referrer = new Uri("https://edenhorticulture.co.uk/account", UriKind.Absolute);
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        request.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        return request;
    }

    private static Uri BuildAbsoluteEdenUrl(string pathOrUrl)
    {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            {
                return absolute;
            }

            if (absolute.Scheme == Uri.UriSchemeFile)
            {
                var normalizedPath = string.IsNullOrWhiteSpace(absolute.PathAndQuery)
                    ? "/account/login"
                    : absolute.PathAndQuery;

                if (!normalizedPath.StartsWith('/'))
                {
                    normalizedPath = "/" + normalizedPath;
                }

                return new Uri(new Uri("https://edenhorticulture.co.uk", UriKind.Absolute), normalizedPath);
            }

            throw new InvalidOperationException($"Unsupported URI scheme: {absolute.Scheme}");
        }

        return new Uri(new Uri("https://edenhorticulture.co.uk", UriKind.Absolute), pathOrUrl);
    }

    private static Uri BuildAbsoluteSafelincsUrl(string pathOrUrl)
    {
        if (Uri.TryCreate(pathOrUrl, UriKind.Absolute, out var absolute))
        {
            if (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps)
            {
                return absolute;
            }

            throw new InvalidOperationException($"Unsupported URI scheme: {absolute.Scheme}");
        }

        return new Uri(new Uri("https://www.safelincs.co.uk", UriKind.Absolute), pathOrUrl);
    }

    private static void AddBrowserHeaders(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        request.Headers.TryAddWithoutValidation("Accept-Language", "en-GB,en-US;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation("Cache-Control", "no-cache");
        request.Headers.TryAddWithoutValidation("Pragma", "no-cache");
    }

    private static bool LooksAuthenticated(string html)
    {
        var text = html ?? string.Empty;
        return text.Contains("/account/logout", StringComparison.OrdinalIgnoreCase)
               || text.Contains("Log out", StringComparison.OrdinalIgnoreCase)
               || text.Contains("My Account", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? ExtractPrice(HtmlDocument doc, string html)
    {
        string? candidate =
            doc.DocumentNode.SelectSingleNode("//meta[@property='product:price:amount']")?.GetAttributeValue("content", null)
            ?? doc.DocumentNode.SelectSingleNode("//*[contains(@class,'price-item--sale')]")?.InnerText
            ?? doc.DocumentNode.SelectSingleNode("//*[contains(@class,'price-item--regular')]")?.InnerText
            ?? doc.DocumentNode.SelectSingleNode("//*[contains(@class,'price') and contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'vat')]")?.InnerText;

        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var parsed = TryParsePrice(candidate);
            if (parsed.HasValue) return parsed;
        }

        foreach (Match match in Regex.Matches(html, @"£\s*(?<price>\d+(?:,\d{3})*(?:\.\d{1,2})?)"))
        {
            var raw = match.Groups["price"].Value;
            if (decimal.TryParse(raw.Replace(",", string.Empty), NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return null;
    }

    private static decimal? TryParsePrice(string input)
    {
        var cleaned = Regex.Replace(WebUtility.HtmlDecode(input ?? string.Empty), @"[^\d\.,]", string.Empty);
        cleaned = cleaned.Replace(",", string.Empty).Trim();
        if (decimal.TryParse(cleaned, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        return null;
    }

    private static string? ExtractStockText(HtmlDocument doc, string html)
    {
        var selectors = new[]
        {
            "//*[contains(@class,'stock')]",
            "//*[contains(@class,'availability')]",
            "//*[contains(@id,'stock')]",
            "//*[contains(@id,'availability')]",
            "//*[contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'out of stock')]",
            "//*[contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), 'in stock')]"
        };

        foreach (var selector in selectors)
        {
            var node = doc.DocumentNode.SelectSingleNode(selector);
            if (node == null) continue;

            var text = WebUtility.HtmlDecode(node.InnerText ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(text)) return text;
        }

        var bodyText = WebUtility.HtmlDecode(doc.DocumentNode.InnerText ?? html ?? string.Empty);
        var match = Regex.Match(bodyText, @"(out of stock|in stock|low stock|only\s+\d+\s+left|available|usually dispatched[^\r\n\.,;]*|pre-order)", RegexOptions.IgnoreCase);
        return match.Success ? match.Value.Trim() : null;
    }

    private static string MapStockStatus(string? stockText, string html)
    {
        var text = (stockText ?? string.Empty).ToLowerInvariant();
        if (text.Contains("out of stock") || text.Contains("sold out") || text.Contains("unavailable")) return "OutOfStock";
        if (text.Contains("low stock") || Regex.IsMatch(text, @"only\s+\d+\s+left")) return "Low";
        if (text.Contains("in stock") || text.Contains("available") || text.Contains("usually dispatched") || text.Contains("pre-order")) return "InStock";

        if (html.Contains("disabled", StringComparison.OrdinalIgnoreCase)
            && html.Contains("add to cart", StringComparison.OrdinalIgnoreCase))
        {
            return "OutOfStock";
        }

        return "Unknown";
    }

    private static SupplierStatusDto ToDto(SupplierListingSnapshot snapshot)
    {
        return new SupplierStatusDto
        {
            ListingId = snapshot.ListingId,
            Supplier = snapshot.Supplier,
            SupplierSku = snapshot.SupplierSku,
            SupplierPrice = snapshot.SupplierPrice,
            StockStatus = snapshot.StockStatus,
            StockText = snapshot.StockText,
            LastCheckedUtc = snapshot.CheckedAtUtc,
            IsSuccess = snapshot.IsSuccess,
            ErrorMessage = snapshot.ErrorMessage
        };
    }
}
