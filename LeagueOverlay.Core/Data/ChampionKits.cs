using System.Text.Json;
using System.Text.RegularExpressions;

namespace LeagueOverlay.Core.Data;

/// <summary>
/// How a champion's abilities scale, from Meraki Analytics' champion data (every ability's ratios: "60% AP", "110% bonus AD",
/// "8% armor"...). Counts are over Q, W, E and R: how many of them scale with AP, with AD, with your own defensive stats.
/// Used by surprise mode so it only suggests styles the kit gets something out of (no AP Jinx: only her E scales with AP).
/// </summary>
/// <param name="AttackBased">The kit is built around basic attacks: on-hit effects, attack speed, empowered attacks, crit synergy.</param>
public sealed record ChampionKit(int Key, int ApAbilities, int AdAbilities, int DefenseAbilities, bool HealsAllies, bool AttackBased, bool Ranged);

public sealed class ChampionKits
{
    public static readonly ChampionKits Empty = new(new Dictionary<int, ChampionKit>());
    private readonly IReadOnlyDictionary<int, ChampionKit> _byKey;

    public ChampionKits(IReadOnlyDictionary<int, ChampionKit> byKey) => _byKey = byKey;

    public bool IsLoaded => _byKey.Count > 0;
    public int Count => _byKey.Count;
    public ChampionKit? For(int championKey) => _byKey.TryGetValue(championKey, out var k) ? k : null;
    public IEnumerable<ChampionKit> All => _byKey.Values;

    private static readonly Regex Ap = new(@"\bAP\b"), Ad = new(@"\bAD\b");
    // Your own defensive stats; "target's maximum health" is damage, not scaling with your tankiness.
    private static readonly Regex Defense = new(@"armor|magic resist|bonus health|maximum health|max health", RegexOptions.IgnoreCase);
    private static readonly Regex Target = new(@"target|enemy|enemies", RegexOptions.IgnoreCase);
    private static readonly Regex HealOrShield = new(@"\b(heal|heals|healing|shield|shields)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Ally = new(@"\b(ally|allies|allied|teammate|teammates)\b", RegexOptions.IgnoreCase);
    private static readonly Regex Attacks = new(@"on-hit|basic attacks?|attack speed|critical strike", RegexOptions.IgnoreCase);

    /// <summary>Reads Meraki's champions.json (keyed by champion name, each with "id", "attackType" and "abilities").</summary>
    public static ChampionKits ParseMeraki(Stream json)
    {
        using var doc = JsonDocument.Parse(json);
        var kits = new Dictionary<int, ChampionKit>();
        foreach (var champ in doc.RootElement.EnumerateObject())
        {
            var c = champ.Value;
            if (!c.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.Number || !c.TryGetProperty("abilities", out var abilities)) continue;
            int ap = 0, ad = 0, def = 0, attackMentions = 0;
            bool heals = false;
            foreach (var slot in abilities.EnumerateObject())
            {
                bool scoring = slot.Name is "Q" or "W" or "E" or "R";
                bool sAp = false, sAd = false, sDef = false;
                foreach (var ability in slot.Value.EnumerateArray())
                {
                    if (!ability.TryGetProperty("effects", out var effects)) continue;
                    foreach (var effect in effects.EnumerateArray())
                    {
                        string desc = effect.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
                        if (HealOrShield.IsMatch(desc) && Ally.IsMatch(desc)) heals = true;
                        if (Attacks.IsMatch(desc)) attackMentions++;
                        if (!effect.TryGetProperty("leveling", out var leveling)) continue;
                        foreach (var level in leveling.EnumerateArray())
                        {
                            if (!level.TryGetProperty("modifiers", out var modifiers)) continue;
                            foreach (var m in modifiers.EnumerateArray())
                            {
                                if (!m.TryGetProperty("units", out var units)) continue;
                                foreach (var u in units.EnumerateArray())
                                {
                                    string unit = u.GetString() ?? "";
                                    if (Ap.IsMatch(unit)) sAp = true;
                                    if (Ad.IsMatch(unit)) sAd = true;
                                    if (Defense.IsMatch(unit) && !Target.IsMatch(unit)) sDef = true;
                                }
                            }
                        }
                    }
                }
                if (!scoring) continue;
                if (sAp) ap++;
                if (sAd) ad++;
                if (sDef) def++;
            }
            bool ranged = c.TryGetProperty("attackType", out var at) && at.GetString() == "RANGED";
            int key = idEl.GetInt32();
            kits[key] = new ChampionKit(key, ap, ad, def, heals, attackMentions >= 3, ranged);
        }
        return new ChampionKits(kits);
    }

    /// <summary>Small cache form (the Meraki file is ~13 MB; this is a few KB).</summary>
    public string ToCacheJson() => JsonSerializer.Serialize(_byKey.Values.OrderBy(k => k.Key));

    public static ChampionKits FromCacheJson(string json) =>
        new((JsonSerializer.Deserialize<List<ChampionKit>>(json) ?? []).ToDictionary(k => k.Key));
}
