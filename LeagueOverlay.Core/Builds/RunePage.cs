using System.Text.Json;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Data;

namespace LeagueOverlay.Core.Builds;

/// <summary>
/// A full rune page: primary and secondary tree, and 9 rune ids in the client's order
/// (keystone, 3 primary runes, 2 secondary runes, 3 stat shards: offense, flex, defense).
/// </summary>
public sealed record RuneSetup(int Primary, int Secondary, ValueList<int> Perks)
{
    public int Keystone => Perks.Count > 0 ? Perks[0] : 0;
    public bool IsComplete => Perks.Count == 9 && Primary > 0 && Secondary > 0;

    /// <summary>Stored form: [primary, secondary, 9 perks].</summary>
    public int[] ToArray() => new[] { Primary, Secondary }.Concat(Perks).ToArray();
    public static RuneSetup? FromArray(int[]? a) => a is { Length: 11 } ? new RuneSetup(a[0], a[1], new ValueList<int>(a.Skip(2))) : null;
}

/// <summary>Reads runes out of a match-v5 participant.</summary>
public static class RuneParse
{
    /// <summary>
    /// The page and each rune's end-of-game numbers: vars has 3 values (var1-var3) for each of the 6 tree runes, in page order.
    /// Null when the participant has no perks (e.g. some special modes).
    /// </summary>
    public static (RuneSetup Page, int[] Vars)? From(JsonElement participant)
    {
        if (!participant.TryGetProperty("perks", out var perks) || perks.ValueKind != JsonValueKind.Object) return null;
        if (!perks.TryGetProperty("styles", out var styles) || styles.GetArrayLength() < 2) return null;
        var ids = new List<int>();
        var vars = new List<int>();
        int primary = 0, secondary = 0;
        foreach (var style in styles.EnumerateArray())
        {
            string desc = style.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
            int id = style.TryGetProperty("style", out var s) ? s.GetInt32() : 0;
            if (desc == "primaryStyle") primary = id; else secondary = id;
        }
        foreach (string which in new[] { "primaryStyle", "subStyle" })
        {
            var style = styles.EnumerateArray().FirstOrDefault(x => x.TryGetProperty("description", out var d) && d.GetString() == which);
            if (style.ValueKind != JsonValueKind.Object) return null;
            foreach (var sel in style.GetProperty("selections").EnumerateArray())
            {
                ids.Add(sel.GetProperty("perk").GetInt32());
                for (int v = 1; v <= 3; v++) vars.Add(sel.TryGetProperty($"var{v}", out var x) && x.ValueKind == JsonValueKind.Number ? (int)x.GetDouble() : 0);
            }
        }
        if (perks.TryGetProperty("statPerks", out var stat))
            foreach (string slot in new[] { "offense", "flex", "defense" })
                ids.Add(stat.TryGetProperty(slot, out var x) ? x.GetInt32() : 0);
        var page = new RuneSetup(primary, secondary, new ValueList<int>(ids));
        return page.IsComplete ? (page, vars.ToArray()) : null;
    }
}

/// <summary>The rune page high-ranked players run most on a champion (in a role), with how often it won.</summary>
public sealed record RunePage(RuneSetup Setup, int Games, double Share, double WinRate, string? Position, string Patch)
{
    /// <summary>Most common keystone first, then the most common full page with that keystone.</summary>
    public static RunePage? From(IReadOnlyList<BuildRecord> builds, string? position, string patch)
    {
        var pages = builds.Select(b => (Build: b, Page: RuneSetup.FromArray(b.Runes))).Where(x => x.Page is not null).ToList();
        if (pages.Count == 0) return null;
        var keystone = pages.GroupBy(x => x.Page!.Keystone).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).First();
        var top = keystone.GroupBy(x => x.Page!).OrderByDescending(g => g.Count()).First();
        return new RunePage(top.Key, pages.Count, top.Count() / (double)pages.Count, top.Count(x => x.Build.Win) / (double)top.Count(), position, patch);
    }
}

/// <summary>
/// What each rune usually does in high-ranked games, per minute (or as is, for percentages and uptimes): the median of
/// each end-of-game number, overall and per champion. The post-game rune analysis compares your game with these.
/// </summary>
public sealed class RuneBenchmarks
{
    /// <summary>Fewer games than this and a median says too little.</summary>
    public const int MinGames = 20;

    public sealed record Stat(double Median, int Games);

    private readonly Dictionary<(int Rune, int Var), Stat> _all;
    private readonly Dictionary<(int Champion, int Rune, int Var), Stat> _byChampion;

    private RuneBenchmarks(Dictionary<(int, int), Stat> all, Dictionary<(int, int, int), Stat> byChampion)
    {
        _all = all;
        _byChampion = byChampion;
    }

    public static readonly RuneBenchmarks Empty = new(new(), new());
    public bool IsEmpty => _all.Count == 0;

    /// <summary>Per minute when perMinute, else the raw value. Champion-specific when there are enough games, else all champions.</summary>
    public Stat? For(int rune, int var, int champion, bool perMinute, out bool championSpecific)
    {
        int key = perMinute ? var : -var;
        championSpecific = _byChampion.TryGetValue((champion, rune, key), out var c) && c.Games >= MinGames;
        if (championSpecific) return c;
        return _all.TryGetValue((rune, key), out var a) && a.Games >= MinGames ? a : null;
    }

    public static RuneBenchmarks From(IEnumerable<BuildRecord> builds)
    {
        // Var keys: +n = var n per minute, -n = var n raw.
        var all = new Dictionary<(int, int), List<double>>();
        var byChamp = new Dictionary<(int, int, int), List<double>>();
        foreach (var b in builds)
        {
            if (RuneSetup.FromArray(b.Runes) is not { } page || b.RuneVars is not { Length: 18 } vars || b.Duration < 600) continue;
            double minutes = b.Duration / 60.0;
            for (int i = 0; i < 6; i++)
            for (int v = 1; v <= 3; v++)
            {
                int raw = vars[i * 3 + v - 1];
                if (raw == 0) continue;
                int rune = page.Perks[i];
                Add(all, (rune, v), raw / minutes); Add(all, (rune, -v), raw);
                Add(byChamp, (b.Champion, rune, v), raw / minutes); Add(byChamp, (b.Champion, rune, -v), raw);
            }
        }
        return new RuneBenchmarks(all.ToDictionary(x => x.Key, x => Median(x.Value)), byChamp.ToDictionary(x => x.Key, x => Median(x.Value)));

        static void Add<TK>(Dictionary<TK, List<double>> d, TK key, double v) where TK : notnull
        {
            if (!d.TryGetValue(key, out var l)) d[key] = l = new List<double>();
            l.Add(v);
        }
        static Stat Median(List<double> values)
        {
            values.Sort();
            int n = values.Count;
            return new Stat(n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2, n);
        }
    }
}
