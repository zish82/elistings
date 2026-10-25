using System.Net;
using System.Text.RegularExpressions;

namespace Server.Services;

/// <summary>Turns scraped category text (breadcrumbs, meta tags) into one clean supplier category name.</summary>
public static class CategoryText
{
    private static readonly string[] Ignored = { "home", "products", "all products", "shop", "catalogue" };

    /// <summary>
    /// "Home &gt; Fire Safety &gt; Fire Extinguisher ID Signs" → "Fire Extinguisher ID Signs".
    /// Returns null for empty input. Keeps at most 100 characters.
    /// </summary>
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = WebUtility.HtmlDecode(raw);
        var parts = Regex.Split(text, @"\s*(?:>|›|»|\||\r|\n)\s*")
            .Select(p => Regex.Replace(p, @"\s+", " ").Trim())
            .Where(p => p.Length > 0 && !Ignored.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var last = parts.LastOrDefault();
        if (string.IsNullOrWhiteSpace(last)) return null;
        return last.Length <= 100 ? last : last[..100].Trim();
    }
}
