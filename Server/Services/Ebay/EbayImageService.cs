using System.IO;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Server.Configuration;

namespace Server.Services;

public class EbayImageService : IMarketplaceImageService
{
    private readonly HttpClient _httpClient;
    private readonly EbaySettings _settings;
    private readonly IEbayService _ebayService;

    public EbayImageService(HttpClient httpClient, IOptions<EbaySettings> settings, IEbayService ebayService)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _ebayService = ebayService;
    }

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

        Console.WriteLine($"[ImageService] Downloading image from: {imageUrl}");
        var response = await _httpClient.GetAsync(imageUrl);
        response.EnsureSuccessStatusCode();
        var data = await response.Content.ReadAsByteArrayAsync();
        
        var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);
        if (string.IsNullOrEmpty(fileName)) fileName = "image.jpg";

        return await UploadImageAsync(data, fileName, accountId);
    }

    public async Task<string> UploadImageAsync(byte[] imageData, string fileName, int? accountId = null)
    {
        var token = await _ebayService.GetOAuthTokenAsync(accountId);
        var apiVersion = "1191";
        var baseUrl = _settings.IsSandbox ? "https://api.sandbox.ebay.com/ws/api.dll" : "https://api.ebay.com/ws/api.dll";

        Console.WriteLine($"[ImageService] Uploading image '{fileName}' ({imageData.Length} bytes) to eBay via Raw Byte Multipart...");

        // Manual Multipart Construction - The most robust way to handle legacy eBay EPS
        var boundary = "eBayBinaryBoundary" + DateTime.Now.Ticks.ToString("x");
        var utf8 = new UTF8Encoding(false); // No BOM

        // 1. Build the XML Payload
        var safePictureName = fileName.Length > 40 ? fileName.Substring(0, 40) : fileName;
        var xmlRequest = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<UploadSiteHostedPicturesRequest xmlns=""urn:ebay:apis:eBLBaseComponents"">
  <Version>{apiVersion}</Version>
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

        var bodyBytes = bodyStream.ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl);
        request.Headers.Add("X-EBAY-API-CALL-NAME", "UploadSiteHostedPictures");
        request.Headers.Add("X-EBAY-API-SITEID", "3");
        request.Headers.Add("X-EBAY-API-COMPATIBILITY-LEVEL", apiVersion);
        request.Headers.Add("X-EBAY-API-IAF-TOKEN", token);

        request.Content = new ByteArrayContent(bodyBytes);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");
        request.Content.Headers.ContentType.Parameters.Add(new NameValueHeaderValue("boundary", boundary));

        var response = await _httpClient.SendAsync(request);
        var responseContent = await response.Content.ReadAsStringAsync();

        try
        {
            var doc = XDocument.Parse(responseContent);
            XNamespace ns = "urn:ebay:apis:eBLBaseComponents";
            var ack = doc.Root?.Element(ns + "Ack")?.Value;
            
            if (ack == "Success" || ack == "Warning")
            {
                var fullUrl = doc.Root?.Element(ns + "SiteHostedPictureDetails")?.Element(ns + "FullURL")?.Value;
                if (!string.IsNullOrEmpty(fullUrl))
                {
                    Console.WriteLine($"[ImageService] Upload Success: {fullUrl}");
                    return fullUrl;
                }
            }

            var errors = doc.Root?.Elements(ns + "Errors")
                .Select(e => e.Element(ns + "LongMessage")?.Value)
                .Where(m => !string.IsNullOrEmpty(m))
                .ToList();
                
            var errorMsg = errors != null && errors.Any() ? string.Join(", ", errors) : "Unknown error";
            Console.WriteLine($"[ImageService] eBay returned failure: {errorMsg}");
            throw new Exception($"eBay Image Upload Failed: {errorMsg}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ImageService] Error parsing response: {ex.Message}");
            Console.WriteLine($"Raw Response: {responseContent}");
            throw;
        }
    }
}
