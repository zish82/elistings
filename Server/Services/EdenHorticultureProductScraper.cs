using HtmlAgilityPack;
using Shared;
using System.Net;
using System.Text.RegularExpressions;

namespace Server.Services;

public class EdenHorticultureProductScraper : ProductScraperBase
{
    public override bool CanHandle(string url) => IsHostMatch(url, "edenhorticulture.co.uk");

    public override Task<ExtractedDetailsDto> ExtractDetailsAsync(string url, HtmlDocument doc, string html)
    {
        var details = new ExtractedDetailsDto();

        details.Title = (doc.DocumentNode.SelectSingleNode("//h1")?.InnerText
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", "")
                        ?? doc.DocumentNode.SelectSingleNode("//title")?.InnerText
                        ?? string.Empty).Trim();

        string? brand = doc.DocumentNode.SelectSingleNode("//meta[@property='product:brand']")?.GetAttributeValue("content", null)
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@name='brand']")?.GetAttributeValue("content", null);

        if (string.IsNullOrWhiteSpace(brand))
        {
            var brandMatch = Regex.Match(html, "\\\"brand\\\"\\s*:\\s*\\{[^\\}]*\\\"name\\\"\\s*:\\s*\\\"(?<brand>[^\\\"]+)\\\"");
            if (brandMatch.Success)
            {
                brand = WebUtility.HtmlDecode(brandMatch.Groups["brand"].Value.Trim());
            }
        }

        details.Brand = (brand ?? string.Empty).Trim();

        details.Description = ExtractCombinedDescription(doc, url);
        if (string.IsNullOrWhiteSpace(details.Description))
        {
            details.Description = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", "")
                              ?? doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "")
                              ?? string.Empty;
        }

        string? productCode = null;
        var skuMatch = Regex.Match(html, "\\\"sku\\\"\\s*:\\s*\\\"(?<sku>[^\\\"]+)\\\"");
        if (skuMatch.Success)
        {
            productCode = skuMatch.Groups["sku"].Value.Trim();
        }

        productCode = productCode
                      ?? doc.DocumentNode.SelectSingleNode("//meta[@name='product-code']")?.GetAttributeValue("content", null)
                      ?? doc.DocumentNode.SelectSingleNode("//span[@itemprop='sku']")?.InnerText
                      ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'sku')]")?.InnerText;

        details.ProductCode = string.IsNullOrWhiteSpace(productCode) ? null : productCode.Trim();

        details.Category = doc.DocumentNode.SelectSingleNode("//meta[@property='product:category']")?.GetAttributeValue("content", "")
                           ?? doc.DocumentNode.SelectSingleNode("//meta[@name='keywords']")?.GetAttributeValue("content", "")?.Split(',').FirstOrDefault()?.Trim()
                           ?? doc.DocumentNode.SelectSingleNode("//nav[contains(@class, 'breadcrumb')]")?.InnerText.Replace("\n", " ").Trim()
                           ?? string.Empty;

        // Price intentionally omitted: domain can hide wholesale price when logged out.
        details.Price = null;
        details.PriceIncVat = null;
        details.PriceExVat = null;

        var imageUrls = new List<string>();
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", "");
        if (!string.IsNullOrEmpty(ogImage)) imageUrls.Add(ResolveUrl(url, ogImage));

        var edenImgNodes = doc.DocumentNode.SelectNodes("//img[contains(@src, '/cdn/shop/products/') or contains(@data-src, '/cdn/shop/products/')]");
        if (edenImgNodes != null)
        {
            foreach (var img in edenImgNodes)
            {
                var src = img.GetAttributeValue("data-src", null)
                          ?? img.GetAttributeValue("src", null)
                          ?? img.GetAttributeValue("srcset", null);
                if (string.IsNullOrWhiteSpace(src)) continue;
                imageUrls.Add(ResolveUrl(url, src));
            }
        }

        var jsonImageMatches = Regex.Matches(html, "\\\"image\\\"\\s*:\\s*\\\"(?<url>https?:\\\\/\\\\/[^\\\"]+)\\\"");
        foreach (Match match in jsonImageMatches)
        {
            var raw = match.Groups["url"].Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            imageUrls.Add(DecodeJsonEscapedUrl(raw));
        }

        details.ImageUrls = imageUrls.Distinct().ToList();

        return Task.FromResult(details);
    }

    private string ExtractCombinedDescription(HtmlDocument doc, string baseUrl)
    {
        HtmlNode? FindAccordionSection(string heading)
        {
            var lower = heading.ToLowerInvariant();
            return doc.DocumentNode.SelectSingleNode($"//details[contains(@class,'accordion')][.//summary//span[contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), '{lower}')]]//div[contains(@class,'accordion__content')]//div[contains(@class,'prose')]")
                   ?? doc.DocumentNode.SelectSingleNode($"//section[.//h2[contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), '{lower}')]]//div[contains(@class,'prose')]")
                   ?? doc.DocumentNode.SelectSingleNode($"//div[contains(@class,'product')][.//*[contains(translate(normalize-space(.), 'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'), '{lower}')]]//div[contains(@class,'prose')]");
        }

        var descriptionNode = FindAccordionSection("description");
        var specsNode = FindAccordionSection("specifications");

        var parts = new List<string>();
        if (descriptionNode != null)
        {
            var html = NormalizeHtmlFragment(descriptionNode.InnerHtml, baseUrl);
            if (!string.IsNullOrWhiteSpace(html))
            {
                parts.Add("<h2>Description</h2>" + html);
            }
        }

        if (specsNode != null)
        {
            var html = NormalizeHtmlFragment(specsNode.InnerHtml, baseUrl);
            if (!string.IsNullOrWhiteSpace(html))
            {
                parts.Add("<h2>Specifications</h2>" + html);
            }
        }

        return string.Join("\n", parts);
    }
}
