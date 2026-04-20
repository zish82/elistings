using HtmlAgilityPack;
using Shared;

namespace Server.Services;

public interface IProductScraper
{
    bool IsFallback { get; }
    bool CanHandle(string url);
    Task<ExtractedDetailsDto> ExtractDetailsAsync(string url, HtmlDocument doc, string html);
    Task<List<string>> ExtractProductLinksAsync(string categoryUrl, HtmlDocument doc, string html);
}
