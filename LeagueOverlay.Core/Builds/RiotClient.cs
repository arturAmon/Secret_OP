using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace LeagueOverlay.Core.Builds;

/// <summary>A Riot server region: the platform host (league-v4) and the regional host it routes match-v5 through.</summary>
public sealed record RiotRegion(string Platform, string Regional, string Label)
{
    public static readonly IReadOnlyList<RiotRegion> All =
    [
        new("EUW1", "europe", "Europe West"),
        new("EUN1", "europe", "Europe Nordic & East"),
        new("TR1", "europe", "Turkey"),
        new("RU", "europe", "Russia"),
        new("ME1", "europe", "Middle East"),
        new("NA1", "americas", "North America"),
        new("BR1", "americas", "Brazil"),
        new("LA1", "americas", "Latin America North"),
        new("LA2", "americas", "Latin America South"),
        new("KR", "asia", "Korea"),
        new("JP1", "asia", "Japan"),
        new("OC1", "sea", "Oceania"),
        new("SG2", "sea", "Southeast Asia"),
        new("TW2", "sea", "Taiwan"),
        new("VN2", "sea", "Vietnam"),
    ];

    public static RiotRegion Default => All[0];
    public static RiotRegion Find(string? platform) =>
        All.FirstOrDefault(r => string.Equals(r.Platform, platform, StringComparison.OrdinalIgnoreCase)) ?? Default;

    public override string ToString() => $"{Label} ({Platform})";
}

/// <summary>The key was refused (401/403): missing, mistyped, or a development key older than 24 hours.</summary>
public sealed class RiotKeyException(HttpStatusCode status) : Exception($"Riot API key rejected ({(int)status})")
{
    public HttpStatusCode Status { get; } = status;
}

/// <summary>
/// Minimal Riot API client that stays inside the key's rate limits. The limits are read from the
/// X-App-Rate-Limit / X-Method-Rate-Limit headers, so personal or production keys automatically go faster
/// than a development key (20 per second, 100 per 2 minutes).
/// </summary>
public sealed class RiotClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly RateLimiter _app = new("20:1,100:120");
    private readonly Dictionary<string, RateLimiter> _methods = new();

    public RiotRegion Region { get; }

    public RiotClient(string apiKey, RiotRegion region, HttpMessageHandler? handler = null)
    {
        Region = region;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Add("X-Riot-Token", apiKey);
    }

    public void Dispose() => _http.Dispose();

    public Task<JsonDocument> PlatformStatusAsync(CancellationToken ct) =>
        GetAsync($"https://{Region.Platform}.api.riotgames.com/lol/status/v4/platform-data", "status", ct);

    /// <summary>Challenger / Grandmaster / Master solo queue ladder ("challengerleagues", ...).</summary>
    public Task<JsonDocument> LeagueAsync(string league, CancellationToken ct) =>
        GetAsync($"https://{Region.Platform}.api.riotgames.com/lol/league/v4/{league}/by-queue/RANKED_SOLO_5x5", league, ct);

    public Task<JsonDocument> MatchIdsAsync(string puuid, long startTimeUnix, int count, CancellationToken ct) =>
        GetAsync($"https://{Region.Regional}.api.riotgames.com/lol/match/v5/matches/by-puuid/{puuid}/ids?queue=420&type=ranked&startTime={startTimeUnix}&count={count}", "ids", ct);

    /// <summary>Recent matches of any queue (normals, flex, solo/duo...) since startTime, newest first.</summary>
    public Task<JsonDocument> RecentMatchIdsAsync(string puuid, long? startTimeUnix, int count, CancellationToken ct) =>
        GetAsync($"https://{Region.Regional}.api.riotgames.com/lol/match/v5/matches/by-puuid/{puuid}/ids?count={count}" +
                 (startTimeUnix is { } start ? $"&startTime={start}" : ""), "ids", ct);

    /// <summary>Account by Riot ID ("Name#Tag"), for its puuid. Account-v1 has no "sea" host; any regional host can look up any account.</summary>
    public Task<JsonDocument> AccountAsync(string gameName, string tagLine, CancellationToken ct)
    {
        string host = Region.Regional == "sea" ? "asia" : Region.Regional;
        return GetAsync($"https://{host}.api.riotgames.com/riot/account/v1/accounts/by-riot-id/{Uri.EscapeDataString(gameName)}/{Uri.EscapeDataString(tagLine)}", "account", ct);
    }

    public Task<JsonDocument> MatchAsync(string matchId, CancellationToken ct) =>
        GetAsync($"https://{Region.Regional}.api.riotgames.com/lol/match/v5/matches/{matchId}", "match", ct);

    public Task<JsonDocument> TimelineAsync(string matchId, CancellationToken ct) =>
        GetAsync($"https://{Region.Regional}.api.riotgames.com/lol/match/v5/matches/{matchId}/timeline", "timeline", ct);

    private async Task<JsonDocument> GetAsync(string url, string method, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            if (!_methods.TryGetValue(method, out var methodLimit)) _methods[method] = methodLimit = new RateLimiter("");
            await _app.WaitAsync(ct);
            await methodLimit.WaitAsync(ct);

            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.Headers.TryGetValues("X-App-Rate-Limit", out var appSpec)) _app.Configure(appSpec.First());
            if (response.Headers.TryGetValues("X-Method-Rate-Limit", out var methodSpec)) methodLimit.Configure(methodSpec.First());

            switch (response.StatusCode)
            {
                case HttpStatusCode.OK:
                    await using (var stream = await response.Content.ReadAsStreamAsync(ct))
                        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new RiotKeyException(response.StatusCode);
                case HttpStatusCode.TooManyRequests:
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10);
                    Debug.WriteLine($"Riot API 429 on {method}; waiting {wait.TotalSeconds}s");
                    await Task.Delay(wait, ct);
                    continue;
                case >= HttpStatusCode.InternalServerError when attempt < 3:
                    await Task.Delay(TimeSpan.FromSeconds(5 * (attempt + 1)), ct);
                    continue;
                default:
                    throw new HttpRequestException($"Riot API {(int)response.StatusCode} for {method}", null, response.StatusCode);
            }
        }
    }
}

/// <summary>
/// Sliding-window limiter for Riot's "count:seconds,count:seconds" limits. Keeps a small safety margin
/// because the server's windows don't start exactly when ours do.
/// </summary>
public sealed class RateLimiter
{
    private readonly object _lock = new();
    private (int Count, TimeSpan Window)[] _limits = [];
    private readonly Queue<DateTime> _sent = new();
    private readonly Func<DateTime> _now;
    private string _spec = "";

    public RateLimiter(string spec, Func<DateTime>? clock = null)
    {
        _now = clock ?? (() => DateTime.UtcNow);
        Configure(spec);
    }

    public void Configure(string spec)
    {
        lock (_lock)
        {
            if (spec == _spec) return;
            _spec = spec;
            _limits = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => p.Split(':'))
                .Where(p => p.Length == 2 && int.TryParse(p[0], out _) && int.TryParse(p[1], out _))
                .Select(p => (Count: Math.Max(1, (int)(int.Parse(p[0]) * 0.9)), Window: TimeSpan.FromSeconds(int.Parse(p[1]) + 0.5)))
                .ToArray();
        }
    }

    /// <summary>How long to wait before the next request is allowed (zero = go now, and it's counted).</summary>
    public TimeSpan TryAcquire()
    {
        lock (_lock)
        {
            var now = _now();
            var longest = _limits.Length == 0 ? TimeSpan.Zero : _limits.Max(l => l.Window);
            while (_sent.Count > 0 && now - _sent.Peek() > longest) _sent.Dequeue();

            var wait = TimeSpan.Zero;
            foreach (var (count, window) in _limits)
            {
                var inWindow = _sent.Where(t => now - t < window).ToList();
                if (inWindow.Count >= count)
                {
                    var until = inWindow[inWindow.Count - count] + window - now; // when the oldest one that matters expires
                    if (until > wait) wait = until;
                }
            }
            if (wait <= TimeSpan.Zero) _sent.Enqueue(now);
            return wait;
        }
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        for (TimeSpan wait; (wait = TryAcquire()) > TimeSpan.Zero;)
            await Task.Delay(wait, ct);
    }
}
