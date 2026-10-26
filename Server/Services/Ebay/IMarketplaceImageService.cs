namespace Server.Services;

public interface IMarketplaceImageService
{
    /// <summary>Uploads raw image bytes to eBay Picture Services; returns the eBay picture URL.</summary>
    Task<string> UploadImageAsync(byte[] imageData, string fileName, int? accountId = null);

    /// <summary>
    /// Gets a picture onto eBay from a web address or data: URL; returns the eBay picture URL.
    /// <paramref name="step"/> (optional) is told what is happening, e.g. "Downloading from the supplier".
    /// </summary>
    Task<string> UploadImageFromUrlAsync(string imageUrl, int? accountId = null, Action<string>? step = null);
}
