using HtmlAgilityPack;
using Shared;
using System.Text.RegularExpressions;

namespace Server.Services;

public class GenericProductScraper : ProductScraperBase
{
    public override bool IsFallback => true;
    public override bool CanHandle(string url) => true;

    public override Task<ExtractedDetailsDto> ExtractDetailsAsync(string url, HtmlDocument doc, string html)
    {
        var details = new ExtractedDetailsDto();

        bool isEbay = IsHostMatch(url, "ebay.co.uk") || IsHostMatch(url, "ebay.com");
        bool isAmazon = IsHostMatch(url, "amazon.co.uk") || IsHostMatch(url, "amazon.com");

        details.Title = ((isEbay ? doc.DocumentNode.SelectSingleNode("//h1[contains(@class, 'x-item-title__mainTitle')]")?.InnerText : null)
                        ?? (isAmazon ? doc.DocumentNode.SelectSingleNode("//span[@id='productTitle']")?.InnerText : null)
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']")?.GetAttributeValue("content", "")
                        ?? doc.DocumentNode.SelectSingleNode("//title")?.InnerText
                        ?? string.Empty).Trim();

        var brand = (isEbay ? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'ux-layout-section--brand')]//span")?.InnerText : null)
                ?? (isAmazon ? doc.DocumentNode.SelectSingleNode("//a[@id='bylineInfo']")?.InnerText : null)
                ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:brand']")?.GetAttributeValue("content", "")
                ?? string.Empty;

        details.Brand = brand.Trim();
        if (details.Brand.Contains("Brand:")) details.Brand = details.Brand.Replace("Brand:", "").Trim();
        if (details.Brand.Contains("Visit the")) details.Brand = details.Brand.Split(' ').Last();

        details.Description = (isAmazon ? doc.DocumentNode.SelectSingleNode("//div[@id='feature-bullets']")?.InnerText : null)
                          ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']")?.GetAttributeValue("content", "")
                          ?? doc.DocumentNode.SelectSingleNode("//meta[@name='description']")?.GetAttributeValue("content", "")
                          ?? string.Empty;

        string? productCode = doc.DocumentNode.SelectSingleNode("//meta[@name='product-code']")?.GetAttributeValue("content", null)
                            ?? doc.DocumentNode.SelectSingleNode("//span[@itemprop='sku']")?.InnerText
                            ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class,'sku')]")?.InnerText
                            ?? doc.DocumentNode.SelectSingleNode("//p[contains(translate(., 'ABCDEFGHIJKLMNOPQRSTUVWXYZ','abcdefghijklmnopqrstuvwxyz'), 'product code')]")?.InnerText;

        details.ProductCode = string.IsNullOrWhiteSpace(productCode) ? null : productCode.Trim();

        details.Category = (isEbay ? doc.DocumentNode.SelectSingleNode("//nav[contains(@class, 'breadcrumbs')]")?.InnerText : null)
                           ?? (isAmazon ? doc.DocumentNode.SelectSingleNode("//div[@id='wayfinding-breadcrumbs_container']")?.InnerText : null)
                           ?? doc.DocumentNode.SelectSingleNode("//meta[@property='product:category']")?.GetAttributeValue("content", "")
                           ?? doc.DocumentNode.SelectSingleNode("//meta[@name='keywords']")?.GetAttributeValue("content", "")?.Split(',').FirstOrDefault()?.Trim()
                           ?? doc.DocumentNode.SelectSingleNode("//nav[contains(@class, 'breadcrumb')]")?.InnerText.Replace("\n", " ").Trim()
                           ?? string.Empty;

        var priceStr = (isEbay ? doc.DocumentNode.SelectSingleNode("//div[contains(@class, 'x-price-primary')]")?.InnerText : null)
                        ?? (isAmazon ? doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'a-price')]//span[contains(@class, 'a-offscreen')]")?.InnerText : null)
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@property='product:price:amount']")?.GetAttributeValue("content", "")
                        ?? doc.DocumentNode.SelectSingleNode("//meta[@property='og:price:amount']")?.GetAttributeValue("content", "")
                        ?? doc.DocumentNode.SelectSingleNode("//span[contains(@class, 'price')]")?.InnerText;
        details.Price = ParsePrice(priceStr);

        var imageUrls = new List<string>();
        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']")?.GetAttributeValue("content", "");
        if (!string.IsNullOrEmpty(ogImage)) imageUrls.Add(ResolveUrl(url, ogImage));

        if (isEbay)
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

        if (!string.IsNullOrEmpty(details.Description))
        {
            details.Description = Regex.Replace(details.Description, @"\s+", " ").Trim();
            if (details.Description.Length > 2000) details.Description = details.Description.Substring(0, 1997) + "...";
        }

        if (string.IsNullOrWhiteSpace(details.Brand) && !string.IsNullOrWhiteSpace(details.Title))
        {
            var m = Regex.Match(details.Title.Trim(), @"^([A-Z0-9\+\-&]{2,}(?:[ \+\-&][A-Z0-9\+\-&]+)*)");
            if (m.Success) details.Brand = m.Groups[1].Value.Trim();
        }

        return Task.FromResult(details);
    }
}
