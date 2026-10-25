using HtmlAgilityPack;
using System.Net;
using System.Text.RegularExpressions;
using Server.Data;

namespace Server.Services;

/// <summary>Details a supplier's product page adds on top of its feed.</summary>
public class SupplierProductDetails
{
    public string? ProductUrl { get; set; }
    public string? DescriptionHtml { get; set; }
    public Dictionary<string, string> Specifics { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ImageUrls { get; set; } = new();
    public string? Barcode { get; set; }
}

/// <summary>
/// Fetches the details missing from a supplier's feed (description, specifications, ...).
/// Implement one per supplier and register it in Program.cs.
/// </summary>
public interface ISupplierProductEnricher
{
    /// <summary>Matches <see cref="SupplierProduct.Supplier"/>.</summary>
    string SupplierCode { get; }
    Task<SupplierProductDetails?> FetchAsync(SupplierProduct product, CancellationToken cancellationToken = default);
}

/// <summary>DropShipMall: maps its product page (see <see cref="DropShipMallWebsiteClient"/>) to product details.</summary>
public class DropShipMallProductEnricher : ISupplierProductEnricher
{
    private readonly DropShipMallWebsiteClient _website;

    public DropShipMallProductEnricher(DropShipMallWebsiteClient website)
    {
        _website = website;
    }

    public string SupplierCode => DropShipMallWebsiteClient.SupplierCode;

    public async Task<SupplierProductDetails?> FetchAsync(SupplierProduct product, CancellationToken cancellationToken = default)
    {
        var page = await _website.FindProductPageAsync(product.SupplierSku, product.ProductUrl, cancellationToken);
        if (page == null) return null;

        return new SupplierProductDetails
        {
            ProductUrl = page.Url,
            DescriptionHtml = HtmlSanitizer.Clean(page.DescriptionHtml),
            Specifics = new Dictionary<string, string>(page.Specifics, StringComparer.OrdinalIgnoreCase),
            ImageUrls = page.ImageUrls.ToList(),
            Barcode = page.Barcode
        };
    }
}

/// <summary>Very small allow-list HTML cleaner for supplier descriptions (eBay rejects scripts/active content).</summary>
public static class HtmlSanitizer
{
    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "br", "ul", "ol", "li", "strong", "b", "em", "i", "u", "h2", "h3", "h4", "table", "tbody", "thead", "tr", "td", "th", "span", "div"
    };

    public static string? Clean(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;

        var doc = new HtmlDocument();
        doc.LoadHtml("<div>" + html + "</div>");
        var root = doc.DocumentNode.FirstChild;

        foreach (var node in root.Descendants().ToList())
        {
            if (node.NodeType != HtmlNodeType.Element) continue;
            if (!AllowedTags.Contains(node.Name))
            {
                if (node.Name is "script" or "style" or "iframe" or "form" or "object") node.Remove();
                else node.ParentNode?.RemoveChild(node, keepGrandChildren: true);
                continue;
            }
            node.Attributes.RemoveAll();
        }

        // Unwrap <p> elements that contain other <p>s (DropShipMall has an empty <p itemprop> wrapper).
        foreach (var p in root.Descendants("p").Where(p => p.Descendants("p").Any()).ToList())
        {
            p.ParentNode?.RemoveChild(p, keepGrandChildren: true);
        }

        var cleaned = Regex.Replace(root.InnerHtml, @"<p>\s*</p>", string.Empty).Trim();
        var text = WebUtility.HtmlDecode(Regex.Replace(cleaned, "<[^>]+>", " ")).Trim();
        return text.Length == 0 ? null : cleaned;
    }
}
