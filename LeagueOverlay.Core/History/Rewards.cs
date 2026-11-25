using System.Text.Json;

namespace LeagueOverlay.Core.History;

public enum CosmeticKind { Background, Border, Accent, Badge }

/// <summary>Something that changes how your share image looks. The "default"/"none" ones are always yours.</summary>
public sealed record Cosmetic(string Id, CosmeticKind Kind, string Name);

/// <summary>The look of your share image: one cosmetic of each kind (ids from <see cref="Rewards.Cosmetics"/>).</summary>
public sealed record CardStyle(string Background = "default", string Border = "default", string Accent = "default", string Badge = "none")
{
    public static readonly CardStyle Default = new();
    public string Get(CosmeticKind kind) => kind switch
    {
        CosmeticKind.Background => Background, CosmeticKind.Border => Border, CosmeticKind.Accent => Accent, _ => Badge,
    };
    public CardStyle With(CosmeticKind kind, string id) => kind switch
    {
        CosmeticKind.Background => this with { Background = id }, CosmeticKind.Border => this with { Border = id },
        CosmeticKind.Accent => this with { Accent = id }, _ => this with { Badge = id },
    };
}

/// <summary>An achievement: reach <see cref="Target"/> on <see cref="Value"/> to unlock a cosmetic.</summary>
public sealed record Achievement(string Id, string Title, string HowTo, string Unlocks, Func<RewardProgress, int> Value, int Target)
{
    public bool IsDone(RewardProgress p) => Value(p) >= Target;
}

/// <summary>One account's surprise games, what they unlocked and the share image style picked. Saved per account.</summary>
public sealed class RewardProgress
{
    public int SurpriseGames { get; set; }
    public int SurpriseWins { get; set; }
    /// <summary>Surprise games per style (SurpriseTheme key: "ap", "tank", ...).</summary>
    public Dictionary<string, int> Themes { get; set; } = new();
    /// <summary>Games already counted (end time ticks), so a game never counts twice.</summary>
    public HashSet<long> Counted { get; set; } = new();
    /// <summary>Counted games that were won (a result can arrive later, from match history).</summary>
    public HashSet<long> Won { get; set; } = new();
    /// <summary>Achievement id -> when it was unlocked.</summary>
    public Dictionary<string, DateTime> Unlocked { get; set; } = new();
    public CardStyle Style { get; set; } = CardStyle.Default;
    /// <summary>The games saved before rewards existed have been counted.</summary>
    public bool Backfilled { get; set; }

    public int Theme(string key) => Themes.GetValueOrDefault(key);
}

/// <summary>
/// Surprise game counting and the achievements that unlock share image styles. A game counts as a surprise game when it
/// was played with a surprise build that was really built (at least <see cref="MinPlanItems"/> of its items) and lasted
/// at least <see cref="MinMinutes"/> minutes (no remakes).
/// </summary>
public static class Rewards
{
    public const int MinPlanItems = 2;
    public const double MinMinutes = 10;

    public static readonly IReadOnlyList<Cosmetic> Cosmetics =
    [
        new("default", CosmeticKind.Background, "Midnight"),
        new("holo", CosmeticKind.Background, "Holo stripes"),
        new("royal", CosmeticKind.Background, "Royal gold"),
        new("arcane", CosmeticKind.Background, "Arcane blue"),
        new("crimson", CosmeticKind.Background, "Crimson"),
        new("shadow", CosmeticKind.Background, "Shadow violet"),
        new("mint", CosmeticKind.Background, "Mint"),
        new("default", CosmeticKind.Border, "Gradient bar"),
        new("neon", CosmeticKind.Border, "Neon"),
        new("gold", CosmeticKind.Border, "Gold frame"),
        new("rivets", CosmeticKind.Border, "Rivets"),
        new("steel", CosmeticKind.Border, "Forged steel"),
        new("default", CosmeticKind.Accent, "Yellow"),
        new("pink", CosmeticKind.Accent, "Pink"),
        new("cyan", CosmeticKind.Accent, "Electric cyan"),
        new("violet", CosmeticKind.Accent, "Violet"),
        new("none", CosmeticKind.Badge, "No badge"),
        new("agent", CosmeticKind.Badge, "Agent of Chaos"),
        new("crown", CosmeticKind.Badge, "Crown"),
    ];

    public static readonly IReadOnlyList<Achievement> Achievements =
    [
        new("first", "First Surprise", "Play a surprise game", "accent:pink", p => p.SurpriseGames, 1),
        new("five", "Regular Chaos", "Play 5 surprise games", "border:neon", p => p.SurpriseGames, 5),
        new("ten", "Chaos Veteran", "Play 10 surprise games", "border:gold", p => p.SurpriseGames, 10),
        new("twentyfive", "Chaos Connoisseur", "Play 25 surprise games", "background:holo", p => p.SurpriseGames, 25),
        new("fifty", "Living Legend", "Play 50 surprise games", "badge:crown", p => p.SurpriseGames, 50),
        new("win", "Agent of Chaos", "Win a surprise game", "badge:agent", p => p.SurpriseWins, 1),
        new("wins10", "Chaos Reigns", "Win 10 surprise games", "background:royal", p => p.SurpriseWins, 10),
        new("ap", "Arcane Surprise", "Play a surprise AP build", "background:arcane", p => p.Theme("ap"), 1),
        new("tank", "Iron Wall", "Play a surprise tank build", "border:rivets", p => p.Theme("tank"), 1),
        new("crit", "Glass Cannon", "Play a surprise crit build", "background:crimson", p => p.Theme("crit"), 1),
        new("onhit", "Overclocked", "Play a surprise on-hit build", "accent:cyan", p => p.Theme("onhit"), 1),
        new("lethality", "Shadow Step", "Play a surprise lethality build", "background:shadow", p => p.Theme("lethality"), 1),
        new("bruiser", "Unbreakable", "Play a surprise bruiser build", "border:steel", p => p.Theme("bruiser"), 1),
        new("healer", "Field Medic", "Play a surprise healing build", "background:mint", p => p.Theme("healer"), 1),
        new("battlemage", "Spellblade", "Play a surprise battlemage build", "accent:violet", p => p.Theme("battlemage"), 1),
    ];

    private static string Kind(CosmeticKind k) => k.ToString().ToLowerInvariant();

    public static Cosmetic Cosmetic(CosmeticKind kind, string id) =>
        Cosmetics.FirstOrDefault(c => c.Kind == kind && c.Id == id) ?? Cosmetics.First(c => c.Kind == kind);

    /// <summary>The achievement that unlocks a cosmetic (null for the ones everyone has).</summary>
    public static Achievement? UnlockedBy(CosmeticKind kind, string id) => Achievements.FirstOrDefault(a => a.Unlocks == $"{Kind(kind)}:{id}");

    public static Cosmetic? Unlocks(Achievement a)
    {
        var parts = a.Unlocks.Split(':');
        return Cosmetics.FirstOrDefault(c => Kind(c.Kind) == parts[0] && c.Id == parts[1]);
    }

    public static bool IsUnlocked(RewardProgress p, CosmeticKind kind, string id) =>
        UnlockedBy(kind, id) is not { } a ? Cosmetics.Any(c => c.Kind == kind && c.Id == id) : p.Unlocked.ContainsKey(a.Id);

    /// <summary>The chosen style, with anything not (or no longer) unlocked put back to the default.</summary>
    public static CardStyle Effective(RewardProgress p)
    {
        var s = p.Style ?? CardStyle.Default;
        foreach (var kind in Enum.GetValues<CosmeticKind>())
            if (!IsUnlocked(p, kind, s.Get(kind))) s = s.With(kind, CardStyle.Default.Get(kind));
        return s;
    }

    /// <summary>Whether a game counts as a surprise game: a surprise build really built, in a game that wasn't a remake.</summary>
    public static bool Counts(GameRecord g)
    {
        if (g.Surprise is not { } plan || g.Duration < MinMinutes * 60) return false;
        var planned = plan.Path.Append(plan.Boots).Where(id => id > 0).ToHashSet();
        return g.Build.Select(b => b.ItemId).Distinct().Count(planned.Contains) >= MinPlanItems;
    }

    /// <summary>Counts a finished game (again when its result arrives later: a win then counts too). Returns the achievements it unlocked (empty if none, or if it doesn't count).</summary>
    public static IReadOnlyList<Achievement> Record(RewardProgress p, GameRecord g)
    {
        if (!Counts(g)) return [];
        bool isNew = p.Counted.Add(g.EndedAt.Ticks), newWin = g.Result == "Win" && p.Won.Add(g.EndedAt.Ticks);
        if (!isNew && !newWin) return [];
        if (isNew)
        {
            p.SurpriseGames++;
            p.Themes[g.Surprise!.Theme] = p.Theme(g.Surprise.Theme) + 1;
        }
        if (newWin) p.SurpriseWins++;
        var unlocked = new List<Achievement>();
        foreach (var a in Achievements)
            if (!p.Unlocked.ContainsKey(a.Id) && a.IsDone(p)) { p.Unlocked[a.Id] = g.EndedAt; unlocked.Add(a); }
        return unlocked;
    }

    /// <summary>Counts the games saved before rewards existed (once per account).</summary>
    public static IReadOnlyList<Achievement> Backfill(RewardProgress p, IEnumerable<GameRecord> games)
    {
        if (p.Backfilled) return [];
        p.Backfilled = true;
        return games.OrderBy(g => g.EndedAt).SelectMany(g => Record(p, g)).ToList();
    }

    /// <summary>The next game-count achievement not reached yet (for a progress bar), or null when all are done.</summary>
    public static Achievement? NextMilestone(RewardProgress p) =>
        Achievements.Where(a => a.Id is "first" or "five" or "ten" or "twentyfive" or "fifty").FirstOrDefault(a => !a.IsDone(p));
}

/// <summary>Reward progress files, one per account: %LOCALAPPDATA%\LeagueOverlay\rewards\{account}.json.</summary>
public sealed class RewardStore(string root)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueOverlay", "rewards");

    private string PathFor(string? account)
    {
        string name = account is null ? "default" : string.Concat(RiotIds.Normalize(account).Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        return Path.Combine(root, (name.Length == 0 ? "default" : name) + ".json");
    }

    public RewardProgress Load(string? account)
    {
        try
        {
            var path = PathFor(account);
            if (File.Exists(path) && JsonSerializer.Deserialize<RewardProgress>(File.ReadAllText(path), Json) is { } p) return p;
        }
        catch (Exception e) when (e is JsonException or IOException or NotSupportedException) { }
        return new RewardProgress();
    }

    public void Save(string? account, RewardProgress p)
    {
        Directory.CreateDirectory(root);
        var path = PathFor(account);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(p, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
