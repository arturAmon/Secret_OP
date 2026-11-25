using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LeagueOverlay.Core.Data;

/// <summary>
/// What a new patch changed compared with the previous one, from Data Dragon: champions (numeric keys) whose stats,
/// abilities or passive differ, items whose cost, stats or description differ, and runes whose description differs.
/// New champions and items count as changed. Best effort: Data Dragon doesn't always carry every ability change, so a
/// champion can be changed in-game but look the same here.
/// </summary>
public sealed record PatchChanges(
    [property: JsonPropertyName("from")] string FromPatch,
    [property: JsonPropertyName("to")] string ToPatch,
    [property: JsonPropertyName("champions")] IReadOnlySet<int> Champions,
    [property: JsonPropertyName("items")] IReadOnlySet<int> Items,
    [property: JsonPropertyName("runes")] IReadOnlySet<int> Runes)
{
    private static readonly string[] SpellFields = ["cooldown", "cost", "range", "effectBurn", "datavalues", "tooltip", "description"];

    /// <summary>Compares two patches' championFull.json, item.json and runesReforged.json.</summary>
    public static PatchChanges Compare(string fromPatch, string toPatch,
        string oldChampionFull, string newChampionFull, string oldItems, string newItems, string? oldRunes, string? newRunes)
    {
        return new PatchChanges(fromPatch, toPatch,
            Changed(ChampionSigs(oldChampionFull), ChampionSigs(newChampionFull)),
            Changed(ItemSigs(oldItems), ItemSigs(newItems)),
            oldRunes is null || newRunes is null ? new HashSet<int>() : Changed(RuneSigs(oldRunes), RuneSigs(newRunes)));
    }

    private static HashSet<int> Changed(Dictionary<int, string> before, Dictionary<int, string> after) =>
        after.Where(x => !before.TryGetValue(x.Key, out var old) || old != x.Value).Select(x => x.Key).ToHashSet();

    // Each entry boiled down to a string of the parts that matter; equal strings = unchanged.
    private static Dictionary<int, string> ChampionSigs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, string>();
        foreach (var c in doc.RootElement.GetProperty("data").EnumerateObject())
        {
            if (!c.Value.TryGetProperty("key", out var k) || !int.TryParse(k.GetString(), out int key)) continue;
            var sig = new StringBuilder();
            if (c.Value.TryGetProperty("stats", out var stats)) sig.Append(stats.GetRawText());
            if (c.Value.TryGetProperty("spells", out var spells))
                foreach (var spell in spells.EnumerateArray())
                foreach (var field in SpellFields)
                    if (spell.TryGetProperty(field, out var v)) sig.Append('|').Append(v.GetRawText());
            if (c.Value.TryGetProperty("passive", out var passive) && passive.TryGetProperty("description", out var pd)) sig.Append('|').Append(pd.GetRawText());
            result[key] = sig.ToString();
        }
        return result;
    }

    private static Dictionary<int, string> ItemSigs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, string>();
        foreach (var i in doc.RootElement.GetProperty("data").EnumerateObject())
        {
            if (!int.TryParse(i.Name, out int id)) continue;
            var sig = new StringBuilder();
            if (i.Value.TryGetProperty("gold", out var gold) && gold.TryGetProperty("total", out var total)) sig.Append(total.GetRawText());
            if (i.Value.TryGetProperty("stats", out var stats)) sig.Append('|').Append(stats.GetRawText());
            if (i.Value.TryGetProperty("description", out var d)) sig.Append('|').Append(d.GetRawText());
            result[id] = sig.ToString();
        }
        return result;
    }

    private static Dictionary<int, string> RuneSigs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, string>();
        foreach (var tree in doc.RootElement.EnumerateArray())
        foreach (var slot in tree.GetProperty("slots").EnumerateArray())
        foreach (var rune in slot.GetProperty("runes").EnumerateArray())
            if (rune.TryGetProperty("id", out var id) && id.TryGetInt32(out int rid))
                result[rid] = rune.TryGetProperty("longDesc", out var d) ? d.GetRawText() : "";
        return result;
    }

    private static readonly JsonSerializerOptions Json = new();

    public string ToJson() => JsonSerializer.Serialize(new Cache(FromPatch, ToPatch, Champions.Order().ToArray(), Items.Order().ToArray(), Runes.Order().ToArray()), Json);

    public static PatchChanges? FromJson(string json)
    {
        try
        {
            var c = JsonSerializer.Deserialize<Cache>(json, Json);
            return c is null ? null : new PatchChanges(c.From, c.To, c.Champions.ToHashSet(), c.Items.ToHashSet(), c.Runes.ToHashSet());
        }
        catch (JsonException) { return null; }
    }

    private sealed record Cache(string From, string To, int[] Champions, int[] Items, int[] Runes);
}
