using System.Collections.Concurrent;
using Shared;

namespace Server.Services;

/// <summary>
/// In-memory progress of single-listing publishes (picture by picture), so the page can show what's happening while
/// its publish request is still running. Entries are dropped 15 minutes after their last update.
/// </summary>
public class PublishProgressStore
{
    private static readonly TimeSpan KeepFor = TimeSpan.FromMinutes(15);
    private readonly ConcurrentDictionary<int, PublishProgressDto> _items = new();

    public void Start(int listingId, string stage)
    {
        Cleanup();
        _items[listingId] = new PublishProgressDto { ListingId = listingId, Stage = stage, UpdatedAtUtc = DateTime.UtcNow };
    }

    public void SetImages(int listingId, IEnumerable<string> urls)
        => Update(listingId, p => p.Images = urls.Select(u => new PublishImageStatusDto { Url = Label(u) }).ToList());

    public void SetStage(int listingId, string stage) => Update(listingId, p => p.Stage = stage);

    public void SetImage(int listingId, int index, string state, string? message = null)
        => Update(listingId, p =>
        {
            if (index < 0 || index >= p.Images.Count) return;
            p.Images[index].State = state;
            p.Images[index].Message = message;
        });

    /// <summary>A copy (safe to serialise while the publish keeps updating), or null.</summary>
    public PublishProgressDto? Get(int listingId)
    {
        if (!_items.TryGetValue(listingId, out var p)) return null;
        lock (p)
        {
            return new PublishProgressDto
            {
                ListingId = p.ListingId,
                Stage = p.Stage,
                UpdatedAtUtc = p.UpdatedAtUtc,
                Images = p.Images.Select(i => new PublishImageStatusDto { Url = i.Url, State = i.State, Message = i.Message }).ToList()
            };
        }
    }

    private void Update(int listingId, Action<PublishProgressDto> change)
    {
        if (!_items.TryGetValue(listingId, out var p)) return;
        lock (p)
        {
            change(p);
            p.UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>Uploaded files (data: URLs) are huge; show a short label instead.</summary>
    private static string Label(string url) => url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? "Uploaded image" : url;

    private void Cleanup()
    {
        var cutoff = DateTime.UtcNow - KeepFor;
        foreach (var (id, p) in _items)
        {
            if (p.UpdatedAtUtc < cutoff) _items.TryRemove(id, out _);
        }
    }
}
