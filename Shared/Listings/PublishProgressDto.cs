namespace Shared;

/// <summary>Live progress of a single "Publish" (polled by the page while the publish request runs).</summary>
public class PublishProgressDto
{
    public int ListingId { get; set; }

    /// <summary>What's happening now, e.g. "Uploading pictures (2 of 5)", "Publishing the offer on eBay".</summary>
    public string Stage { get; set; } = string.Empty;

    public List<PublishImageStatusDto> Images { get; set; } = new();
    public DateTime UpdatedAtUtc { get; set; }
}

public class PublishImageStatusDto
{
    public string Url { get; set; } = string.Empty;

    /// <summary>See <see cref="PublishImageStates"/>.</summary>
    public string State { get; set; } = PublishImageStates.Waiting;

    /// <summary>Step in progress, or why it failed.</summary>
    public string? Message { get; set; }
}

public static class PublishImageStates
{
    public const string Waiting = "Waiting";
    public const string Working = "Working";
    public const string Uploaded = "Uploaded";
    public const string OnEbay = "Already on eBay";
    public const string Failed = "Failed";
}
