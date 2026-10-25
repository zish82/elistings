namespace Shared;

public class ExtractedDetailsDto
{
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Brand { get; set; } = string.Empty;
    public decimal? Price { get; set; }
    public decimal? PriceIncVat { get; set; }
    public decimal? PriceExVat { get; set; }
    public List<string> ImageUrls { get; set; } = new();
    public string? ProductCode { get; set; }
}
