using System.Text.Json;

namespace LeagueOverlay.Core.Data;

public sealed record ChampionInfo(string Id, string Name, IReadOnlyList<string> Tags, int Attack, int Magic, int Defense)
{
    /// <summary>Numeric champion id ("key" in champion.json), as used by match-v5 (championId).</summary>
    public int Key { get; init; }
    /// <summary>Base stats from champion.json ("hp", "hpperlevel", "armor", "attackrange", ...).</summary>
    public IReadOnlyDictionary<string, double> BaseStats { get; init; } = new Dictionary<string, double>();

    /// <summary>A stat at a level, with League's growth curve: base + growth × (L−1) × (0.7025 + 0.0175 × (L−1)).</summary>
    public double StatAt(string stat, int level)
    {
        double b = BaseStats.GetValueOrDefault(stat), g = BaseStats.GetValueOrDefault(stat + "perlevel");
        int l = Math.Max(1, level) - 1;
        return b + g * l * (0.7025 + 0.0175 * l);
    }
    public string PrimaryTag => Tags.Count > 0 ? Tags[0] : "Fighter";
    public bool HasTag(string tag) => Tags.Contains(tag);
}

public sealed class StaticData
{
    public static readonly StaticData Empty = new("", new Dictionary<string, ChampionInfo>(), ItemCatalog.Empty);

    public string Version { get; }
    /// <summary>Keyed by Data Dragon id ("MonkeyKing"), case-insensitive.</summary>
    public IReadOnlyDictionary<string, ChampionInfo> Champions { get; }
    public ItemCatalog Items { get; }
    /// <summary>Rune trees, shards and end-of-game rune stat labels (empty if they couldn't be loaded).</summary>
    public RuneCatalog Runes { get; }
    /// <summary>How each champion's abilities scale (Meraki Analytics), for surprise mode; empty if it couldn't be loaded.</summary>
    public ChampionKits Kits { get; init; } = ChampionKits.Empty;
    public bool IsLoaded => Items.ById.Count > 0;

    public StaticData(string version, IReadOnlyDictionary<string, ChampionInfo> champions, ItemCatalog items, RuneCatalog? runes = null)
    {
        Version = version;
        Champions = champions;
        Items = items;
        Runes = runes ?? RuneCatalog.Empty;
    }

    public ChampionInfo? Champion(string key) => Champions.TryGetValue(key, out var c) ? c : null;

    public ChampionInfo? ChampionByKey(int numericKey) => _byKey.TryGetValue(numericKey, out var c) ? c : null;
    private Dictionary<int, ChampionInfo> _byKey => __byKey ??= Champions.Values.Where(c => c.Key > 0).DistinctBy(c => c.Key).ToDictionary(c => c.Key);
    private Dictionary<int, ChampionInfo>? __byKey;

    /// <summary>"16.23.1" -> "16.23", the form match-v5 game versions are compared in.</summary>
    public string Patch => PatchOf(Version);
    public static string PatchOf(string version) => string.Join('.', version.Split('.').Take(2));

    public static StaticData Parse(string version, string championJson, string itemJson, RuneCatalog? runes = null)
    {
        using var doc = JsonDocument.Parse(championJson);
        var champions = new Dictionary<string, ChampionInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in doc.RootElement.GetProperty("data").EnumerateObject())
        {
            var c = prop.Value;
            var info = c.GetProperty("info");
            champions[prop.Name] = new ChampionInfo(
                prop.Name,
                c.GetProperty("name").GetString() ?? prop.Name,
                c.GetProperty("tags").EnumerateArray().Select(t => t.GetString() ?? "").ToList(),
                info.GetProperty("attack").GetInt32(),
                info.GetProperty("magic").GetInt32(),
                info.GetProperty("defense").GetInt32())
            {
                Key = c.TryGetProperty("key", out var k) && int.TryParse(k.GetString(), out var key) ? key : 0,
                BaseStats = c.TryGetProperty("stats", out var st)
                    ? st.EnumerateObject().Where(x => x.Value.ValueKind == JsonValueKind.Number).ToDictionary(x => x.Name, x => x.Value.GetDouble())
                    : new Dictionary<string, double>(),
            };
        }
        return new StaticData(version, champions, ItemCatalog.Parse(itemJson), runes);
    }
}

/// <summary>Downloads champion and item data from Data Dragon, cached on disk once per patch.</summary>
public static class DataDragon
{
    private const string Cdn = "https://ddragon.leagueoflegends.com";

    public static string DefaultCacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueOverlay", "ddragon");

    public static async Task<StaticData> LoadAsync(HttpClient http, string cacheRoot, CancellationToken ct = default)
    {
        string? version = null;
        try
        {
            using var versions = JsonDocument.Parse(await http.GetStringAsync($"{Cdn}/api/versions.json", ct));
            version = versions.RootElement[0].GetString();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Offline: fall back to the newest patch already on disk.
            version = Directory.Exists(cacheRoot)
                ? Directory.GetDirectories(cacheRoot).Select(Path.GetFileName).OrderByDescending(v => v, VersionComparer.Instance).FirstOrDefault()
                : null;
        }
        if (version is null) throw new InvalidOperationException("Data Dragon unreachable and nothing cached yet.");

        var dir = Path.Combine(cacheRoot, version);
        Directory.CreateDirectory(dir);
        var champions = await CachedAsync(http, dir, "champion.json", $"{Cdn}/cdn/{version}/data/en_US/champion.json", ct);
        var items = await CachedAsync(http, dir, "item.json", $"{Cdn}/cdn/{version}/data/en_US/item.json", ct);
        var data = StaticData.Parse(version, champions, items, await LoadRunesAsync(http, dir, version, ct));
        return new StaticData(data.Version, data.Champions, data.Items, data.Runes) { Kits = await LoadKitsAsync(http, dir, ct) };
    }

    public const string MerakiChampionsUrl = "https://cdn.merakianalytics.com/riot/lol/resources/latest/en-US/champions.json";

    /// <summary>
    /// Ability scaling per champion from Meraki Analytics (~13 MB), boiled down to a few KB and cached per patch.
    /// Optional: without it, surprise mode falls back to Data Dragon's rough ratings.
    /// </summary>
    private static async Task<ChampionKits> LoadKitsAsync(HttpClient http, string dir, CancellationToken ct)
    {
        string path = Path.Combine(dir, "kits2.json"); // 2: positions and ratings
        try
        {
            if (File.Exists(path)) return ChampionKits.FromCacheJson(await File.ReadAllTextAsync(path, ct));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(2));
            await using var stream = await http.GetStreamAsync(MerakiChampionsUrl, cts.Token);
            var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cts.Token);
            buffer.Position = 0;
            var kits = ChampionKits.ParseMeraki(buffer);
            if (kits.Count > 100)
            {
                await File.WriteAllTextAsync(path + ".tmp", kits.ToCacheJson(), ct);
                File.Move(path + ".tmp", path, overwrite: true);
            }
            return kits;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or JsonException && !ct.IsCancellationRequested)
        {
            return ChampionKits.Empty;
        }
    }

    /// <summary>
    /// Rune trees (Data Dragon) plus the labels of the end-of-game rune numbers (CommunityDragon). Either can fail without
    /// stopping the app: no runes means no rune suggestions, no labels means rune numbers aren't analysed.
    /// </summary>
    private static async Task<RuneCatalog> LoadRunesAsync(HttpClient http, string dir, string version, CancellationToken ct)
    {
        string? runes = null, perks = null;
        try { runes = await CachedAsync(http, dir, "runesReforged.json", $"{Cdn}/cdn/{version}/data/en_US/runesReforged.json", ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested) { }
        if (runes is null) return RuneCatalog.Empty;
        try { perks = await CachedAsync(http, dir, "perks.json", "https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/v1/perks.json", ct); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !ct.IsCancellationRequested) { }
        try { return RuneCatalog.Parse(runes, perks); }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return RuneCatalog.Empty; }
    }

    private static async Task<string> CachedAsync(HttpClient http, string dir, string file, string url, CancellationToken ct)
    {
        var path = Path.Combine(dir, file);
        if (File.Exists(path)) return await File.ReadAllTextAsync(path, ct);
        var text = await http.GetStringAsync(url, ct);
        await File.WriteAllTextAsync(path + ".tmp", text, ct);
        File.Move(path + ".tmp", path, overwrite: true);
        return text;
    }

    private sealed class VersionComparer : IComparer<string?>
    {
        public static readonly VersionComparer Instance = new();
        public int Compare(string? a, string? b) =>
            (Version.TryParse(a, out var va), Version.TryParse(b, out var vb)) switch
            {
                (true, true) => va!.CompareTo(vb),
                _ => string.CompareOrdinal(a, b),
            };
    }
}
