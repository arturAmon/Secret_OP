namespace LeagueOverlay.Core.History;

/// <summary>You against the enemy in the same position (ADC vs ADC, support vs support, ...). Positive diffs = you're ahead.</summary>
public sealed record LaneMatchup(RecordedPlayer Me, RecordedPlayer Opponent, double Minutes)
{
    public double CsPerMin(RecordedPlayer p) => p.CreepScore / Math.Max(1, Minutes);
    public double CsPerMinDiff => CsPerMin(Me) - CsPerMin(Opponent);
    public double KdaDiff => Me.Kda - Opponent.Kda;
    public int GoldDiff => Me.ItemGold - Opponent.ItemGold;
    public int LevelDiff => Me.Level - Opponent.Level;
    public double VisionDiff => Me.VisionScore - Opponent.VisionScore;

    /// <summary>Null if your position is unknown or nobody on the enemy team has it (customs, ARAM).</summary>
    public static LaneMatchup? Find(IReadOnlyList<RecordedPlayer> players, double minutes)
    {
        if (players.FirstOrDefault(p => p.IsMe) is not { Position: { Length: > 0 } position } me) return null;
        var opponent = players.FirstOrDefault(p => !p.MyTeam && string.Equals(p.Position, position, StringComparison.OrdinalIgnoreCase));
        return opponent is null ? null : new LaneMatchup(me, opponent, minutes);
    }
}

/// <summary>A lighthearted title for something that went well.</summary>
public sealed record Award(string Title, string Reason, int Points);

/// <summary>
/// End-of-game titles. Every title celebrates something the player did well; there's always at least one,
/// and the score is just the titles' points added up.
/// </summary>
public static class Awards
{
    public const double LongEnoughForDeathlessMinutes = 15;

    public static List<Award> For(GameRecord g)
    {
        var list = new List<Award>();
        if (g.Me is not { } me) return list;
        var all = g.Players;
        double minutes = Math.Max(1, g.Duration / 60);
        bool won = g.Result == "Win";
        // Best (ties allowed), but not when everyone is level: that's nobody's title.
        bool Top(Func<RecordedPlayer, double> value) =>
            value(me) > 0 && all.All(p => value(p) <= value(me)) && all.Any(p => value(p) < value(me));

        // Best in the whole game.
        bool csTop = Top(p => p.CreepScore), visionTop = Top(p => p.VisionScore);
        if (Top(p => p.Kills)) list.Add(new("Champ Farmer", $"Most kills in the game ({me.Kills})", 100));
        if (visionTop) list.Add(new("Light in the Darkness", $"Best vision score in the game ({me.VisionScore:0})", 100));
        if (csTop) list.Add(new("CS Machine", $"Most farm in the game ({me.CreepScore} CS)", 100));
        if (Top(p => p.Assists)) list.Add(new("Wingman Supreme", $"Most assists in the game ({me.Assists})", 80));
        if (Top(p => p.ItemGold)) list.Add(new("Big Spender", $"Most gold spent on items in the game ({me.ItemGold / 1000.0:0.0}k)", 80));
        if (Top(p => p.Level)) list.Add(new("XP Sponge", $"Highest level in the game ({me.Level})", 50));
        if (me.Kda >= 2 && Top(p => p.Kda)) list.Add(new("KDA Connoisseur", $"Best KDA in the game ({me.Kda:0.0})", 80));

        if (me.Deaths == 0 && minutes >= LongEnoughForDeathlessMinutes)
            list.Add(new("Allergic to the Gray Screen", "Didn't die once", 120));
        else if (all.All(p => p.Deaths >= me.Deaths) && minutes >= LongEnoughForDeathlessMinutes)
            list.Add(new("Slippery Customer", $"Fewest deaths in the game ({me.Deaths})", 60));

        // Personal bests that don't need to beat everyone.
        double csPerMin = me.CreepScore / minutes, visionPerMin = me.VisionScore / minutes;
        if (!csTop && csPerMin >= 8) list.Add(new("Farm to Table", $"{csPerMin:0.0} CS per minute", 60));
        if (!visionTop && visionPerMin >= 1.5) list.Add(new("Ward Wizard", $"Vision score of {me.VisionScore:0} ({visionPerMin:0.0} per minute)", 50));

        // Against your lane opponent.
        if (LaneMatchup.Find(all, minutes) is { } lane)
        {
            if (lane.GoldDiff >= 1500)
                list.Add(new("Landlord of the Lane", $"Ended {lane.GoldDiff / 1000.0:0.0}k gold ahead of {lane.Opponent.Name}", 100));
            if (!csTop && lane.CsPerMinDiff >= 1)
                list.Add(new("Minion Whisperer", $"Out-farmed {lane.Opponent.Name} by {lane.CsPerMinDiff:0.0} CS per minute", 60));
        }

        // The team's story.
        int worstDeficit = g.Timeline.Gold.Count == 0 ? 0 : g.Timeline.Gold.Max(s => s.Enemy - s.MyTeam);
        int finalLead = g.MyTeamGold - g.EnemyTeamGold;
        if (won && worstDeficit >= 3000) list.Add(new("Comeback Kid", $"Won after being {worstDeficit / 1000.0:0.0}k gold behind", 150));
        else if (won && finalLead >= 10000) list.Add(new("Steamroller", $"Won with a {finalLead / 1000.0:0.0}k gold lead", 100));
        if (won) list.Add(new("Nexus Demolition Crew", "Won the game", 50));
        if (g.Suggested >= 3 && g.Followed == g.Suggested) list.Add(new("Good Listener", $"Followed all {g.Suggested} item suggestions", 40));

        // From Riot's match history, once it's been added to the game.
        if (g.PostGame?.Find(me.Key) is { } pm) list.AddRange(MatchHistoryTitles(pm, g.PostGame));

        if (list.Count == 0) list.Add(new("Brave Summoner", "Played it out to the very end", 30));
        return list.OrderByDescending(a => a.Points).ToList();
    }

    public static int Score(IEnumerable<Award> awards) => awards.Sum(a => a.Points);

    private static IEnumerable<Award> MatchHistoryTitles(PostGamePlayer me, PostGameStats post)
    {
        var all = post.Players;
        bool Top(Func<PostGamePlayer, double> value) =>
            value(me) > 0 && all.All(p => value(p) <= value(me)) && all.Any(p => value(p) < value(me));

        if (me.PentaKills > 0) yield return new("PENTAKILL!", "Took down the whole enemy team in one go", 300);
        else if (me.QuadraKills > 0) yield return new("Four of a Kind", "Got a quadra kill", 150);
        else if (me.TripleKills > 0) yield return new("Hat Trick", "Got a triple kill", 80);
        if (me.ObjectivesStolen > 0) yield return new("Smite Thief", $"Stole {Plural(me.ObjectivesStolen, "epic monster")} from the enemy", 150);
        if (Top(p => p.ChampionDamage)) yield return new("Damage Dealer Deluxe", $"Most damage to champions in the game ({me.ChampionDamage / 1000.0:0.0}k)", 100);
        if (Top(p => p.HealAndShield)) yield return new("Guardian Angel", $"Healed and shielded allies the most ({me.HealAndShield / 1000.0:0.0}k)", 100);
        if (Top(p => p.BuildingDamage)) yield return new("Demolition Expert", $"Most damage to buildings ({me.BuildingDamage / 1000.0:0.0}k)", 90);
        if (me.OutnumberedKills >= 2) yield return new("Outnumbered, Not Outplayed", $"{me.OutnumberedKills} kills while outnumbered", 90);
        if (me.LargestKillingSpree >= 8) yield return new("On a Roll", $"A killing spree of {me.LargestKillingSpree}", 90);
        if (Top(p => p.DamageTaken)) yield return new("Human Shield", $"Soaked up the most damage for the team ({me.DamageTaken / 1000.0:0.0}k)", 80);
        if (Top(p => p.CcSeconds)) yield return new("Crowd Control Freak", $"Most crowd control on enemies ({me.CcSeconds}s)", 80);
        if (me.DragonTakedowns >= 2 && Top(p => p.DragonTakedowns)) yield return new("Dragon Tamer", $"Took part in {me.DragonTakedowns} dragons", 80);
        if (me.SavedAllies > 0) yield return new("Clutch Saver", $"Saved {Plural(me.SavedAllies, "ally", "allies")} from certain death", 80);
        if (me.SkillshotsHit >= 20 && Top(p => p.SkillshotsHit)) yield return new("Sharpshooter", $"Landed the most skillshots ({me.SkillshotsHit})", 70);
        if (me.SkillshotsDodged >= 10 && Top(p => p.SkillshotsDodged)) yield return new("Matrix Mode", $"Dodged the most skillshots ({me.SkillshotsDodged})", 70);
        if (me.FirstBlood) yield return new("Early Bird", "Got first blood", 50);
        if (me.DancedWithHerald) yield return new("Dance Partner", "Danced with the Rift Herald", 40);
        if (me.Pings >= 30 && Top(p => p.Pings)) yield return new("Air Traffic Controller", $"Kept the team informed ({me.Pings} pings)", 20);
    }

    private static string Plural(int n, string one, string? many = null) => n == 1 ? $"1 {one}" : $"{n} {many ?? one + "s"}";
}
