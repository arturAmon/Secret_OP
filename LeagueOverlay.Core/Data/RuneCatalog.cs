using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeagueOverlay.Core.Data;

/// <summary>What kind of number an end-of-game rune stat is, so only like is compared with like.</summary>
public enum RuneStatKind { Damage, Healing, Shield, Gold, Count, Percent, Time, Other }

/// <summary>One end-of-game rune stat: "Total Damage Dealt" is var1 and counts damage.</summary>
public sealed record RuneStatLabel(string Label, int Var, RuneStatKind Kind)
{
    /// <summary>Totals grow with game length; percentages and times-of-uptime don't.</summary>
    public bool PerMinute => Kind is RuneStatKind.Damage or RuneStatKind.Healing or RuneStatKind.Shield or RuneStatKind.Gold or RuneStatKind.Count;
}

/// <summary>A rune (or stat shard): its tree, row (0 = keystone) and icon path under ddragon's cdn/img/.</summary>
public sealed record RuneInfo(int Id, string Name, int Style, int Row, string Icon, string ShortDesc);

public sealed record RuneStyle(int Id, string Name, string Icon, IReadOnlyList<IReadOnlyList<int>> Rows);

/// <summary>
/// The rune trees from Data Dragon's runesReforged.json, the stat shards (not in that file) and, when available,
/// the labels of each rune's end-of-game numbers from CommunityDragon's perks.json ("Total Damage Dealt: @eogvar1@").
/// </summary>
public sealed class RuneCatalog
{
    public static readonly RuneCatalog Empty = new([], new Dictionary<int, RuneInfo>(), new Dictionary<int, IReadOnlyList<RuneStatLabel>>());

    /// <summary>Stat shard rows as the client shows them: offense, flex, defense.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<int>> ShardRows = [[5008, 5005, 5007], [5008, 5010, 5001], [5011, 5013, 5001]];
    public const int ShardStyle = 5000;

    private static readonly Dictionary<int, (string Name, string Icon)> Shards = new()
    {
        [5008] = ("Adaptive Force", "perk-images/StatMods/StatModsAdaptiveForceIcon.png"),
        [5005] = ("Attack Speed", "perk-images/StatMods/StatModsAttackSpeedIcon.png"),
        [5007] = ("Ability Haste", "perk-images/StatMods/StatModsCDRScalingIcon.png"),
        [5010] = ("Move Speed", "perk-images/StatMods/StatModsMovementSpeedIcon.png"),
        [5001] = ("Health Scaling", "perk-images/StatMods/StatModsHealthPlusIcon.png"),
        [5011] = ("Health", "perk-images/StatMods/StatModsHealthScalingIcon.png"),
        [5013] = ("Tenacity and Slow Resist", "perk-images/StatMods/StatModsTenacityIcon.png"),
        [5002] = ("Armor", "perk-images/StatMods/StatModsArmorIcon.png"),
        [5003] = ("Magic Resist", "perk-images/StatMods/StatModsMagicResIcon.png"),
    };

    private readonly Dictionary<string, RuneInfo> _byName;

    public IReadOnlyList<RuneStyle> Styles { get; }
    public IReadOnlyDictionary<int, RuneInfo> ById { get; }
    public IReadOnlyDictionary<int, IReadOnlyList<RuneStatLabel>> StatLabels { get; }
    public bool IsLoaded => Styles.Count > 0;

    public RuneCatalog(IReadOnlyList<RuneStyle> styles, IReadOnlyDictionary<int, RuneInfo> byId, IReadOnlyDictionary<int, IReadOnlyList<RuneStatLabel>> labels)
    {
        Styles = styles;
        ById = byId;
        StatLabels = labels;
        _byName = byId.Values.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public RuneInfo? Get(int id) => ById.TryGetValue(id, out var r) ? r : null;
    public RuneInfo? ByName(string name) => _byName.TryGetValue(name, out var r) ? r : null;
    public RuneStyle? Style(int id) => Styles.FirstOrDefault(s => s.Id == id);
    public RuneStyle? StyleByName(string name) => Styles.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
    public string Name(int id) => Get(id)?.Name ?? Style(id)?.Name ?? $"Rune {id}";

    /// <summary>The other runes in the same row of the same tree (what you could have taken instead).</summary>
    public IReadOnlyList<int> Alternatives(int id) =>
        Get(id) is { Style: not ShardStyle } r && Style(r.Style) is { } s && r.Row < s.Rows.Count ? s.Rows[r.Row].Where(x => x != id).ToList() : [];

    public IReadOnlyList<RuneStatLabel> Labels(int id) => StatLabels.TryGetValue(id, out var l) ? l : [];

    /// <summary>The stat that says how much a rune did: its first damage / healing / shield / gold number.</summary>
    public RuneStatLabel? MainStat(int id) =>
        Labels(id).FirstOrDefault(l => l.Kind is RuneStatKind.Damage or RuneStatKind.Healing or RuneStatKind.Shield or RuneStatKind.Gold);

    public static RuneCatalog Parse(string runesReforgedJson, string? perksJson)
    {
        var styles = new List<RuneStyle>();
        var byId = new Dictionary<int, RuneInfo>();
        using (var doc = JsonDocument.Parse(runesReforgedJson))
        {
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                int styleId = s.GetProperty("id").GetInt32();
                var rows = new List<IReadOnlyList<int>>();
                int row = 0;
                foreach (var slot in s.GetProperty("slots").EnumerateArray())
                {
                    var ids = new List<int>();
                    foreach (var r in slot.GetProperty("runes").EnumerateArray())
                    {
                        int id = r.GetProperty("id").GetInt32();
                        ids.Add(id);
                        byId[id] = new RuneInfo(id, Str(r, "name"), styleId, row, Str(r, "icon"), StripTags(Str(r, "shortDesc")));
                    }
                    rows.Add(ids);
                    row++;
                }
                styles.Add(new RuneStyle(styleId, Str(s, "name"), Str(s, "icon"), rows));
            }
        }
        foreach (var (id, (name, icon)) in Shards)
            byId[id] = new RuneInfo(id, name, ShardStyle, ShardRows.Select((r, i) => (r, i)).FirstOrDefault(x => x.r.Contains(id)).i, icon, "");

        var labels = new Dictionary<int, IReadOnlyList<RuneStatLabel>>();
        if (perksJson is not null)
        {
            try
            {
                using var perks = JsonDocument.Parse(perksJson);
                foreach (var p in perks.RootElement.EnumerateArray())
                {
                    if (!p.TryGetProperty("id", out var idEl) || !p.TryGetProperty("endOfGameStatDescs", out var descs)) continue;
                    var list = descs.EnumerateArray().Select(d => d.GetString() ?? "").SelectMany(ParseStatDesc).ToList();
                    if (list.Count > 0) labels[idEl.GetInt32()] = list;
                }
            }
            catch (JsonException) { /* labels are optional */ }
        }
        return new RuneCatalog(styles, byId, labels);
    }

    private static readonly Regex Var = new(@"@eogvar(\d)@");

    /// <summary>"Total Damage Dealt: @eogvar1@" -> (Total Damage Dealt, 1, Damage). A description can hold several lines (&lt;br&gt;).</summary>
    internal static IEnumerable<RuneStatLabel> ParseStatDesc(string desc)
    {
        foreach (var line in Regex.Split(desc, "<br>", RegexOptions.IgnoreCase))
        {
            var vars = Var.Matches(line);
            if (vars.Count == 0) continue;
            string label = StripTags(line[..line.IndexOf('@')]).Trim().TrimEnd(':').Trim();
            if (label.Length == 0) continue;
            int v = int.Parse(vars[0].Groups[1].Value);
            string lower = label.ToLowerInvariant();
            string after = line[(vars[0].Index + vars[0].Length)..];
            static bool Has(string s, params string[] words) => words.Any(s.Contains);
            var kind = vars.Count > 1 && after.StartsWith(':') ? RuneStatKind.Time // "mm:ss" uptime or completion time
                : after.StartsWith('%') || lower.Contains("percent") ? RuneStatKind.Percent
                : Has(lower, "mana", "resource", "/healing", "ad/ap") ? RuneStatKind.Other
                : lower.Contains("gold") ? RuneStatKind.Gold
                : Has(lower, "shield", "blocked", "mitigated") ? RuneStatKind.Shield
                : Has(lower, "heal", "restored") ? RuneStatKind.Healing
                : lower.Contains("damage") && !lower.Contains("reduced") ? RuneStatKind.Damage
                : Has(lower, "seconds", "time spent", "duration", "souls", "stacks", "times", "activat", "attacks", "wards", "items",
                      "received", "swapped", "collected", "spawned", "spotted", "sensed", "number") ? RuneStatKind.Count
                : RuneStatKind.Other;
            yield return new RuneStatLabel(char.ToUpperInvariant(label[0]) + label[1..], v, kind);
        }
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
    private static string StripTags(string s) => Regex.Replace(s, "<[^>]+>", "");
}
