using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Server.Configuration;

namespace Server.Services;

/// <summary>
/// Uploads listing pictures to eBay Picture Services with the Media API
/// (createImageFromFile / createImageFromUrl). The old Trading API call UploadSiteHostedPictures was
/// switched off by eBay on 30 Sep 2026 (it now answers 404 "Resource not found").
/// </summary>
public class EbayImageService : IMarketplaceImageService
{
    private const string BrowserUserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    private readonly HttpClient _httpClient;
    private readonly EbaySettings _settings;
    private readonly IEbayService _ebayService;

    public EbayImageService(HttpClient httpClient, IOptions<EbaySettings> settings, IEbayService ebayService)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _ebayService = ebayService;
    }

    private string MediaBaseUrl => _settings.IsSandbox
        ? "https://apim.sandbox.ebay.com/commerce/media/v1_beta"
        : "https://apim.ebay.com/commerce/media/v1_beta";

    public async Task<string> UploadImageFromUrlAsync(string imageUrl, int? accountId = null, Action<string>? step = null)
    {
        if (imageUrl.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            var parts = imageUrl.Split(new[] { "base64," }, StringSplitOptions.None);
            if (parts.Length != 2) throw new Exception("The uploaded image isn't valid (bad data URL).");

            step?.Invoke("Uploading to eBay");
            return await UploadImageAsync(Convert.FromBase64String(parts[1]), "uploaded_image.jpg", accountId);
        }

        // 1) Download the picture ourselves and send the bytes.
        //    Supplier sites can refuse this server (e.g. Safelincs returns a block page instead of the picture),
        // 2) …so if we don't get a real image, let eBay fetch the URL itself (createImageFromUrl).
        step?.Invoke("Downloading from the supplier");
        string? downloadProblem;
        try
        {
            var (data, problem) = await DownloadImageAsync(imageUrl);
            if (data != null)
            {
                step?.Invoke("Uploading to eBay");
                var fileName = Path.GetFileName(new Uri(imageUrl).LocalPath);
                if (string.IsNullOrEmpty(fileName)) fileName = "image.jpg";
                return await UploadImageAsync(data, fileName, accountId);
            }
            downloadProblem = problem;
        }
        catch (HttpRequestException ex)
        {
            downloadProblem = $"Download failed ({ex.Message})";
        }
        catch (TaskCanceledException)
        {
            downloadProblem = "Download timed out";
        }

        Console.WriteLine($"[ImageService] {downloadProblem} – asking eBay to fetch {imageUrl} itself");
        step?.Invoke("Couldn't download it here – asking eBay to fetch it");
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
            return (null, $"The supplier site answered {(int)response.StatusCode} {response.ReasonPhrase} instead of the picture");
        }

        var data = await response.Content.ReadAsByteArrayAsync();
        if (!LooksLikeImage(data))
        {
            var type = response.Content.Headers.ContentType?.MediaType ?? "unknown type";
            return (null, $"The supplier site returned {type} ({data.Length} bytes) instead of a picture – it may be blocking this server");
        }
        return (data, null);
    }

    public async Task<string> UploadImageAsync(byte[] imageData, string fileName, int? accountId = null)
    {
        var token = await _ebayService.GetOAuthTokenAsync(accountId);
        Console.WriteLine($"[ImageService] Uploading image '{fileName}' ({imageData.Length} bytes) to eBay (Media API)...");

        // Plain, quoted multipart headers (no filename*= extension, no quoted boundary) – what eBay's docs/examples send.
        var image = new ByteArrayContent(imageData);
        image.Headers.ContentType = new MediaTypeHeaderValue(MediaTypeFor(imageData));
        image.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "\"image\"",
            FileName = $"\"{SafeFileName(fileName)}\""
        };
        var boundary = "eListings" + Guid.NewGuid().ToString("N");
        using var form = new MultipartFormDataContent(boundary);
        form.Headers.ContentType = MediaTypeHeaderValue.Parse($"multipart/form-data; boundary={boundary}");
        form.Add(image);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MediaBaseUrl}/image/create_image_from_file") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, token);
    }

    /// <summary>eBay downloads the picture from <paramref name="imageUrl"/> itself (no bytes sent from here).</summary>
    private async Task<string> UploadExternalUrlAsync(string imageUrl, int? accountId)
    {
        var token = await _ebayService.GetOAuthTokenAsync(accountId);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{MediaBaseUrl}/image/create_image_from_url")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { imageUrl }), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await SendAsync(request, token);
    }

    private async Task<string> SendAsync(HttpRequestMessage request, string token)
    {
        using var response = await _httpClient.SendAsync(request);
        var content = await response.Content.ReadAsStringAsync();
        try
        {
            var url = ParseImageResponse((int)response.StatusCode, content);
            if (url == null && response.Headers.Location != null)
            {
                url = await GetImageUrlAsync(response.Headers.Location, token); // 201 with only a Location header
            }
            if (string.IsNullOrEmpty(url)) throw new Exception("eBay accepted the picture but didn't return its address.");

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

    /// <summary>getImage: the picture's EPS URL from the Location returned by the create call.</summary>
    private async Task<string?> GetImageUrlAsync(Uri location, string token)
    {
        var uri = location.IsAbsoluteUri ? location : new Uri(new Uri(MediaBaseUrl + "/"), location);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _httpClient.SendAsync(request);
        return ParseImageResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The EPS picture URL from a Media API image response; null when a successful response has no body (use Location);
    /// an exception with eBay's message for errors.
    /// </summary>
    internal static string? ParseImageResponse(int statusCode, string content)
    {
        JsonElement root = default;
        var isJson = false;
        if (!string.IsNullOrWhiteSpace(content))
        {
            try
            {
                using var doc = JsonDocument.Parse(content);
                root = doc.RootElement.Clone();
                isJson = true;
            }
            catch (JsonException) { }
        }

        if (statusCode is >= 200 and < 300)
        {
            if (isJson && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("imageUrl", out var u) && u.ValueKind == JsonValueKind.String)
                return u.GetString();
            return null;
        }

        if (isJson && root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("longMessage", out var lm) && lm.ValueKind == JsonValueKind.String ? lm.GetString()
                           : e.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .ToList();
            if (messages.Count > 0)
                throw new Exception($"eBay rejected the picture (HTTP {statusCode}): {string.Join(" ", messages)}");
        }

        var snippet = content.Length > 200 ? content[..200] + "…" : content;
        throw new Exception($"eBay rejected the picture (HTTP {statusCode}): {(string.IsNullOrWhiteSpace(snippet) ? "no details" : snippet.Trim())}");
    }

    /// <summary>True for JPEG, PNG, GIF, WebP, BMP, TIFF and AVIF/HEIC bytes (checks the file signature).</summary>
    internal static bool LooksLikeImage(byte[] data) => DetectFormat(data) != null;

    private static string MediaTypeFor(byte[] data) => DetectFormat(data) ?? "application/octet-stream";

    private static string? DetectFormat(byte[] data)
    {
        if (data.Length < 12) return null;
        bool StartsWith(params byte[] sig) => data.AsSpan(0, sig.Length).SequenceEqual(sig);

        if (StartsWith(0xFF, 0xD8, 0xFF)) return "image/jpeg";
        if (StartsWith(0x89, 0x50, 0x4E, 0x47)) return "image/png";
        if (StartsWith(0x47, 0x49, 0x46, 0x38)) return "image/gif";
        if (StartsWith(0x52, 0x49, 0x46, 0x46) && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return "image/webp";
        if (StartsWith(0x42, 0x4D)) return "image/bmp";
        if (StartsWith(0x49, 0x49, 0x2A, 0x00) || StartsWith(0x4D, 0x4D, 0x00, 0x2A)) return "image/tiff";
        if (data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70) // "ftyp"
            return data[8] == 0x61 ? "image/avif" : "image/heic";
        return null;
    }

    /// <summary>Plain ASCII file name for the multipart header.</summary>
    private static string SafeFileName(string fileName)
    {
        var clean = new string(fileName.Where(c => c < 128 && (char.IsLetterOrDigit(c) || c is '.' or '-' or '_')).ToArray());
        if (clean.Length > 60) clean = clean[^60..];
        return string.IsNullOrEmpty(clean) || !clean.Contains('.') ? "image.jpg" : clean;
    }
}
