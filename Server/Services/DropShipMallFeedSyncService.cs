using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Server.Configuration;
using Server.Data;
using Shared;
using System.Globalization;
using System.Net;
using System.Text;

namespace Server.Services;

/// <summary>
/// Imports DropShipMall products and stock.
///
/// Feeds (all CSV, comma-delimited):
///  - Product Catalogue (/trade/catalogue/products) – login required. Columns: Product Code, Product Name,
///    Images (space-separated), Discontinued, Barcode, Primary Category, Sold As, Piece Price, ...
///  - Shopify Catalogue (/trade/catalogue/shopify) – login required. Shopify import format
///    (Handle, Title, Body(HTML), Variant SKU, Variant Price, Image Src, ...). Only one image per product.
///  - Stock Feed (/trade/catalogue/stock) – public. Columns: Product Code, Stock Level.
///
/// Neither catalogue currently contains product descriptions.
/// The CSVs contain unescaped quotes inside values (e.g. 1.5" Steel Pins), so a lenient parser is used.
/// </summary>
public class DropShipMallFeedSyncService : IFeedProductSyncService
{
    private const string Supplier = "DropShipMall";

    private readonly AppDbContext _context;
    private readonly DropShipMallSettings _settings;
    private readonly SupplierCatalogService _catalog;

    public DropShipMallFeedSyncService(AppDbContext context, IOptions<DropShipMallSettings> settings, SupplierCatalogService catalog)
    {
        _context = context;
        _settings = settings.Value;
        _catalog = catalog;
    }

    // Hangfire retry policy lives on IFeedProductSyncService (the job is registered via the interface).
    public async Task RunCatalogueImportAsync()
    {
        var result = await ImportCatalogueAsync("scheduler-catalogue");
        LogResult(result);
    }

    public async Task RunStockRefreshAsync()
    {
        var result = await RefreshStockAsync("scheduler-stock");
        LogResult(result);
    }

    public async Task<FeedSyncResult> ImportCatalogueAsync(string triggeredBy = "manual")
    {
        var feeds = await GetCatalogueFeedCandidatesAsync();
        if (!feeds.Any())
        {
            return new FeedSyncResult
            {
                Supplier = Supplier,
                FeedType = "Catalogue Import",
                ErrorMessage = "No DropShipMall catalogue feed configured. Add a Product Catalogue or Shopify Catalogue URL."
            };
        }

        var attemptErrors = new List<string>();
        foreach (var feed in feeds)
        {
            var result = await ImportCatalogueFeedAsync(feed, triggeredBy);
            if (result.IsSuccess) return result;
            attemptErrors.Add($"{feed.FeedType}: {result.ErrorMessage}");
        }

        return new FeedSyncResult
        {
            Supplier = Supplier,
            FeedType = "Catalogue Import",
            ErrorMessage = "Unable to import catalogue. " + string.Join(" ", attemptErrors)
        };
    }

    public async Task<FeedSyncResult> RefreshStockAsync(string triggeredBy = "manual")
    {
        var feed = await FindFeedAsync("Stock Feed");
        return feed == null
            ? new FeedSyncResult { Supplier = Supplier, FeedType = "Stock Feed", ErrorMessage = "Stock Feed not configured for DropShipMall." }
            : await RefreshStockAsync(triggeredBy, feed);
    }

    public async Task<FeedSyncResult> SyncFeedAsync(int feedId, string triggeredBy = "manual")
    {
        var feed = await _context.SupplierFeeds.FirstOrDefaultAsync(f => f.Id == feedId);
        if (feed == null) return new FeedSyncResult { ErrorMessage = "Supplier feed was not found." };
        if (!feed.Supplier.Equals(Supplier, StringComparison.OrdinalIgnoreCase))
        {
            return new FeedSyncResult { Supplier = feed.Supplier, FeedType = feed.FeedType, ErrorMessage = $"No sync handler is configured for supplier '{feed.Supplier}'." };
        }

        return feed.FeedType.Contains("stock", StringComparison.OrdinalIgnoreCase)
            ? await RefreshStockAsync(triggeredBy, feed)
            : await ImportCatalogueFeedAsync(feed, triggeredBy);
    }

    // ---------------------------------------------------------------- stock

    private async Task<FeedSyncResult> RefreshStockAsync(string triggeredBy, SupplierFeed feed)
    {
        var result = new FeedSyncResult { Supplier = Supplier, FeedType = feed.FeedType };
        try
        {
            var rows = ParseCsv(await DownloadFeedAsync(feed));
            if (!rows.Any())
            {
                result.ErrorMessage = "Stock feed returned no rows. Check that the URL points to a CSV feed.";
                return result;
            }

            var productsBySku = await LoadProductsBySkuAsync();
            var supplierSettings = await _catalog.FindAsync(Supplier, null);
            var listingsByProduct = (await _context.Listings
                    .Where(l => l.SupplierProductId != null)
                    .ToListAsync())
                .GroupBy(l => l.SupplierProductId!.Value)
                .ToDictionary(g => g.Key, g => g.ToList());

            foreach (var row in rows)
            {
                result.ProcessedRows++;
                var sku = Pick(row, "Product Code", "SKU", "Variant SKU", "Code");
                if (sku == null || !productsBySku.TryGetValue(sku, out var product)) { result.Skipped++; continue; }

                var stock = ParseInt(Pick(row, "Stock Level", "Stock", "Quantity", "Qty", "Variant Inventory Qty"));
                if (stock.HasValue) product.StockLevel = stock;

                var price = ParseDecimal(Pick(row, "Piece Price", "Price", "Supplier Price", "Cost"));
                if (price.HasValue)
                {
                    product.SupplierPrice = price;
                    product.SuggestedPrice = PricingMath.SuggestItemPrice(PricingMath.CostIncVat(product.SupplierPrice, supplierSettings), supplierSettings?.DefaultMarginPercent ?? 20m);
                }

                product.UpdatedAtUtc = DateTime.UtcNow;
                result.Updated++;

                if (listingsByProduct.TryGetValue(product.Id, out var linked))
                {
                    foreach (var listing in linked)
                    {
                        listing.SourcePrice = product.SupplierPrice;
                        listing.SourceStockLevel = product.StockLevel;
                        if (string.IsNullOrWhiteSpace(listing.SourceProductCode)) listing.SourceProductCode = product.SupplierSku;
                        result.LinkedListingsUpdated++;
                    }
                }
            }

            await _context.SaveChangesAsync();
            Console.WriteLine($"[FeedSync] {triggeredBy} stock refresh rows={result.ProcessedRows}, updated={result.Updated}, skipped={result.Skipped}");
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    // ------------------------------------------------------------ catalogue

    private async Task<FeedSyncResult> ImportCatalogueFeedAsync(SupplierFeed feed, string triggeredBy)
    {
        var result = new FeedSyncResult { Supplier = Supplier, FeedType = feed.FeedType };
        try
        {
            var rows = ParseCsv(await DownloadFeedAsync(feed));
            if (!rows.Any()) { result.ErrorMessage = $"{feed.FeedType} returned no rows."; return result; }
            if (!rows[0].ContainsKey(NormalizeKey("Product Name")) && !rows[0].ContainsKey(NormalizeKey("Title")))
            {
                result.ErrorMessage = $"{feed.FeedType} has no Product Name/Title column (looks like a stock-only feed). Import skipped so existing product details are not overwritten.";
                return result;
            }

            var supplierSettings = await _catalog.FindAsync(Supplier, null);

            // Load all existing DropShipMall products once instead of one query per row.
            var existing = await _context.SupplierProducts.Where(p => p.Supplier == Supplier).ToListAsync();
            var bySku = new Dictionary<string, SupplierProduct>(StringComparer.OrdinalIgnoreCase);
            var byExternalId = new Dictionary<string, SupplierProduct>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in existing.OrderBy(p => p.Id))
            {
                if (!string.IsNullOrWhiteSpace(p.SupplierSku)) bySku.TryAdd(p.SupplierSku, p);
                if (!string.IsNullOrWhiteSpace(p.ExternalProductId)) byExternalId.TryAdd(p.ExternalProductId, p);
                if (!string.IsNullOrWhiteSpace(p.Handle)) byExternalId.TryAdd(p.Handle, p);
            }

            var discontinued = 0;
            foreach (var row in rows)
            {
                result.ProcessedRows++;

                var sku = Pick(row, "Product Code", "Variant SKU", "SKU", "Code");
                var handle = Pick(row, "Handle", "Slug");
                var externalId = FirstNonEmpty(sku, handle, Pick(row, "Variant ID", "Product ID", "ID"));
                var title = Pick(row, "Product Name", "Title");
                if (string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(title))
                {
                    result.Skipped++;
                    continue;
                }

                SupplierProduct? entity = null;
                if (sku != null) bySku.TryGetValue(sku, out entity);
                if (entity == null) byExternalId.TryGetValue(externalId, out entity);

                var isDiscontinued = string.Equals(Pick(row, "Discontinued"), "Yes", StringComparison.OrdinalIgnoreCase);
                if (isDiscontinued)
                {
                    // Discontinued products are never imported; ones imported earlier are deactivated.
                    if (entity != null && entity.IsActive)
                    {
                        entity.IsActive = false;
                        entity.UpdatedAtUtc = DateTime.UtcNow;
                    }
                    discontinued++;
                    result.Skipped++;
                    continue;
                }

                if (entity == null)
                {
                    entity = new SupplierProduct
                    {
                        Supplier = Supplier,
                        ExternalProductId = externalId,
                        SupplierSku = sku,
                        Handle = handle,
                        CreatedAtUtc = DateTime.UtcNow,
                        IsActive = true
                    };
                    _context.SupplierProducts.Add(entity);
                    if (sku != null) bySku[sku] = entity;
                    byExternalId[externalId] = entity;
                    result.Inserted++;
                }
                else
                {
                    result.Updated++;
                }

                // Only overwrite fields the feed actually provides, so a sparser feed never blanks
                // out data from a richer one. IsActive is never set back to true here: products
                // deleted on /products must stay deleted.
                entity.SupplierFeedId = feed.Id;
                entity.SupplierSku ??= sku;
                entity.Handle ??= handle;
                entity.Title = CleanText(title.Replace("\"\"", "\""));

                var description = Pick(row, "Body (HTML)", "Description");
                if (description != null) entity.Description = description;

                var brand = Pick(row, "Vendor", "Brand");
                if (brand != null) entity.Brand = brand;

                var category = Pick(row, "Primary Category", "Product Category", "Category");
                if (category != null) entity.CategoryName = CleanText(category);

                var images = ParseImageUrls(Pick(row, "Images", "Image Src", "Image URLs", "Main Image"));
                if (images.Length > 0)
                {
                    entity.ImageUrl = images[0];
                    entity.ImageUrlsJson = System.Text.Json.JsonSerializer.Serialize(images);
                }

                var price = ParseDecimal(Pick(row, "Piece Price", "Variant Price", "Cost per item", "Price", "Supplier Price"));
                if (price.HasValue)
                {
                    entity.SupplierPrice = price;
                    entity.SuggestedPrice = PricingMath.SuggestItemPrice(PricingMath.CostIncVat(price, supplierSettings), supplierSettings?.DefaultMarginPercent ?? 20m);
                }

                var stock = ParseInt(Pick(row, "Variant Inventory Qty", "Stock Level", "Stock", "Quantity"));
                if (stock.HasValue) entity.StockLevel = stock;

                var postage = Pick(row, "Postage Category");
                if (postage != null) entity.PostageCategory = postage;

                var barcode = Pick(row, "Barcode", "Variant Barcode");
                if (barcode != null) entity.Barcode = barcode;

                entity.Currency = "GBP";
                entity.RawPayloadJson = System.Text.Json.JsonSerializer.Serialize(row);
                entity.UpdatedAtUtc = DateTime.UtcNow;

                if (result.ProcessedRows % 2000 == 0)
                {
                    await _context.SaveChangesAsync();
                }
            }

            await _context.SaveChangesAsync();
            Console.WriteLine($"[FeedSync] {triggeredBy} imported feedType={feed.FeedType} rows={result.ProcessedRows}, inserted={result.Inserted}, updated={result.Updated}, skipped={result.Skipped}, discontinued={discontinued}");
            return result;
        }
        catch (Exception ex)
        {
            result.ErrorMessage = ex.Message;
            return result;
        }
    }

    private async Task<Dictionary<string, SupplierProduct>> LoadProductsBySkuAsync()
    {
        var products = await _context.SupplierProducts
            .Where(p => p.Supplier == Supplier && p.SupplierSku != null)
            .OrderBy(p => p.Id)
            .ToListAsync();

        var map = new Dictionary<string, SupplierProduct>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in products) map.TryAdd(p.SupplierSku!, p);
        return map;
    }

    private async Task<SupplierFeed?> FindFeedAsync(string feedType)
    {
        return await _context.SupplierFeeds
            .Where(f => f.Supplier == Supplier && f.FeedType == feedType)
            .OrderByDescending(f => f.IsRecommended)
            .ThenByDescending(f => f.Id)
            .FirstOrDefaultAsync();
    }

    private async Task<List<SupplierFeed>> GetCatalogueFeedCandidatesAsync()
    {
        // Product Catalogue first: it has all images, the Discontinued flag and barcode.
        // Stock Feed is deliberately excluded – it only has Product Code + Stock Level.
        var preferredOrder = new[] { "Product Catalogue", "Shopify Catalogue" };

        var feeds = await _context.SupplierFeeds
            .Where(f => f.Supplier == Supplier && preferredOrder.Contains(f.FeedType))
            .ToListAsync();

        return feeds
            .OrderBy(f => Array.IndexOf(preferredOrder, f.FeedType))
            .ThenByDescending(f => f.IsRecommended)
            .ThenByDescending(f => f.Id)
            .ToList();
    }

    // ------------------------------------------------------------- download

    /// <summary>
    /// Downloads a feed. Uses the feed's saved Cookie header if present; otherwise, if the response
    /// is a login/HTML page and DropShipMall credentials are configured, logs in and retries.
    /// </summary>
    private async Task<string> DownloadFeedAsync(SupplierFeed feed)
    {
        var cookies = new CookieContainer();
        using var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = string.IsNullOrWhiteSpace(feed.CookieHeader),
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36");

        var (ok, content, failure) = await TryDownloadAsync(client, feed);
        if (ok) return content!;

        if (string.IsNullOrWhiteSpace(feed.CookieHeader) && _settings.HasCredentials)
        {
            await LoginAsync(client);
            (ok, content, failure) = await TryDownloadAsync(client, feed);
            if (ok) return content!;
            throw new InvalidOperationException($"{feed.FeedType}: still not authorised after logging in to DropShipMall ({failure}). Check DropShipMall:Username and DropShipMall:Password.");
        }

        throw new InvalidOperationException(failure + (_settings.HasCredentials || !string.IsNullOrWhiteSpace(feed.CookieHeader)
            ? string.Empty
            : " This feed needs a logged-in trade account: set DropShipMall:Username and DropShipMall:Password in appsettings.Development.json."));
    }

    private static async Task<(bool Ok, string? Content, string? Failure)> TryDownloadAsync(HttpClient client, SupplierFeed feed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, feed.Url);
        request.Headers.TryAddWithoutValidation("Accept", "text/csv,text/plain,*/*");
        if (!string.IsNullOrWhiteSpace(feed.CookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", feed.CookieHeader);
        }

        using var response = await client.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return (false, null, $"{feed.FeedType} returned HTTP {(int)response.StatusCode}.");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (bytes.Length == 0)
        {
            return (false, null, $"{feed.FeedType} returned an empty response (not logged in).");
        }

        if (LooksLikeExcelFile(bytes))
        {
            return (false, null, $"{feed.FeedType} returned an Excel file; a CSV feed URL is required.");
        }

        var text = DecodeFeedText(bytes);
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("<", StringComparison.Ordinal))
        {
            return (false, null, $"{feed.FeedType} returned an HTML page instead of CSV (not logged in).");
        }

        return (true, text, null);
    }

    private async Task LoginAsync(HttpClient client)
    {
        var baseUri = new Uri(_settings.BaseUrl.TrimEnd('/') + "/");
        var loginUri = new Uri(baseUri, _settings.LoginPath.TrimStart('/'));

        // Load the login page first so any session cookie is issued before posting.
        using (var _ = await client.GetAsync(loginUri)) { }

        using var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = _settings.Username,
            ["password"] = _settings.Password,
            ["userRemember"] = "on",
            ["submit"] = "Login"
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, loginUri) { Content = form };
        request.Headers.Referrer = loginUri;
        using var response = await client.SendAsync(request);
        Console.WriteLine($"[FeedSync] DropShipMall login responded {(int)response.StatusCode} ({response.RequestMessage?.RequestUri?.PathAndQuery}).");
    }

    private static string DecodeFeedText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }
        return Encoding.UTF8.GetString(bytes);
    }

    private static bool LooksLikeExcelFile(byte[] bytes)
    {
        var isZip = bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04;
        var isOle = bytes.Length >= 8 && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0
                    && bytes[4] == 0xA1 && bytes[5] == 0xB1 && bytes[6] == 0x1A && bytes[7] == 0xE1;
        return isZip || isOle;
    }

    // ---------------------------------------------------------------- parse

    /// <summary>
    /// Lenient CSV parser: quoted fields may span lines; a quote only opens a quoted field at the
    /// start of a field, and only closes it when followed by a delimiter/newline/end. This copes with
    /// DropShipMall values like <c>1.5" Steel Pins</c> that contain unescaped quotes.
    /// Row keys are normalised (see <see cref="NormalizeKey"/>).
    /// </summary>
    internal static List<Dictionary<string, string>> ParseCsv(string content)
    {
        var records = new List<List<string>>();
        var firstLineEnd = content.IndexOf('\n');
        var delimiter = DetectDelimiter(firstLineEnd >= 0 ? content[..firstLineEnd] : content);

        var row = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var atFieldStart = true;

        void EndField() { row.Add(field.ToString()); field.Clear(); atFieldStart = true; }
        void EndRow()
        {
            EndField();
            if (row.Count > 1 || row[0].Length > 0) records.Add(row);
            row = new List<string>();
        }

        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (inQuotes)
            {
                if (c == '\\' && i + 1 < content.Length && content[i + 1] == '"')
                {
                    field.Append('"'); i++; // backslash-escaped quote, e.g. "Cypress spray 21.5\""
                }
                else if (c == '"')
                {
                    var next = i + 1 < content.Length ? content[i + 1] : '\0';
                    if (next == '"') { field.Append('"'); i++; }
                    else if (next == delimiter || next == '\n' || next == '\r' || next == '\0') inQuotes = false;
                    else field.Append(c);
                }
                else field.Append(c);
                continue;
            }

            if (c == '"' && atFieldStart) { inQuotes = true; atFieldStart = false; }
            else if (c == delimiter) EndField();
            else if (c == '\n') EndRow();
            else if (c == '\r') { /* ignore */ }
            else { field.Append(c); atFieldStart = false; }
        }
        if (field.Length > 0 || row.Count > 0) EndRow();

        var rows = new List<Dictionary<string, string>>();
        if (records.Count == 0) return rows;

        var headers = records[0].Select(NormalizeKey).ToList();
        var malformed = 0;
        foreach (var values in records.Skip(1))
        {
            // A row with the wrong number of columns would put values (e.g. price) in the wrong fields.
            if (values.Count != headers.Count)
            {
                malformed++;
                continue;
            }

            var dict = new Dictionary<string, string>();
            for (var h = 0; h < headers.Count; h++)
            {
                if (string.IsNullOrEmpty(headers[h])) continue;
                dict[headers[h]] = h < values.Count ? values[h].Trim() : string.Empty;
            }
            rows.Add(dict);
        }
        if (malformed > 0) Console.WriteLine($"[FeedSync] Skipped {malformed} malformed CSV row(s).");
        return rows;
    }

    private static char DetectDelimiter(string headerLine)
    {
        var comma = headerLine.Count(c => c == ',');
        var tab = headerLine.Count(c => c == '\t');
        var semicolon = headerLine.Count(c => c == ';');
        if (tab > comma && tab >= semicolon) return '\t';
        if (semicolon > comma) return ';';
        return ',';
    }

    /// <summary>"Body (HTML)", "Body(HTML)" and "body_html" all become "bodyhtml".</summary>
    private static string NormalizeKey(string key)
        => new string(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static string? Pick(Dictionary<string, string> row, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (row.TryGetValue(NormalizeKey(key), out var value)
                && !string.IsNullOrWhiteSpace(value)
                && !value.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                return value.Trim();
            }
        }
        return null;
    }

    private static string[] ParseImageUrls(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();

        return raw
            .Split(new[] { ' ', '\t', '\r', '\n', ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => v.Trim())
            .Where(v => Uri.TryCreate(v, UriKind.Absolute, out var uri)
                        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The feeds contain HTML-encoded text (e.g. "Oil Burner &amp;amp; Wax Melts", "2.5&amp;quot;").
    /// Decode it (twice, for double-encoded values) and collapse whitespace.
    /// </summary>
    private static string CleanText(string value)
    {
        var decoded = WebUtility.HtmlDecode(WebUtility.HtmlDecode(value));
        return System.Text.RegularExpressions.Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static decimal? ParseDecimal(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var cleaned = input.Replace("£", string.Empty).Replace(",", string.Empty).Trim();
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static int? ParseInt(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        return int.TryParse(input.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static void LogResult(FeedSyncResult result)
    {
        if (!result.IsSuccess)
        {
            Console.WriteLine($"[FeedSync] {result.FeedType} failed: {result.ErrorMessage}");
            return;
        }

        Console.WriteLine($"[FeedSync] {result.FeedType} complete. processed={result.ProcessedRows}, inserted={result.Inserted}, updated={result.Updated}, skipped={result.Skipped}, linkedListings={result.LinkedListingsUpdated}");
    }
}
