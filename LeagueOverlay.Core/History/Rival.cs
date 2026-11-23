using System.Text.Json;

namespace LeagueOverlay.Core.History;

/// <summary>One stat in a duel. Null values mean "not known" (e.g. turret plates before match history has the game).</summary>
public sealed record DuelStat(string Label, double? Mine, double? Theirs, string Format, bool HigherIsBetter = true)
{
    /// <summary>+1 you win this stat, -1 they do, 0 tie or unknown.</summary>
    public int Winner => Mine is not { } m || Theirs is not { } t || Math.Abs(m - t) < 1e-9 ? 0
        : (m > t) == HigherIsBetter ? 1 : -1;

    public string Show(double? v) => v is { } x ? x.ToString(Format) : "—";
}

/// <summary>You against your rival in one game: who did better at what.</summary>
public sealed record RivalDuel(
    RecordedPlayer Me, RecordedPlayer Rival, bool SameTeam, List<DuelStat> Stats, bool HasMatchHistory)
{
    public int MyWins => Stats.Count(s => s.Winner > 0);
    public int RivalWins => Stats.Count(s => s.Winner < 0);

    /// <summary>The duel for a list of players (live or saved). Null if the rival isn't in this game.</summary>
    public static RivalDuel? Compare(IReadOnlyList<RecordedPlayer> players, string? rivalId, double minutes, PostGameStats? post = null)
    {
        if (players.FirstOrDefault(p => p.IsMe) is not { } me) return null;
        if (players.FirstOrDefault(p => !p.IsMe && RiotIds.Same(p.Key, rivalId)) is not { } rival) return null;
        minutes = Math.Max(1, minutes);

        var pm = post?.Find(me.Key);
        var pr = post?.Find(rival.Key);
        bool full = pm is not null && pr is not null;

        double TeamKills(RecordedPlayer p) => Math.Max(1, players.Where(x => x.MyTeam == p.MyTeam).Sum(x => x.Kills));
        double Kp(RecordedPlayer p, PostGamePlayer? pg) => pg is { KillParticipation: > 0 } ? pg.KillParticipation : (p.Kills + p.Assists) / TeamKills(p);
        int Kills(RecordedPlayer p, PostGamePlayer? pg) => pg?.Kills ?? p.Kills;
        int Deaths(RecordedPlayer p, PostGamePlayer? pg) => pg?.Deaths ?? p.Deaths;
        int Assists(RecordedPlayer p, PostGamePlayer? pg) => pg?.Assists ?? p.Assists;
        double Kda(RecordedPlayer p, PostGamePlayer? pg) => (Kills(p, pg) + Assists(p, pg)) / (double)Math.Max(1, Deaths(p, pg));
        int Cs(RecordedPlayer p, PostGamePlayer? pg) => pg?.Cs ?? p.CreepScore;
        double Vision(RecordedPlayer p, PostGamePlayer? pg) => pg?.VisionScore ?? p.VisionScore;

        var stats = new List<DuelStat>
        {
            new("Kills", Kills(me, pm), Kills(rival, pr), "0"),
            new("Deaths", Deaths(me, pm), Deaths(rival, pr), "0", HigherIsBetter: false),
            new("Assists", Assists(me, pm), Assists(rival, pr), "0"),
            new("KDA", Kda(me, pm), Kda(rival, pr), "0.0"),
            new("Kill participation", Kp(me, pm), Kp(rival, pr), "0%"),
            new("CS", Cs(me, pm), Cs(rival, pr), "0"),
            new("CS per minute", Cs(me, pm) / minutes, Cs(rival, pr) / minutes, "0.0"),
            new("Vision score", Vision(me, pm), Vision(rival, pr), "0"),
            full ? new("Gold earned", pm!.GoldEarned, pr!.GoldEarned, "#,0") : new("Item gold", me.ItemGold, rival.ItemGold, "#,0"),
            new("Damage to champions", pm?.ChampionDamage, pr?.ChampionDamage, "#,0"),
            new("Turret plates", pm?.TurretPlates, pr?.TurretPlates, "0"),
            new("Damage to buildings", pm?.BuildingDamage, pr?.BuildingDamage, "#,0"),
            new("Wards placed", pm?.WardsPlaced, pr?.WardsPlaced, "0"),
            new("Wards destroyed", pm?.WardsKilled, pr?.WardsKilled, "0"),
            new("Control wards bought", pm?.ControlWards, pr?.ControlWards, "0"),
            new("Solo kills", pm?.SoloKills, pr?.SoloKills, "0"),
            new("Damage taken", pm?.DamageTaken, pr?.DamageTaken, "#,0"),
            new("Healing & shielding allies", pm?.HealAndShield, pr?.HealAndShield, "#,0"),
            new("Crowd control (s)", pm?.CcSeconds, pr?.CcSeconds, "0"),
            new("CS at 10 min", pm?.CsAt(10) ?? pm?.CsAt10, pr?.CsAt(10) ?? pr?.CsAt10, "0"),
            new("Gold at 15 min", pm?.GoldAt(15), pr?.GoldAt(15), "#,0"),
            new("Objective takedowns", pm?.ObjectiveTakedowns, pr?.ObjectiveTakedowns, "0"),
            new("Skillshots hit", pm?.SkillshotsHit, pr?.SkillshotsHit, "0"),
            new("Skillshots dodged", pm?.SkillshotsDodged, pr?.SkillshotsDodged, "0"),
            new("Largest killing spree", pm?.LargestKillingSpree, pr?.LargestKillingSpree, "0"),
            new("Time spent dead", pm?.TimeDeadSeconds, pr?.TimeDeadSeconds, "0's'", HigherIsBetter: false),
            new("Level", pm?.Level ?? me.Level, pr?.Level ?? rival.Level, "0"),
        };
        // Leave out rows that would only say "—" (no match history yet, or a game saved before the timeline was fetched).
        stats.RemoveAll(s => s.Mine is null && s.Theirs is null);
        return new RivalDuel(me, rival, me.MyTeam == rival.MyTeam, stats, full);
    }
}

/// <summary>One finished game with the rival, kept for the long-term tally (separately from the 10 saved games).</summary>
public sealed record RivalryEntry(DateTime EndedAt, string RivalId, string MyChampion, string RivalChampion, string Result, int MyWins, int RivalWins);

/// <summary>Head-to-head record against rivals, in %LOCALAPPDATA%\LeagueOverlay\rivalry.json.</summary>
public sealed class RivalryLog(string path)
{
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueOverlay", "rivalry.json");

    public List<RivalryEntry> Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<RivalryEntry>>(File.ReadAllText(path)) ?? [] : []; }
        catch (Exception e) when (e is JsonException or IOException) { return []; }
    }

    /// <summary>Adds or replaces the entry for this game (it's updated again once match history adds more stats).</summary>
    public void Record(GameRecord game, RivalDuel duel)
    {
        var entries = Load();
        entries.RemoveAll(e => e.EndedAt == game.EndedAt);
        entries.Add(new RivalryEntry(game.EndedAt, duel.Rival.Key, game.ChampionName, duel.Rival.Name, game.Result, duel.MyWins, duel.RivalWins));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(entries.OrderBy(e => e.EndedAt)));
    }

    /// <summary>Games together, games where you won more stats, games where they did.</summary>
    public (int Games, int MyGames, int TheirGames) Tally(string? rivalId)
    {
        var mine = Load().Where(e => RiotIds.Same(e.RivalId, rivalId)).ToList();
        return (mine.Count, mine.Count(e => e.MyWins > e.RivalWins), mine.Count(e => e.RivalWins > e.MyWins));
    }
}
