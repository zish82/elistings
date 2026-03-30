using HtmlAgilityPack;
using Shared;
using System.Net.Http;
using System.Linq;
using System.Text.RegularExpressions;

namespace Server.Services;

public class ScraperService
{
    private readonly HttpClient _httpClient;

    public ScraperService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        // Some sites block default .NET user agent
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/91.0.4472.124 Safari/537.36");
    }

    private string ResolveUrl(string baseUrl, string? relativeUrl)
    {
        if (string.IsNullOrEmpty(relativeUrl)) return "";
        if (relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return relativeUrl;
        
        try
        {
            var baseUri = new Uri(baseUrl);
            // Protocol-relative URLs
            if (relativeUrl.StartsWith("//")) return baseUri.Scheme + ":" + relativeUrl;

            // Absolute path
            if (relativeUrl.StartsWith("/")) return new Uri(baseUri, relativeUrl).AbsoluteUri;

            // If it's a simple root-relative path missing a leading slash (e.g. "templates_safelincs/.."), build from authority
            if (!relativeUrl.StartsWith("./") && !relativeUrl.StartsWith("../"))
            {
                return baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/" + relativeUrl.TrimStart('/');
            }

            var resolvedUri = new Uri(baseUri, relativeUrl);
            var final = resolvedUri.AbsoluteUri;
            // Normalize missing slash after authority (defensive fix for odd paths)
            var authority = baseUri.GetLeftPart(UriPartial.Authority);
            if (final.StartsWith(authority, StringComparison.OrdinalIgnoreCase) && final.Length > authority.Length)
            {
                if (final[authority.Length] != '/')
                {
                    final = authority.TrimEnd('/') + "/" + final.Substring(authority.Length);
                }
            }
            return final;
        }
        catch
        {
            return relativeUrl;
        }
    }

    public async Task<ExtractedDetailsDto> ExtractDetailsAsync(string url)
    {
        var html = await _httpClient.GetStringAsync(url);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var details = new ExtractedDetailsDto();

        bool isSafelincs = url.Contains("safelincs.co.uk");
        bool isEbay = url.Contains("ebay.co.uk") || url.Contains("ebay.com");
        bool isAmazon = url.Contains("amazon.co.uk") || url.Contains("amazon.com");

        // Title
        details.Title = (isSafelincs ? (doc.DocumentNode.SelectSingleNode("//h1")?.InnerText ?? doc.DocumentNode.SelectSingleNode("//h1[contains(@class,'product-title')]")?.InnerText) : null)
                        ?? (isEbay ? doc.DocumentNode.SelectSingleNode("//h1[contains(@class, 'x-item-title__mainTitle')]")?.InnerText : null)
                        ?? (isAmazon ? doc.DocumentNode.SelectSingleNode("//span[@id='productTitle']")?.InnerText : null)
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", "")
                        ?? doc.DocumentNode.SelectSingleNode("//title")?.InnerText.Trim()
                        ?? "";

        // Brand
        string? brand = null;
        if (isSafelincs)
        {
            brand = doc.DocumentNode.SelectSingleNode("//span[@itemprop='brand']")?.InnerText
                    ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:brand']")?.GetAttributeValue("content", null)
                    ?? doc.DocumentNode.SelectSingleNode("//meta[@name='brand']")?.GetAttributeValue("content", null)
                    ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'manufacturer')]//span")?.InnerText
                    ?? doc.DocumentNode.SelectSingleNode("//th[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), 'brand')]/following-sibling::td[1]")?.InnerText
                    ?? doc.DocumentNode.SelectSingleNode("//th[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), 'manufacturer')]/following-sibling::td[1]")?.InnerText;

            // If Safelincs has a "Technical Data" section, try to find Brand there (table or dl)
            if (string.IsNullOrWhiteSpace(brand))
            {
                var techHeading = doc.DocumentNode.SelectSingleNode("//h2[contains(translate(normalize-space(.),'TECHNICAL DATA','technical data'),'technical data')]")
                                 ?? doc.DocumentNode.SelectSingleNode("//h3[contains(translate(normalize-space(.),'TECHNICAL DATA','technical data'),'technical data')]");

                if (techHeading != null)
                {
                    var tbl = techHeading.SelectSingleNode("following-sibling::div//table") ?? techHeading.SelectSingleNode("following-sibling::table");
                    if (tbl != null)
                    {
                        var th = tbl.SelectSingleNode(".//th[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'brand')]");
                        if (th != null)
                        {
                            var td = th.SelectSingleNode("following-sibling::td[1]") ?? th.ParentNode.SelectSingleNode(".//td");
                            if (td != null) brand = td.InnerText.Trim();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(brand))
                    {
                        var dt = techHeading.SelectSingleNode("following-sibling::div//dt[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'brand')]")
                                 ?? techHeading.SelectSingleNode("following-sibling::dt[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'brand')]");
                        if (dt != null)
                        {
                            var dd = dt.SelectSingleNode("following-sibling::dd[1]") ?? dt.ParentNode.SelectSingleNode(".//dd[1]");
                            if (dd != null) brand = dd.InnerText.Trim();
                        }
                    }
                }
            }
        }

        brand = brand
                ?? (isEbay ? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'ux-layout-section--brand')]//span")?.InnerText : null)
                ?? (isAmazon ? doc.DocumentNode.SelectSingleNode("//a[@id='bylineInfo']")?.InnerText : null)
                ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:brand']")?.GetAttributeValue("content", "")
                ?? "";

        details.Brand = brand.Trim();
        
        // Amazon Brand cleaning: "Brand: FireAngel" -> "FireAngel"
        if (details.Brand.Contains("Brand:")) details.Brand = details.Brand.Replace("Brand:", "").Trim();
        if (details.Brand.Contains("Visit the")) details.Brand = details.Brand.Split(' ').Last();

        // Description - prefer HTML for product pages (Safelincs)
        if (isSafelincs)
        {
            var descNode = doc.DocumentNode.SelectSingleNode("//div[@itemprop='description']")
                           ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'product-description')]")
                           ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'tab-content')]");

            if (descNode != null)
            {
                var rawHtml = descNode.InnerHtml;
                details.Description = NormalizeHtmlFragment(rawHtml, url);
            }
        }

        if (string.IsNullOrEmpty(details.Description))
        {
            details.Description = (isAmazon ? doc.DocumentNode.SelectSingleNode("//div[@id='feature-bullets']")?.InnerText : null)
                              ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", "")
                              ?? doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "")
                              ?? "";
        }

        // Product Code / SKU extraction (common labels: 'Product code', 'Product code:', 'Part no', 'Part number', 'SKU')
        string? productCode = null;
        // Meta or itemprop
        productCode = productCode ?? doc.DocumentNode.SelectSingleNode("//meta[@name='product-code']")?.GetAttributeValue("content", null);
        productCode = productCode ?? doc.DocumentNode.SelectSingleNode("//span[@itemprop='sku']")?.InnerText;
        productCode = productCode ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'sku')]")?.InnerText;
        productCode = productCode ?? doc.DocumentNode.SelectSingleNode("//p[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), 'product code')]")?.InnerText;

        // Check technical data tables / dl lists
        if (string.IsNullOrWhiteSpace(productCode))
        {
            var techHeading = doc.DocumentNode.SelectSingleNode("//h2[contains(translate(normalize-space(.),'TECHNICAL DATA','technical data'),'technical data')]")
                             ?? doc.DocumentNode.SelectSingleNode("//h3[contains(translate(normalize-space(.),'TECHNICAL DATA','technical data'),'technical data')]");
            if (techHeading != null)
            {
                var tbl = techHeading.SelectSingleNode("following-sibling::div//table") ?? techHeading.SelectSingleNode("following-sibling::table");
                if (tbl != null)
                {
                    var th = tbl.SelectSingleNode(".//th[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'product code')]")
                             ?? tbl.SelectSingleNode(".//th[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'part no')]");
                    if (th != null)
                    {
                        var td = th.SelectSingleNode("following-sibling::td[1]") ?? th.ParentNode.SelectSingleNode(".//td");
                        if (td != null) productCode = td.InnerText.Trim();
                    }
                }

                if (string.IsNullOrWhiteSpace(productCode))
                {
                    var dt = techHeading.SelectSingleNode("following-sibling::div//dt[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'product code')]")
                             ?? techHeading.SelectSingleNode("following-sibling::dt[contains(translate(normalize-space(.),'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'),'part no')]");
                    if (dt != null)
                    {
                        var dd = dt.SelectSingleNode("following-sibling::dd[1]") ?? dt.ParentNode.SelectSingleNode(".//dd[1]");
                        if (dd != null) productCode = dd.InnerText.Trim();
                    }
                }
            }
        }

        details.ProductCode = string.IsNullOrWhiteSpace(productCode) ? null : productCode.Trim();

        // Category
        details.Category = (isSafelincs ? doc.DocumentNode.SelectSingleNode("//*[@id='breadcrumb']")?.InnerText.Replace("\n", " ").Trim() : null)
                           ?? (isEbay ? doc.DocumentNode.SelectSingleNode("//nav[contains(@class, 'breadcrumbs')]")?.InnerText : null)
                           ?? (isAmazon ? doc.DocumentNode.SelectSingleNode("//div[@id='wayfinding-breadcrumbs_container']")?.InnerText : null)
                           ?? doc.DocumentNode.SelectSingleNode("//meta[@property='product:category']")?.GetAttributeValue("content", "")
                           ?? doc.DocumentNode.SelectSingleNode("//meta[@name='keywords']")?.GetAttributeValue("content", "")?.Split(',').FirstOrDefault()?.Trim()
                           ?? doc.DocumentNode.SelectSingleNode("//nav[contains(@class, 'breadcrumb')]")?.InnerText.Replace("\n", " ").Trim()
                           ?? "";

        // Price Handling
        if (isSafelincs)
        {
            // Price - try several common selectors for Safelincs
            var incVatNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'large-price')]")
                             ?? doc.DocumentNode.SelectSingleNode("//span[contains(., 'incl') or contains(., 'Inc') or contains(., 'incl. VAT')]")
                             ?? doc.DocumentNode.SelectSingleNode("//p[contains(@class,'price')]")
                             ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'price')]");

            var exVatNode = doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'ex-vat')]")
                            ?? doc.DocumentNode.SelectSingleNode("//span[contains(., 'ex VAT')]")
                            ?? doc.DocumentNode.SelectSingleNode("//*[contains(text(), 'ex VAT')]");

            details.PriceIncVat = ParsePrice(incVatNode?.InnerText ?? incVatNode?.GetAttributeValue("content", null));
            details.PriceExVat = ParsePrice(exVatNode?.InnerText ?? exVatNode?.GetAttributeValue("content", null));
            details.Price = details.PriceIncVat ?? details.PriceExVat;
        }
        else
        {
            var priceStr = (isEbay ? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'x-price-primary')]")?.InnerText : null)
                            ?? (isAmazon ? doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'a-price')]//span[contains(@class, 'a-offscreen')]")?.InnerText : null)
                            ?? doc.DocumentNode.SelectSingleNode("//meta[@property='product:price:amount']")?.GetAttributeValue("content", "")
                            ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:price:amount']")?.GetAttributeValue("content", "")
                            ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'price')]")?.InnerText;
            details.Price = ParsePrice(priceStr);
        }

        // Images
        var imageUrls = new List<string>();
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", "");
        if (!string.IsNullOrEmpty(ogImage)) imageUrls.Add(ResolveUrl(url, ogImage));

        if (isSafelincs)
        {
             // Try multiple gallery/thumb selectors
             var safelincsImgNodes = doc.DocumentNode.SelectNodes("//ul[contains(@class, 'product-thumbs')]//img")
                                     ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'product-gallery')]//img")
                                     ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'product-image')]//img")
                                     ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'product-slider')]//img");

             if (safelincsImgNodes != null)
             {
                 foreach (var img in safelincsImgNodes)
                 {
                     var src = img.GetAttributeValue("data-src", null) ?? img.GetAttributeValue("data-large", null) ?? img.GetAttributeValue("src", null) ?? img.GetAttributeValue("srcset", null);
                     if (string.IsNullOrEmpty(src)) continue;
                     // Common Safelincs uses 'small' in filename for thumbnails
                     src = src.Replace("small", "large");
                     imageUrls.Add(ResolveUrl(url, src));
                 }
             }
        }
        else if (isEbay)
        {
            var ebayImages = doc.DocumentNode.SelectNodes("//div[contains(@class, 'ux-image-filmstrip-carousel')]//img")
                             ?.Select(img => img.GetAttributeValue("src", "").Replace("s-l64", "s-l1600"))
                             .Where(s => !string.IsNullOrEmpty(s));
            if (ebayImages != null) imageUrls.AddRange(ebayImages);
        }
        else if (isAmazon)
        {
            var amazonImage = doc.DocumentNode.SelectSingleNode("//img[@id='landingImage']")?.GetAttributeValue("data-old-hires", "");
            if (string.IsNullOrEmpty(amazonImage)) amazonImage = doc.DocumentNode.SelectSingleNode("//img[@id='landingImage']")?.GetAttributeValue("src", "");
            if (!string.IsNullOrEmpty(amazonImage)) imageUrls.Add(amazonImage);
        }

        details.ImageUrls = imageUrls.Distinct().ToList();

        if (!string.IsNullOrEmpty(details.Description) && !isSafelincs)
        {
            // For plain-text descriptions (non-Safelincs), collapse whitespace and limit length
            details.Description = System.Text.RegularExpressions.Regex.Replace(details.Description, @"\s+", " ").Trim();
            if (details.Description.Length > 2000) details.Description = details.Description.Substring(0, 1997) + "...";
        }


        // If brand still empty, try to infer from title (e.g., "BLACK+DECKER Mains Heat Alarm...")
        if (string.IsNullOrWhiteSpace(details.Brand) && !string.IsNullOrWhiteSpace(details.Title))
        {
            var m = Regex.Match(details.Title.Trim(), @"^([A-Z0-9\+\-&]{2,}(?:[ \+\-&][A-Z0-9\+\-&]+)*)");
            if (m.Success) details.Brand = m.Groups[1].Value.Trim();
        }

        return details;
    }

    public async Task<List<string>> ExtractProductLinksAsync(string categoryUrl)
    {
        Console.WriteLine($"[Scraper] Extracting product links from category: {categoryUrl}");
        var html = await _httpClient.GetStringAsync(categoryUrl);
        var doc = new HtmlDocument();
        doc.LoadHtml(html);

        var links = new HashSet<string>();
        
        // Find all links that might be products. 
        // Safelincs products are usually within divs with specific classes like 'product-card' but we'll be broad.
        var nodes = doc.DocumentNode.SelectNodes("//a[@href]");

        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                var href = node.GetAttributeValue("href", "");
                if (string.IsNullOrEmpty(href) || href == "#" || href.Contains("javascript:")) continue;

                var fullUrl = ResolveUrl(categoryUrl, href);
                
                // Remove query parameters and fragments for uniqueness
                if (fullUrl.Contains("?")) fullUrl = fullUrl.Split('?')[0];
                if (fullUrl.Contains("#")) fullUrl = fullUrl.Split('#')[0];
                if (!fullUrl.EndsWith("/")) fullUrl += "/";

                if (IsProductUrl(fullUrl))
                {
                    links.Add(fullUrl);
                }
            }
        }

        Console.WriteLine($"[Scraper] Found {links.Count} potential product links.");
        return links.ToList();
    }

    private bool IsProductUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;
        
        // Exclude common non-product pages
        var exclusions = new[] { "/cart/", "/login/", "/contact-us/", "/about-us/", "/terms-and-conditions/", "/privacy-policy/", "/blog/", "/my-account/", "/basket/", "/cookie-policy/", "/customer-reviews/" };
        if (exclusions.Any(e => url.Contains(e, StringComparison.OrdinalIgnoreCase))) return false;

        // Safelincs specific: 
        if (!url.Contains("safelincs.co.uk")) return false;

        var uri = new Uri(url);
        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path)) return false;
        
        // Heuristic: Products have descriptive slugs with multiple hyphens.
        // Categories like "heat-alarms" or "smoke-alarms" usually have just one hyphen or few words.
        // Products like "fireangel-w2-heat-alarm" have more.
        var hyphenCount = path.Count(c => c == '-');
        
        // Most categories on Safelincs are 1-2 words. Most products are 3+.
        return hyphenCount >= 3 && path.Length > 15;
    }

    private decimal? ParsePrice(string? priceStr)
    {
        if (string.IsNullOrEmpty(priceStr)) return null;
        var match = System.Text.RegularExpressions.Regex.Match(priceStr, @"[0-9]+(\.[0-9]{1,2})?");
        if (match.Success && decimal.TryParse(match.Value, out var price)) return price;
        return null;
    }

    private string NormalizeHtmlFragment(string html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var fragDoc = new HtmlDocument();
        // Load as a fragment inside a container element
        fragDoc.LoadHtml("<div id=\"frag\">" + html + "</div>");

        // Remove scripts and styles
        var scripts = fragDoc.DocumentNode.SelectNodes("//script|//style");
        if (scripts != null)
        {
            foreach (var s in scripts) s.Remove();
        }

        // Fix image urls and links
        var imgNodes = fragDoc.DocumentNode.SelectNodes("//img");
        if (imgNodes != null)
        {
            foreach (var img in imgNodes)
            {
                var src = img.GetAttributeValue("data-src", null) ?? img.GetAttributeValue("data-large", null) ?? img.GetAttributeValue("src", null) ?? img.GetAttributeValue("srcset", null);
                if (!string.IsNullOrEmpty(src))
                {
                    var resolved = ResolveUrl(baseUrl, src);
                    img.SetAttributeValue("src", resolved);
                    img.Attributes.Remove("srcset");
                    img.Attributes.Remove("data-src");
                    img.Attributes.Remove("data-large");
                }
            }
        }

        var linkNodes = fragDoc.DocumentNode.SelectNodes("//a[@href]");
        if (linkNodes != null)
        {
            foreach (var a in linkNodes)
            {
                var href = a.GetAttributeValue("href", null);
                if (!string.IsNullOrEmpty(href) && !href.StartsWith("mailto:") && !href.StartsWith("javascript:"))
                {
                    a.SetAttributeValue("href", ResolveUrl(baseUrl, href));
                }
            }
        }

        // Remove event handler attributes for safety
        var allNodes = fragDoc.DocumentNode.SelectNodes("//*");
        if (allNodes != null)
        {
            foreach (var n in allNodes)
            {
                var attrs = n.Attributes.ToList();
                foreach (var at in attrs)
                {
                    if (at.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                        n.Attributes.Remove(at.Name);
                    // strip javascript: URLs
                    if ((at.Name == "href" || at.Name == "src") && at.Value != null && at.Value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        n.Attributes.Remove(at.Name);
                }
            }
        }

        var container = fragDoc.GetElementbyId("frag");
        return container?.InnerHtml.Trim() ?? string.Empty;
    }
}

