namespace Server.Data;

/// <summary>
/// A picture already uploaded to eBay for an eBay account: the original picture (supplier URL or hand-uploaded data URL)
/// and the eBay-hosted URL it became. Re-publishing reuses the eBay URL instead of downloading and uploading again.
/// </summary>
public class EbayPictureUpload
{
    public int Id { get; set; }
    /// <summary>The eBay account (EbayTokens.Id) the picture was uploaded with; 0 = the user's default account.</summary>
    public int AccountId { get; set; }
    /// <summary>SHA-256 (hex) of the original picture URL; data URLs are too long to index directly.</summary>
    public string SourceHash { get; set; } = string.Empty;
    /// <summary>The original URL, for troubleshooting (null for hand-uploaded data URLs).</summary>
    public string? SourceUrl { get; set; }
    public string EbayUrl { get; set; } = string.Empty;
    public DateTime UploadedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAtUtc { get; set; } = DateTime.UtcNow;
}
