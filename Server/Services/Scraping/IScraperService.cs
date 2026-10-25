using Shared;

namespace Server.Services;

public interface IScraperService
{
    Task<ExtractedDetailsDto> ExtractDetailsAsync(string url);
    Task<List<string>> ExtractProductLinksAsync(string categoryUrl);
}
