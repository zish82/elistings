namespace Shared;

public class SupplierFeedDto
{
    public int Id { get; set; }
    public string Supplier { get; set; } = string.Empty;
    public string FeedType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool HasCookieHeader { get; set; }
    public bool IsRecommended { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    /// <summary>False when the feed's supplier is disabled on /supplier-feeds.</summary>
    public bool SupplierEnabled { get; set; } = true;
}

public class CreateSupplierFeedRequest
{
    public string Supplier { get; set; } = string.Empty;
    public string FeedType { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? CookieHeader { get; set; }
    public bool IsRecommended { get; set; }
}
