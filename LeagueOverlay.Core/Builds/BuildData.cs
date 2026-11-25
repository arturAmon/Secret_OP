using System.Text.Json;
using System.Text.Json.Serialization;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.Builds;

/// <summary>
/// One player's build from one ranked game: finished items in the order they were bought, plus their boots.
/// Games collected since runes were added also have the rune page (<see cref="RuneSetup.ToArray"/>), each rune's
/// end-of-game numbers (3 per tree rune) and the game length; older ones have null / 0 there.
/// </summary>
public sealed record BuildRecord(
    [property: JsonPropertyName("c")] int Champion,
    [property: JsonPropertyName("p")] string Position,
    [property: JsonPropertyName("w")] bool Win,
    [property: JsonPropertyName("i")] int[] Items,
    [property: JsonPropertyName("b")] int Boots,
    [property: JsonPropertyName("r"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? Runes = null,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? RuneVars = null,
    [property: JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int Duration = 0,
    [property: JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int[]? Starter = null)
{
    public bool HasRunes => Runes is { Length: 11 };
}

/// <summary>Turns a match-v5 match + timeline into builds.</summary>
public static class MatchParser
{
    public const int RankedSoloQueue = 420;
    /// <summary>Shorter games are remakes or early surrenders whose builds aren't finished enough to learn from.</summary>
    public const int MinGameSeconds = 15 * 60;

    /// <summary>The game's patch ("16.23"), or null if the game isn't a finished ranked solo game.</summary>
    public static string? UsablePatch(JsonElement match)
    {
        var info = match.GetProperty("info");
        if (info.GetProperty("queueId").GetInt32() != RankedSoloQueue) return null;
        if (info.GetProperty("gameDuration").GetInt64() < MinGameSeconds) return null;
        return StaticData.PatchOf(info.GetProperty("gameVersion").GetString() ?? "");
    }

    public static List<BuildRecord> Parse(JsonElement match, JsonElement timeline, ItemCatalog items)
    {
        // Purchase order from the timeline, with undos taken back out.
        var bought = new Dictionary<int, List<int>>();
        var starters = new Dictionary<int, List<int>>(); // bought in the first minute, trinkets aside
        const long StarterMs = 60_000;
        foreach (var frame in timeline.GetProperty("info").GetProperty("frames").EnumerateArray())
        foreach (var e in frame.GetProperty("events").EnumerateArray())
        {
            string? type = e.GetProperty("type").GetString();
            if (type is not ("ITEM_PURCHASED" or "ITEM_UNDO")) continue;
            int pid = e.TryGetProperty("participantId", out var p) ? p.GetInt32() : 0;
            if (pid is < 1 or > 10) continue;
            var list = bought.TryGetValue(pid, out var l) ? l : bought[pid] = new List<int>();
            var start = starters.TryGetValue(pid, out var st) ? st : starters[pid] = new List<int>();
            bool early = e.TryGetProperty("timestamp", out var ts) && ts.GetInt64() < StarterMs;
            if (type == "ITEM_PURCHASED")
            {
                int id = e.GetProperty("itemId").GetInt32();
                list.Add(id);
                if (early && items.Get(id)?.Tags.Contains("Trinket") != true) start.Add(id);
            }
            else if (e.TryGetProperty("beforeId", out var before))
            {
                if (list.LastIndexOf(before.GetInt32()) is >= 0 and var idx) list.RemoveAt(idx);
                if (early && start.LastIndexOf(before.GetInt32()) is >= 0 and var sidx) start.RemoveAt(sidx);
            }
        }

        var result = new List<BuildRecord>();
        int duration = match.GetProperty("info").TryGetProperty("gameDuration", out var gd) ? (int)gd.GetInt64() : 0;
        foreach (var participant in match.GetProperty("info").GetProperty("participants").EnumerateArray())
        {
            int pid = participant.GetProperty("participantId").GetInt32();
            var purchases = bought.GetValueOrDefault(pid) ?? [];
            var core = new List<int>();
            int boots = 0;
            foreach (int id in purchases)
            {
                if (TierTwoBoots(id, items) is { } b) { if (boots == 0) boots = b; continue; }
                if (items.Get(id) is { IsCompleted: true, IsBoots: false, IsConsumableOrTrinket: false } && !core.Contains(id)) core.Add(id);
            }
            if (boots == 0 && participant.TryGetProperty("roleBoundItem", out var rb) && TierTwoBoots(rb.GetInt32(), items) is { } roleBoots)
                boots = roleBoots; // bot lane: boots live in the role quest slot

            var runes = RuneParse.From(participant);
            result.Add(new BuildRecord(
                participant.GetProperty("championId").GetInt32(),
                participant.TryGetProperty("teamPosition", out var pos) ? pos.GetString() ?? "" : "",
                participant.GetProperty("win").GetBoolean(),
                core.ToArray(),
                boots,
                runes?.Page.ToArray(),
                runes?.Vars,
                duration,
                starters.GetValueOrDefault(pid) is { Count: > 0 } starter ? starter.Order().ToArray() : null));
        }
        return result;
    }

    /// <summary>The tier-2 boots an item is or came from (tier-3 boots count as the tier-2 pair they upgrade), else null.</summary>
    public static int? TierTwoBoots(int id, ItemCatalog items)
    {
        if (!items.IsUpgradedBoots(new PlayerItem { ItemID = id })) return null;
        var info = items.Get(id);
        while (info is { Depth: > 2, From.Count: > 0 } && items.Get(info.From[0]) is { } parent) info = parent;
        return info?.Id ?? id;
    }
}

/// <summary>Builds collected for one region and patch, saved under %LOCALAPPDATA%\LeagueOverlay\builds.</summary>
public sealed class BuildStore
{
    private sealed class FileShape
    {
        public int Format { get; set; } = 1;
        public string Platform { get; set; } = "";
        public string Patch { get; set; } = "";
        public List<string> Matches { get; set; } = new();
        public List<BuildRecord> Builds { get; set; } = new();
    }

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.Never };
    private readonly object _lock = new();
    private readonly HashSet<string> _matchSet;
    private readonly FileShape _file;
    private IReadOnlyList<BuildRecord>? _snapshot;

    public string Platform => _file.Platform;
    public string Patch => _file.Patch;
    public string FilePath { get; }
    public int Version { get; private set; }
    public int MatchCount { get { lock (_lock) return _file.Matches.Count; } }
    /// <summary>Games whose builds include rune pages (games collected before runes were added don't).</summary>
    public int RuneMatchCount { get { lock (_lock) return _runeMatches; } }
    private int _runeMatches;

    private BuildStore(string path, FileShape file)
    {
        FilePath = path;
        _file = file;
        _matchSet = file.Matches.ToHashSet();
        int per = file.Matches.Count == 0 ? 10 : Math.Max(1, file.Builds.Count / file.Matches.Count);
        _runeMatches = file.Builds.Count(b => b.HasRunes) / per;
    }

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueOverlay", "builds");

    public static BuildStore Load(string root, string platform, string patch)
    {
        string path = Path.Combine(root, platform, $"{patch}.json");
        FileShape? file = null;
        try { if (File.Exists(path)) file = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Json); }
        catch (Exception e) when (e is JsonException or IOException) { /* start over */ }
        if (file is null || file.Platform != platform || file.Patch != patch) file = new FileShape { Platform = platform, Patch = patch };
        return new BuildStore(path, file);
    }

    public bool Contains(string matchId) { lock (_lock) return _matchSet.Contains(matchId); }

    public void Add(string matchId, IEnumerable<BuildRecord> builds)
    {
        lock (_lock)
        {
            if (!_matchSet.Add(matchId)) return;
            _file.Matches.Add(matchId);
            var list = builds.ToList();
            _file.Builds.AddRange(list);
            if (list.Any(b => b.HasRunes)) _runeMatches++;
            _snapshot = null;
            Version++;
        }
    }

    public IReadOnlyList<BuildRecord> Builds { get { lock (_lock) return _snapshot ??= _file.Builds.ToArray(); } }

    /// <summary>Match ids with their builds. Every collected match adds one build per player, in order, so builds group by match.</summary>
    public IReadOnlyList<(string MatchId, BuildRecord[] Builds)> ByMatch()
    {
        lock (_lock)
        {
            int per = _file.Matches.Count == 0 ? 0 : _file.Builds.Count / _file.Matches.Count;
            if (per == 0 || per * _file.Matches.Count != _file.Builds.Count) return [];
            return _file.Matches.Select((id, i) => (id, _file.Builds.GetRange(i * per, per).ToArray())).ToList();
        }
    }

    private int _savedVersion;

    /// <summary>Writes the file if anything was added since the last save.</summary>
    public void Save()
    {
        string json;
        lock (_lock)
        {
            if (Version == _savedVersion) return;
            json = JsonSerializer.Serialize(_file, Json);
            _savedVersion = Version;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath + ".tmp", json);
        File.Move(FilePath + ".tmp", FilePath, overwrite: true);
    }

    /// <summary>The patch before "16.23" ("16.22"); null at the start of a season, when it can't be worked out.</summary>
    public static string? PreviousPatch(string patch)
    {
        var parts = patch.Split('.');
        return parts.Length == 2 && int.TryParse(parts[1], out var minor) && minor > 1 ? $"{parts[0]}.{minor - 1}" : null;
    }

    /// <summary>Deletes saved patches older than the ones kept (current and previous).</summary>
    public static void Prune(string root, string platform, IEnumerable<string> keepPatches)
    {
        var dir = Path.Combine(root, platform);
        if (!Directory.Exists(dir)) return;
        var keep = keepPatches.Select(p => p + ".json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Directory.GetFiles(dir, "*.json"))
            if (!keep.Contains(Path.GetFileName(f))) try { File.Delete(f); } catch (IOException) { }
    }
}

/// <summary>What high-ranked players build on one champion.</summary>
/// <param name="Path">Most common finished item at each step (1st item, 2nd item, ...).</param>
/// <param name="PathShare">Share of games with that item at that step.</param>
/// <param name="ItemShare">Share of games in which each finished item was bought at all.</param>
/// <param name="BootsShare">Share of games ending with each tier-2 boots.</param>
public sealed record ChampionBuild(
    int Games,
    double WinRate,
    IReadOnlyList<int> Path,
    IReadOnlyList<double> PathShare,
    IReadOnlyDictionary<int, double> ItemShare,
    IReadOnlyDictionary<int, double> BootsShare,
    string? Position,
    string Patch)
{
    public double Share(int itemId) => ItemShare.GetValueOrDefault(itemId);

    /// <summary>Most common starting items (sorted, an entry per copy); empty when the games don't record them.</summary>
    public IReadOnlyList<int> Starter { get; init; } = [];
    public double StarterShare { get; init; }

    public static ChampionBuild? From(IReadOnlyList<BuildRecord> builds, string? position, string patch)
    {
        if (builds.Count == 0) return null;
        var path = new List<int>();
        var pathShare = new List<double>();
        for (int step = 0; step < 6; step++)
        {
            // Most common item bought as the (step+1)-th finished item, among items not already in the path.
            var top = builds.Where(b => b.Items.Length > step).Select(b => b.Items[step])
                .Where(id => !path.Contains(id))
                .GroupBy(id => id).Select(g => (Id: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count).ThenBy(x => x.Id)
                .FirstOrDefault();
            if (top.Count == 0) break;
            path.Add(top.Id);
            pathShare.Add(top.Count / (double)builds.Count);
        }

        var itemShare = builds.SelectMany(b => b.Items.Distinct()).GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count() / (double)builds.Count);
        var bootsShare = builds.Where(b => b.Boots != 0).GroupBy(b => b.Boots)
            .ToDictionary(g => g.Key, g => g.Count() / (double)builds.Count);
        var starts = builds.Where(b => b.Starter is { Length: > 0 }).Select(b => b.Starter!).ToList();
        // Only from enough games that recorded it (older collected games don't).
        var topStart = starts.Count < 10 ? null : starts.GroupBy(s => string.Join(",", s)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        return new ChampionBuild(builds.Count, builds.Count(b => b.Win) / (double)builds.Count, path, pathShare, itemShare, bootsShare, position, patch)
        {
            Starter = topStart?.First() ?? [],
            StarterShare = topStart is null ? 0 : topStart.Count() / (double)starts.Count,
        };
    }
}

/// <summary>
/// Popular builds for the current patch, topped up with the previous patch while the current one has few games
/// (for example on the first days of a new patch).
/// </summary>
public sealed class BuildLibrary
{
    /// <summary>Fewer games than this on a champion and the numbers are too noisy to call "most common".</summary>
    public const int MinGames = 12;

    private readonly Dictionary<(int, string?), (int Version, ChampionBuild? Build)> _cache = new();
    private readonly object _lock = new();

    public BuildStore Current { get; }
    public BuildStore? Previous { get; }

    public BuildLibrary(BuildStore current, BuildStore? previous)
    {
        Current = current;
        Previous = previous;
    }

    private int Version => Current.Version * 100_000 + (Previous?.Version ?? 0);

    /// <summary>Builds for a champion (numeric key) in a role ("BOTTOM", ...). Falls back to all roles, then to the previous patch.</summary>
    public ChampionBuild? For(int championKey, string? position)
    {
        lock (_lock)
        {
            int version = Version;
            if (_cache.TryGetValue((championKey, position), out var hit) && hit.Version == version) return hit.Build;
            var build = Compute(championKey, string.IsNullOrEmpty(position) ? null : position.ToUpperInvariant());
            _cache[(championKey, position)] = (version, build);
            return build;
        }
    }

    private readonly Dictionary<(int, string?), (int Version, RunePage? Page)> _runeCache = new();
    private (int Version, RuneBenchmarks Bench)? _bench;

    /// <summary>The most common rune page for a champion in a role, falling back the same way as <see cref="For"/>.</summary>
    public RunePage? Runes(int championKey, string? position)
    {
        lock (_lock)
        {
            int version = Version;
            if (_runeCache.TryGetValue((championKey, position), out var hit) && hit.Version == version) return hit.Page;
            string? role = string.IsNullOrEmpty(position) ? null : position.ToUpperInvariant();
            var page = Pick(championKey, role, b => b.HasRunes, RunePage.From);
            _runeCache[(championKey, position)] = (version, page);
            return page;
        }
    }

    private (int Version, MatchupStats Stats)? _matchups;

    /// <summary>Role, lane matchup and synergy win rates from every collected game (both patches).</summary>
    public MatchupStats Matchups
    {
        get
        {
            lock (_lock)
            {
                int version = Version;
                if (_matchups is { } m && m.Version == version) return m.Stats;
                var groups = Current.ByMatch().Select(g => g.Builds);
                if (Previous is not null) groups = groups.Concat(Previous.ByMatch().Select(g => g.Builds));
                var stats = MatchupStats.From(groups);
                _matchups = (version, stats);
                return stats;
            }
        }
    }

    /// <summary>Typical end-of-game rune numbers from every collected game (both patches).</summary>
    public RuneBenchmarks Benchmarks
    {
        get
        {
            lock (_lock)
            {
                int version = Version;
                if (_bench is { } b && b.Version == version) return b.Bench;
                var bench = RuneBenchmarks.From(Previous is null ? Current.Builds : Current.Builds.Concat(Previous.Builds));
                _bench = (version, bench);
                return bench;
            }
        }
    }

    private ChampionBuild? Compute(int champ, string? position) => Pick(champ, position, _ => true, ChampionBuild.From);

    private T? Pick<T>(int champ, string? position, Func<BuildRecord, bool> usable, Func<IReadOnlyList<BuildRecord>, string?, string, T?> make) where T : class
    {
        var current = Current.Builds.Where(b => b.Champion == champ && usable(b)).ToList();
        var both = Previous is null ? current : current.Concat(Previous.Builds.Where(b => b.Champion == champ && usable(b))).ToList();
        string bothPatch = Previous is null ? Current.Patch : $"{Current.Patch} + {Previous.Patch}";

        // The role matters more than the patch (support Ashe builds nothing like ADC Ashe), so try both patches
        // in the role before giving up on the role.
        var pools = new[] { (current, Current.Patch), (both, bothPatch) };
        if (position is not null)
        {
            foreach (var (pool, patch) in pools)
            {
                var inRole = pool.Where(b => b.Position == position).ToList();
                if (inRole.Count >= MinGames) return make(inRole, position, patch);
            }
        }
        foreach (var (pool, patch) in pools)
            if (pool.Count >= MinGames) return make(pool, null, patch);
        return null;
    }
}
