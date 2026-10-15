using System.Net;
using System.Text.Json;

namespace Server.Services;

/// <summary>An eBay API call failed. <see cref="Exception.Message"/> is eBay's own (human-readable) error.</summary>
public class EbayApiException : Exception
{
    public HttpStatusCode? StatusCode { get; }
    public int? EbayErrorId { get; }

    public EbayApiException(string message, HttpStatusCode? statusCode = null, int? ebayErrorId = null, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        EbayErrorId = ebayErrorId;
    }

    /// <summary>Builds an exception from an eBay REST error body: {"errors":[{"errorId":..,"message":..,"longMessage":..}]}.</summary>
    public static async Task<EbayApiException> FromResponseAsync(HttpResponseMessage response, string operation)
    {
        var body = await response.Content.ReadAsStringAsync();
        int? errorId = null;
        string? detail = null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array && errors.GetArrayLength() > 0)
            {
                var first = errors[0];
                if (first.TryGetProperty("errorId", out var id) && id.TryGetInt32(out var parsed)) errorId = parsed;
                detail = (first.TryGetProperty("longMessage", out var lm) ? lm.GetString() : null)
                         ?? (first.TryGetProperty("message", out var m) ? m.GetString() : null);
            }
        }
        catch (JsonException)
        {
            // not JSON: fall back to the raw body below
        }

        detail ??= string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body;
        var idText = errorId.HasValue ? $" (eBay error {errorId})" : string.Empty;
        return new EbayApiException($"{operation} failed{idText}: {detail}", response.StatusCode, errorId);
    }
}
