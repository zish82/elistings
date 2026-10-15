namespace Shared;

public class BulkJobDto
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;      // "extract" | "publish"
    public string Status { get; set; } = "Running";       // Running | Completed | Failed
    public string? Message { get; set; }
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public List<string> Errors { get; set; } = new();
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }

    public bool IsFinished => Status != "Running";
}

public class BulkJobStartedDto
{
    public string JobId { get; set; } = string.Empty;
}
