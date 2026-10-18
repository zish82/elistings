using System.Collections.Concurrent;

namespace Server.Services;

/// <summary>
/// App-wide politeness limit for supplier websites: any two requests to the same configured host
/// (from imports, stock checks, enrichment, scraping, ...) are at least <c>MinGap</c> apart.
/// Configure in appsettings: "RequestThrottle": { "edenhorticulture.co.uk": 2000 } (milliseconds).
/// </summary>
public sealed class HostRequestThrottle
{
    private sealed class HostState
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public DateTime LastRequestUtc = DateTime.MinValue;
    }

    private readonly Dictionary<string, TimeSpan> _gaps;
    private readonly ConcurrentDictionary<string, HostState> _states = new(StringComparer.OrdinalIgnoreCase);

    public HostRequestThrottle(IConfiguration configuration)
        : this(configuration.GetSection("RequestThrottle").GetChildren()
            .Where(c => int.TryParse(c.Value, out _))
            .ToDictionary(c => c.Key, c => TimeSpan.FromMilliseconds(int.Parse(c.Value!)), StringComparer.OrdinalIgnoreCase))
    {
    }

    public HostRequestThrottle(IDictionary<string, TimeSpan> gaps)
    {
        _gaps = new Dictionary<string, TimeSpan>(gaps, StringComparer.OrdinalIgnoreCase);
        if (_gaps.Count == 0)
        {
            // Default: Eden Horticulture (we scrape it while logged in, so be especially polite).
            _gaps["edenhorticulture.co.uk"] = TimeSpan.FromSeconds(2);
        }
    }

    /// <summary>The configured host this request belongs to (matches subdomains, e.g. www.), or null.</summary>
    public string? MatchHost(Uri? uri)
    {
        if (uri == null) return null;
        var host = uri.Host;
        return _gaps.Keys.FirstOrDefault(h => host.Equals(h, StringComparison.OrdinalIgnoreCase)
                                              || host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Waits until at least the configured gap has passed since the previous request to this host.</summary>
    public async Task WaitTurnAsync(string host, CancellationToken ct)
    {
        var gap = _gaps[host];
        var state = _states.GetOrAdd(host, _ => new HostState());
        await state.Gate.WaitAsync(ct);
        try
        {
            var wait = state.LastRequestUtc + gap - DateTime.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct);
            state.LastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            state.Gate.Release();
        }
    }
}

/// <summary>Added to every HttpClient from IHttpClientFactory; delays requests to throttled hosts.</summary>
public sealed class HostThrottlingHandler : DelegatingHandler
{
    private readonly HostRequestThrottle _throttle;

    public HostThrottlingHandler(HostRequestThrottle throttle)
    {
        _throttle = throttle;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var host = _throttle.MatchHost(request.RequestUri);
        if (host != null) await _throttle.WaitTurnAsync(host, cancellationToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
