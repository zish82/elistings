using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Server.Configuration;

namespace Server.Services;

/// <summary>Uploads listing pictures to eBay Picture Services (Trading API UploadSiteHostedPictures).</summary>
public class EbayImageService : IMarketplaceImageService
{
    private const string ApiVersion = "1191";
    private const string BrowserUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";
    private static readonly XNamespace Ns = "urn:ebay:apis:eBLBaseComponents";

    private readonly HttpClient _httpClient;
    private readonly EbaySettings _settings;
    private readonly IEbayService _ebayService;

    public EbayImageService(HttpClient httpClient, IOptions<EbaySettings> settings, IEbayService ebayService)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _ebayService = ebayService;
    }

    private string ApiUrl => _settings.IsSandbox ? "https://api.sandbox.ebay.com/ws/api.dll" : "https://api.ebay.com/ws/api.dll";

    public async Task<string> UploadImageFromUrlAsync(string imageUrl, int? accountId = null)
    {
        if (imageUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("[ImageService] Processing base64 data URL...");
            var parts = imageUrl.Split(new[] { "base64," }, StringSplitOptions.None);
            if (parts.Length != 2) throw new Exception("Invalid base64 image data.");

            var imageData = Convert.FromBase64String(parts[1]);
            return await UploadImageAsync(imageData, "uploaded_image.jpg", accountId);
        }

        // 1) Download the picture ourselves and send the bytes.
        //    Supplier sites can refuse this server (e.g. Safelincs returns a block page instead of the picture),
        // 2) …so if we don't get a real image, let eBay fetch the URL itself (ExternalPictureURL).
        string? downloadProblem;
        try
        {
            var (data, problem) = await DownloadImageAsync(imageUrl);
            if (data != null)
            {
                var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);
                if (string.IsNullOrEmpty(fileName)) fileName = "image.jpg";
                return await UploadImageAsync(data, fileName, accountId);
            }
            downloadProblem = problem;
        }
        catch (HttpRequestException ex)
        {
            downloadProblem = $"download failed ({ex.Message})";
        }
        catch (TaskCanceledException)
        {
            downloadProblem = "download timed out";
        }

        Console.WriteLine($"[ImageService] {downloadProblem} – asking eBay to fetch {imageUrl} itself");
        try
        {
            return await UploadExternalUrlAsync(imageUrl, accountId);
        }
        catch (Exception ex)
        {
            throw new Exception($"{downloadProblem}; eBay couldn't fetch it either: {ex.Message}", ex);
        }
    }

    /// <summary>The image bytes, or null plus the reason when the site didn't return a picture.</summary>
    private async Task<(byte[]? Data, string? Problem)> DownloadImageAsync(string imageUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
        // Prefer formats eBay accepts; some sites serve WebP/AVIF from a ".jpg" address otherwise.
        request.Headers.TryAddWithoutValidation("Accept", "image/jpeg,image/png,image/gif,image/webp;q=0.8,image/*;q=0.5");

        Console.WriteLine($"[ImageService] Downloading image from: {imageUrl}");
        using var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            return (null, $"the supplier site answered {(int)response.StatusCode} {response.ReasonPhrase} instead of the picture");
        }

        var data = await response.Content.ReadAsByteArrayAsync();
        if (!LooksLikeImage(data))
        {
            var type = response.Content.Headers.ContentType?.MediaType ?? "unknown type";
            return (null, $"the supplier site returned {type} ({data.Length} bytes) instead of a picture – it may be blocking this server");
        }
        return (data, null);
    }

    public async Task<string> UploadImageAsync(byte[] imageData, string fileName, int? accountId = null)
    {
        var token = await _ebayService.GetOAuthTokenAsync(accountId);

        Console.WriteLine($"[ImageService] Uploading image '{fileName}' ({imageData.Length} bytes) to eBay via Raw Byte Multipart...");

        // Manual Multipart Construction - The most robust way to handle legacy eBay EPS
        var boundary = "eBayBinaryBoundary" + DateTime.Now.Ticks.ToString("x");
        var utf8 = new UTF8Encoding(false); // No BOM

        // 1. Build the XML Payload
        var safePictureName = fileName.Length > 40 ? fileName.Substring(0, 40) : fileName;
        var xmlRequest = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<UploadSiteHostedPicturesRequest xmlns=""urn:ebay:apis:eBLBaseComponents"">
  <Version>{ApiVersion}</Version>
  <PictureName>{System.Security.SecurityElement.Escape(safePictureName)}</PictureName>
  <ExtensionInDays>30</ExtensionInDays>
</UploadSiteHostedPicturesRequest>";

        // 2. Build the Full Request Body Manually
        using var bodyStream = new MemoryStream();
        using var writer = new StreamWriter(bodyStream, utf8);

        // Part 1: XMLPayload
        writer.Write($"--{boundary}\r\n");
        writer.Write("Content-Disposition: form-data; name=\"XMLPayload\"\r\n");
        writer.Write("Content-Type: text/xml; charset=utf-8\r\n\r\n");
        writer.Write(xmlRequest);
        writer.Write("\r\n");

        // Part 2: image
        writer.Write($"--{boundary}\r\n");
        writer.Write("Content-Disposition: form-data; name=\"image\"; filename=\"image.jpg\"\r\n");
        writer.Write("Content-Type: application/octet-stream\r\n\r\n");
        writer.Flush(); // Flush text to stream before writing binary data

        bodyStream.Write(imageData, 0, imageData.Length);

        // Closing boundary
        writer.Write($"\r\n--{boundary}--\r\n");
        writer.Flush();

        using var request = NewTradingRequest(token);
        request.Content = new ByteArrayContent(bodyStream.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));

        return await SendAsync(request);
    }

    /// <summary>eBay downloads the picture from <paramref name="imageUrl"/> itself (no bytes sent from here).</summary>
    private async Task<string> UploadExternalUrlAsync(string imageUrl, int? accountId)
    {
        var token = await _ebayService.GetOAuthTokenAsync(accountId);
        var xmlRequest = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<UploadSiteHostedPicturesRequest xmlns=""urn:ebay:apis:eBLBaseComponents"">
  <Version>{ApiVersion}</Version>
  <ExternalPictureURL>{System.Security.SecurityElement.Escape(imageUrl)}</ExternalPictureURL>
  <ExtensionInDays>30</ExtensionInDays>
</UploadSiteHostedPicturesRequest>";

        using var request = NewTradingRequest(token);
        request.Content = new StringContent(xmlRequest, new UTF8Encoding(false), "text/xml");
        return await SendAsync(request);
    }

    private HttpRequestMessage NewTradingRequest(string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Add("X-EBAY-API-CALL-NAME", "UploadSiteHostedPictures");
        request.Headers.Add("X-EBAY-API-SITEID", "3");
        request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", ApiVersion);
        request.Headers.Add("X-EBAY-API-IAF-TOKEN", token);
        return request;
    }

    private async Task<string> SendAsync(HttpRequestMessage request)
    {
        using var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        try
        {
            var url = ParseUploadResponse((int)response.StatusCode, content);
            Console.WriteLine($"[ImageService] Upload Success: {url}");
            return url;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ImageService] {ex.Message}");
            Console.WriteLine($"Raw Response: {content}");
            throw;
        }
    }

    /// <summary>eBay's picture URL from an UploadSiteHostedPictures response, or an exception with a readable reason.</summary>
    internal static string ParseUploadResponse(int statusCode, string content)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(content);
        }
        catch (XmlException)
        {
            var snippet = content.Length > 200 ? content[..200] + "…" : content;
            throw new Exception($"eBay Image Upload Failed: eBay answered HTTP {statusCode} with an unexpected response: {snippet.Trim()}");
        }

        var ack = doc.Root?.Element(Ns + "Ack")?.Value;
        if (ack == "Success" || ack == "Warning")
        {
            var fullUrl = doc.Root?.Element(Ns + "SiteHostedPictureDetails")?.Element(Ns + "FullURL")?.Value;
            if (!string.IsNullOrEmpty(fullUrl)) return fullUrl;
        }

        var errors = doc.Root?.Elements(Ns + "Errors")
            .Select(e => e.Element(Ns + "LongMessage")?.Value)
            .Where(m => !string.IsNullOrEmpty(m))
            .ToList();
        var errorMsg = errors is { Count: > 0 } ? string.Join(", ", errors) : "Unknown error";
        throw new Exception($"eBay Image Upload Failed: {errorMsg}");
    }

    /// <summary>True for JPEG, PNG, GIF, WebP, BMP, TIFF and AVIF/HEIC bytes (checks the file signature).</summary>
    internal static bool LooksLikeImage(byte[] data)
    {
        if (data.Length < 12) return false;
        bool StartsWith(params byte[] sig) => data.AsSpan(0, sig.Length).SequenceEqual(sig);

        return StartsWith(0xFF, 0xD8, 0xFF)                                        // JPEG
            || StartsWith(0x89, 0x50, 0x4E, 0x47)                                  // PNG
            || StartsWith(0x47, 0x49, 0x46, 0x38)                                  // GIF
            || (StartsWith(0x52, 0x49, 0x46, 0x46) && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) // WebP
            || StartsWith(0x42, 0x4D)                                              // BMP
            || StartsWith(0x49, 0x49, 0x2A, 0x00) || StartsWith(0x4D, 0x4D, 0x00, 0x2A) // TIFF
            || (data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70); // AVIF/HEIC ("ftyp")
    }
}
