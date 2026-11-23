using System.Diagnostics;
using System.Net;
using System.Text.Json;
using LeagueOverlay.Core.Data;

namespace LeagueOverlay.Core.Builds;

public enum CrawlState { NoKey, Starting, Collecting, Paused, Complete, KeyRejected, Offline }

public sealed record CrawlStatus(CrawlState State, string Region, string Patch, int Games, int Target)
{
    public static CrawlStatus NoKey => new(CrawlState.NoKey, "", "", 0, BuildCrawler.TargetGames);
}

/// <summary>
/// Collects builds from recent ranked solo games of Challenger and Grandmaster players in the chosen region:
/// for each game, the match (champions, roles, result) and its timeline (purchase order).
/// Runs in the background, pauses while you're in a game, saves as it goes and stops once the patch has enough games.
/// </summary>
public sealed class BuildCrawler
{
    /// <summary>Games per patch to collect. At a development key's rate this takes roughly two hours.</summary>
    public const int TargetGames = 3000;
    private const int SaveEvery = 10;
    private static readonly string[] Ladders = ["challengerleagues", "grandmasterleagues"];

    private readonly RiotClient _client;
    private readonly BuildLibrary _library;
    private readonly ItemCatalog _items;
    private readonly Func<bool> _isPaused;
    private CrawlState _state = CrawlState.Starting;

    public event Action<CrawlStatus>? StatusChanged;

    public BuildCrawler(RiotClient client, BuildLibrary library, ItemCatalog items, Func<bool> isPaused)
    {
        _client = client;
        _library = library;
        _items = items;
        _isPaused = isPaused;
    }

    private BuildStore Current => _library.Current;

    public CrawlStatus Status => new(_state, _client.Region.Platform, Current.Patch, Current.MatchCount, TargetGames);

    private void Report(CrawlState state)
    {
        _state = state;
        StatusChanged?.Invoke(Status);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        Report(Current.MatchCount >= TargetGames ? CrawlState.Complete : CrawlState.Starting);
        if (_state == CrawlState.Complete) return;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (await CrawlAsync(ct)) { Report(CrawlState.Complete); return; }
                }
                catch (RiotKeyException) { Report(CrawlState.KeyRejected); return; }
                catch (Exception e) when (e is HttpRequestException or IOException || (e is TaskCanceledException && !ct.IsCancellationRequested))
                {
                    Debug.WriteLine($"Build crawl interrupted: {e.Message}");
                    Report(CrawlState.Offline);
                    await Task.Delay(TimeSpan.FromMinutes(1), ct);
                }
                finally { SaveAll(); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    /// <summary>One pass over the ladder. True once the current patch has enough games.</summary>
    private async Task<bool> CrawlAsync(CancellationToken ct)
    {
        await WhileInGameAsync(ct);
        var players = new List<string>();
        foreach (var ladder in Ladders)
        {
            using var doc = await _client.LeagueAsync(ladder, ct);
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateArray())
                if (e.TryGetProperty("puuid", out var p) && p.GetString() is { Length: > 0 } puuid) players.Add(puuid);
        }
        Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(players));
        Report(CrawlState.Collecting);

        // Patches last two weeks; older games would be filtered out by their version anyway.
        long since = DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds();
        var notUsable = new HashSet<string>();
        int unsaved = 0;

        foreach (var puuid in players)
        {
            if (Current.MatchCount >= TargetGames) return true;
            await WhileInGameAsync(ct);
            List<string> ids;
            using (var doc = await _client.MatchIdsAsync(puuid, since, 20, ct))
                ids = doc.RootElement.EnumerateArray().Select(x => x.GetString()!).ToList();

            foreach (var id in ids)
            {
                if (Current.Contains(id) || _library.Previous?.Contains(id) == true || notUsable.Contains(id)) continue;
                await WhileInGameAsync(ct);
                try
                {
                    using var match = await _client.MatchAsync(id, ct);
                    var patch = MatchParser.UsablePatch(match.RootElement);
                    var store = patch == Current.Patch ? Current
                        : _library.Previous is { } prev && patch == prev.Patch && prev.MatchCount < TargetGames ? prev
                        : null;
                    if (store is null) { notUsable.Add(id); continue; }

                    using var timeline = await _client.TimelineAsync(id, ct);
                    store.Add(id, MatchParser.Parse(match.RootElement, timeline.RootElement, _items));
                }
                catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { notUsable.Add(id); continue; }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    Debug.WriteLine($"Skipping {id}: {e.Message}"); // unexpected shape; don't let one game stop the crawl
                    notUsable.Add(id);
                    continue;
                }

                if (++unsaved >= SaveEvery) { SaveAll(); unsaved = 0; }
                Report(CrawlState.Collecting);
                if (Current.MatchCount >= TargetGames) return true;
            }
        }
        return Current.MatchCount >= TargetGames;
    }

    /// <summary>Never compete with the game for bandwidth: wait while a game is running.</summary>
    private async Task WhileInGameAsync(CancellationToken ct)
    {
        if (!_isPaused()) return;
        SaveAll();
        Report(CrawlState.Paused);
        while (_isPaused()) await Task.Delay(TimeSpan.FromSeconds(5), ct);
        Report(CrawlState.Collecting);
    }

    private void SaveAll()
    {
        try
        {
            Current.Save();
            _library.Previous?.Save();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Debug.WriteLine($"Saving builds failed: {e.Message}"); }
    }
}
