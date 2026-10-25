using Microsoft.EntityFrameworkCore;
using Shared;

namespace Server.Data;

public class SupplierFeed
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string FeedType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CookieHeader { get; set; }
    public bool IsRecommended { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
