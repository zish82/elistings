using HtmlAgilityPack;
using Shared;

namespace Server.Services;

/// <summary>Creating a listing from a Godropship product URL: details from the page's JSON-LD and buy box.</summary>
public class GodropshipProductScraper : ProductScraperBase
{
    public override bool CanHandle(string url) => IsHostMatch(url, "godropship.co.uk");

    public override Task<ExtractedDetailsDto> ExtractDetailsAsync(string url, HtmlDocument doc, string html)
    {
        var page = GodropshipPageParser.Parse(html);
        return Task.FromResult(new ExtractedDetailsDto
        {
            Title = page.Title,
            Description = string.IsNullOrWhiteSpace(page.DescriptionHtml) ? string.Empty : NormalizeHtmlFragment(page.DescriptionHtml, url),
            Category = page.Category ?? string.Empty,
            Brand = string.Empty, // the JSON-LD brand is always "Go Dropship", not the product's brand
            // Godropship shows one price (UK delivery included); its VAT basis is the supplier setting on /suppliers.
            Price = page.Price,
            PriceIncVat = page.Price,
            ImageUrls = page.ImageUrls,
            ProductCode = page.ItemCode
        });
    }
}
