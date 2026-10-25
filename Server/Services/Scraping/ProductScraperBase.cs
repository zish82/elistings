using HtmlAgilityPack;
using System.Text.RegularExpressions;

namespace Server.Services;

public abstract class ProductScraperBase : IProductScraper
{
    public virtual bool IsFallback => false;
    public abstract bool CanHandle(string url);
    public abstract Task<Shared.ExtractedDetailsDto> ExtractDetailsAsync(string url, HtmlDocument doc, string html);

    public virtual Task<List<string>> ExtractProductLinksAsync(string categoryUrl, HtmlDocument doc, string html)
    {
        return Task.FromResult(new List<string>());
    }

    protected static bool IsHostMatch(string url, string domain)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(domain)) return false;

        try
        {
            var host = new Uri(url).Host.ToLowerInvariant();
            var d = domain.ToLowerInvariant();
            return host == d || host.EndsWith("." + d, StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    protected string ResolveUrl(string baseUrl, string? relativeUrl)
    {
        if (string.IsNullOrEmpty(relativeUrl)) return "";
        if (relativeUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return relativeUrl;

        try
        {
            var baseUri = new Uri(baseUrl);
            if (relativeUrl.StartsWith("//")) return baseUri.Scheme + ":" + relativeUrl;
            if (relativeUrl.StartsWith("/")) return new Uri(baseUri, relativeUrl).AbsoluteUri;

            if (!relativeUrl.StartsWith("./") && !relativeUrl.StartsWith("../"))
            {
                return baseUri.GetLeftPart(UriPartial.Authority).TrimEnd('/') + "/" + relativeUrl.TrimStart('/');
            }

            var resolvedUri = new Uri(baseUri, relativeUrl);
            var final = resolvedUri.AbsoluteUri;
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

    protected decimal? ParsePrice(string? priceStr)
    {
        if (string.IsNullOrEmpty(priceStr)) return null;
        var match = Regex.Match(priceStr, @"[0-9]+(\.[0-9]{1,2})?");
        if (match.Success && decimal.TryParse(match.Value, out var price)) return price;
        return null;
    }

    protected string NormalizeHtmlFragment(string html, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(html)) return string.Empty;

        var fragDoc = new HtmlDocument();
        fragDoc.LoadHtml("<div id=\"frag\">" + html + "</div>");

        var scripts = fragDoc.DocumentNode.SelectNodes("//script|//style");
        if (scripts != null)
        {
            foreach (var s in scripts) s.Remove();
        }

        var imgNodes = fragDoc.DocumentNode.SelectNodes("//img");
        if (imgNodes != null)
        {
            foreach (var img in imgNodes)
            {
                var src = img.GetAttributeValue("data-src", null)
                          ?? img.GetAttributeValue("data-large", null)
                          ?? img.GetAttributeValue("src", null)
                          ?? img.GetAttributeValue("srcset", null);
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

        var allNodes = fragDoc.DocumentNode.SelectNodes("//*");
        if (allNodes != null)
        {
            foreach (var n in allNodes)
            {
                var attrs = n.Attributes.ToList();
                foreach (var at in attrs)
                {
                    if (at.Name.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    {
                        n.Attributes.Remove(at.Name);
                    }
                    if ((at.Name == "href" || at.Name == "src")
                        && at.Value != null
                        && at.Value.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                    {
                        n.Attributes.Remove(at.Name);
                    }
                }
            }
        }

        var container = fragDoc.GetElementbyId("frag");
        return container?.InnerHtml.Trim() ?? string.Empty;
    }

    protected static string DecodeJsonEscapedUrl(string raw)
    {
        return raw
            .Replace("\\/", "/")
            .Replace("\\u0026", "&")
            .Replace("\\u003d", "=");
    }
}
