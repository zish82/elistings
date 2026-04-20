using HtmlAgilityPack;
using Shared;
using System.Text.RegularExpressions;

namespace Server.Services;

public class SafelincsProductScraper : ProductScraperBase
{
    public override bool CanHandle(string url) => IsHostMatch(url, "safelincs.co.uk");

    public override Task<ExtractedDetailsDto> ExtractDetailsAsync(string url, HtmlDocument doc, string html)
    {
        var details = new ExtractedDetailsDto();

        details.Title = (doc.DocumentNode.SelectSingleNode("//h1")?.InnerText
                        ?? doc.DocumentNode.SelectSingleNode("//h1[contains(@class,'product-title')]")?.InnerText
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", "")
                        ?? doc.DocumentNode.SelectSingleNode("//title")?.InnerText
                        ?? string.Empty).Trim();

        string? brand = doc.DocumentNode.SelectSingleNode("//span[@itemprop='brand']")?.InnerText
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:brand']")?.GetAttributeValue("content", null)
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@name='brand']")?.GetAttributeValue("content", null)
                        ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'manufacturer')]//span")?.InnerText
                        ?? doc.DocumentNode.SelectSingleNode("//th[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), 'brand')]/following-sibling::td[1]")?.InnerText
                        ?? doc.DocumentNode.SelectSingleNode("//th[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), 'manufacturer')]/following-sibling::td[1]")?.InnerText;

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

        details.Brand = (brand ?? string.Empty).Trim();

        var descNode = doc.DocumentNode.SelectSingleNode("//div[@itemprop='description']")
                       ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class,'product-description')]")
                       ?? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'tab-content')]");

        if (descNode != null)
        {
            details.Description = NormalizeHtmlFragment(descNode.InnerHtml, url);
        }

        if (string.IsNullOrWhiteSpace(details.Description))
        {
            details.Description = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", "")
                              ?? doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "")
                              ?? string.Empty;
        }

        string? productCode = doc.DocumentNode.SelectSingleNode("//meta[@name='product-code']")?.GetAttributeValue("content", null)
                            ?? doc.DocumentNode.SelectSingleNode("//span[@itemprop='sku']")?.InnerText
                            ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'sku')]")?.InnerText
                            ?? doc.DocumentNode.SelectSingleNode("//p[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), 'product code')]")?.InnerText;

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
            }
        }

        details.ProductCode = string.IsNullOrWhiteSpace(productCode) ? null : productCode.Trim();

        details.Category = doc.DocumentNode.SelectSingleNode("//*[@id='breadcrumb']")?.InnerText.Replace("\n", " ").Trim()
                           ?? doc.DocumentNode.SelectSingleNode("//meta[@property='product:category']")?.GetAttributeValue("content", "")
                           ?? doc.DocumentNode.SelectSingleNode("//meta[@name='keywords']")?.GetAttributeValue("content", "")?.Split(',').FirstOrDefault()?.Trim()
                           ?? doc.DocumentNode.SelectSingleNode("//nav[contains(@class, 'breadcrumb')]")?.InnerText.Replace("\n", " ").Trim()
                           ?? string.Empty;

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

        var imageUrls = new List<string>();
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", "");
        if (!string.IsNullOrEmpty(ogImage)) imageUrls.Add(ResolveUrl(url, ogImage));

        var safelincsImgNodes = doc.DocumentNode.SelectNodes("//ul[contains(@class, 'product-thumbs')]//img")
                                 ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'product-gallery')]//img")
                                 ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'product-image')]//img")
                                 ?? doc.DocumentNode.SelectNodes("//div[contains(@class,'product-slider')]//img");

        if (safelincsImgNodes != null)
        {
            foreach (var img in safelincsImgNodes)
            {
                var src = img.GetAttributeValue("data-src", null)
                          ?? img.GetAttributeValue("data-large", null)
                          ?? img.GetAttributeValue("src", null)
                          ?? img.GetAttributeValue("srcset", null);
                if (string.IsNullOrEmpty(src)) continue;
                src = src.Replace("small", "large");
                imageUrls.Add(ResolveUrl(url, src));
            }
        }

        details.ImageUrls = imageUrls.Distinct().ToList();

        if (string.IsNullOrWhiteSpace(details.Brand) && !string.IsNullOrWhiteSpace(details.Title))
        {
            var m = Regex.Match(details.Title.Trim(), @"^([A-Z0-9\+\-&]{2,}(?:[ \+\-&][A-Z0-9\+\-&]+)*)");
            if (m.Success) details.Brand = m.Groups[1].Value.Trim();
        }

        return Task.FromResult(details);
    }

    public override Task<List<string>> ExtractProductLinksAsync(string categoryUrl, HtmlDocument doc, string html)
    {
        Console.WriteLine($"[Scraper] Extracting product links from category: {categoryUrl}");

        var links = new HashSet<string>();
        var nodes = doc.DocumentNode.SelectNodes("//a[@href]");

        if (nodes != null)
        {
            foreach (var node in nodes)
            {
                var href = node.GetAttributeValue("href", "");
                if (string.IsNullOrEmpty(href) || href == "#" || href.Contains("javascript:")) continue;

                var fullUrl = ResolveUrl(categoryUrl, href);

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
        return Task.FromResult(links.ToList());
    }

    private bool IsProductUrl(string url)
    {
        if (string.IsNullOrEmpty(url)) return false;

        var exclusions = new[]
        {
            "/cart/", "/login/", "/contact-us/", "/about-us/", "/terms-and-conditions/",
            "/privacy-policy/", "/blog/", "/my-account/", "/basket/", "/cookie-policy/", "/customer-reviews/"
        };
        if (exclusions.Any(e => url.Contains(e, StringComparison.OrdinalIgnoreCase))) return false;

        if (!CanHandle(url)) return false;

        var uri = new Uri(url);
        var path = uri.AbsolutePath.Trim('/');
        if (string.IsNullOrEmpty(path)) return false;

        var hyphenCount = path.Count(c => c == '-');
        return hyphenCount >= 3 && path.Length > 15;
    }
}
