using Shared;

namespace Server.Services;

public interface IMarketplaceImageService
{
    /// <summary>
    /// Downloads an image from a URL and uploads it to the marketplace.
    /// </summary>
    Task<string> UploadImageFromUrlAsync(string imageUrl);

    /// <summary>
    /// Uploads raw image data to the marketplace.
    /// </summary>
    Task<string> UploadImageAsync(byte[] imageData, string fileName);
}
