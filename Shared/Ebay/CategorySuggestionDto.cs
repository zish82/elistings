namespace Shared;

public class CategorySuggestionDto
{
    public string CategoryId { get; set; } = string.Empty;
    public string CategoryName { get; set; } = string.Empty;
    public double Confidence { get; set; }
    public List<string> CategoryPath { get; set; } = new();
}
