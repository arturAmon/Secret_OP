using System.Net.Security;
using System.Text.Json;

namespace LeagueOverlay.Core.Live;

/// <summary>Reads the League client's local Live Client Data API. Returns null whenever no game is running.</summary>
public sealed class LiveClient : IDisposable
{
    public static readonly Uri DefaultUri = new("https://127.0.0.1:2999/liveclientdata/allgamedata");

    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    public Uri Uri { get; }

    public LiveClient(Uri? uri = null)
    {
        Uri = uri ?? FromEnvironment() ?? DefaultUri;

        // The game client serves a self-signed certificate on 127.0.0.1:2999. This HttpClient only ever
        // talks to Uri, so certificate errors are tolerated only when Uri is that local endpoint.
        bool trustLocalGameCert = Uri.IsLoopback && Uri.Port == 2999;
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(1),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, errors) =>
                    errors == SslPolicyErrors.None || trustLocalGameCert,
            },
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1.5) };
    }

    private static Uri? FromEnvironment() =>
        Environment.GetEnvironmentVariable("LEAGUE_LIVE_URL") is { Length: > 0 } s && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    public async Task<LiveGame?> TryGetAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(Uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null; // e.g. loading screen
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var game = await JsonSerializer.DeserializeAsync<LiveGame>(stream, JsonOptions, ct);
            return game is { AllPlayers.Count: > 0 } ? game : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null; // not running / timed out / partial data
        }
    }

    public void Dispose() => _http.Dispose();
}
