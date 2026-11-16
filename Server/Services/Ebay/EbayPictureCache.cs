using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Server.Data;

namespace Server.Services;

/// <summary>
/// Turns a listing's pictures into eBay-hosted URLs, reusing pictures already uploaded for the same eBay account
/// (<see cref="EbayPictureUpload"/>) so re-publishing doesn't download from the supplier and upload to eBay again.
/// Kept per account because eBay-hosted pictures belong to the seller account that uploaded them.
/// </summary>
public class EbayPictureCache
{
    private readonly AppDbContext _context;

    public EbayPictureCache(AppDbContext context) => _context = context;

    public sealed record Result(List<string> Urls, List<string> Errors, List<string> ReusedSources);

    public static bool IsEbayHosted(string url) => url.Contains("ebayimg.com", StringComparison.OrdinalIgnoreCase);

    public static string Hash(string sourceUrl)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceUrl))).ToLowerInvariant();

    /// <summary>
    /// The eBay URL for each picture, in order. Pictures already on eBay are kept; remembered uploads are reused
    /// (unless <paramref name="useCache"/> is false); the rest go through <paramref name="upload"/> (index, url → eBay URL;
    /// throws on failure) and are remembered. <paramref name="reused"/> is told which indexes needed no upload.
    /// </summary>
    public async Task<Result> ResolveAsync(int accountId, IReadOnlyList<string> sourceUrls, bool useCache,
        Func<int, string, Task<string>> upload, Action<int>? reused = null)
    {
        var urls = new List<string>();
        var errors = new List<string>();
        var reusedSources = new List<string>();

        var hashes = sourceUrls.Where(u => !IsEbayHosted(u)).Select(Hash).Distinct().ToList();
        var known = useCache && hashes.Count > 0
            ? await _context.EbayPictureUploads.Where(p => p.AccountId == accountId && hashes.Contains(p.SourceHash)).ToListAsync()
            : new List<EbayPictureUpload>();
        var byHash = known.ToDictionary(p => p.SourceHash);

        for (var i = 0; i < sourceUrls.Count; i++)
        {
            var url = sourceUrls[i];
            if (IsEbayHosted(url))
            {
                urls.Add(url);
                reused?.Invoke(i);
                continue;
            }

            var hash = Hash(url);
            if (byHash.TryGetValue(hash, out var hit))
            {
                hit.LastUsedAtUtc = DateTime.UtcNow;
                urls.Add(hit.EbayUrl);
                reusedSources.Add(url);
                reused?.Invoke(i);
                continue;
            }

            try
            {
                var ebayUrl = await upload(i, url);
                urls.Add(ebayUrl);
                hit = await _context.EbayPictureUploads.FirstOrDefaultAsync(p => p.AccountId == accountId && p.SourceHash == hash);
                if (hit == null)
                {
                    hit = new EbayPictureUpload { AccountId = accountId, SourceHash = hash };
                    _context.EbayPictureUploads.Add(hit);
                }
                hit.SourceUrl = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? null : url;
                hit.EbayUrl = ebayUrl;
                hit.UploadedAtUtc = hit.LastUsedAtUtc = DateTime.UtcNow;
                byHash[hash] = hit; // the same picture twice in one listing is uploaded once
            }
            catch (Exception ex)
            {
                var label = url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? "Uploaded image" : url;
                errors.Add($"Picture {i + 1} ({label}): {ex.Message}");
            }
        }

        await _context.SaveChangesAsync();
        return new Result(urls, errors, reusedSources);
    }

    /// <summary>Drops remembered uploads (e.g. eBay rejected them because they expired), so they are uploaded again.</summary>
    public async Task ForgetAsync(int accountId, IEnumerable<string> sourceUrls)
    {
        var hashes = sourceUrls.Select(Hash).Distinct().ToList();
        var rows = await _context.EbayPictureUploads.Where(p => p.AccountId == accountId && hashes.Contains(p.SourceHash)).ToListAsync();
        _context.EbayPictureUploads.RemoveRange(rows);
        await _context.SaveChangesAsync();
    }
}
