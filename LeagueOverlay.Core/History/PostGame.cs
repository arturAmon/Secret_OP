using System.Text.Json;
using LeagueOverlay.Core.Builds;

namespace LeagueOverlay.Core.History;

/// <summary>
/// One player's final numbers from Riot's match history (match-v5), available a few minutes after the game.
/// Fields after Level were added later (format 2) and default to 0 in games saved before that.
/// </summary>
public sealed record PostGamePlayer(
    string RiotId, string Champion, bool Win, int Kills, int Deaths, int Assists, double KillParticipation,
    int Cs, int GoldEarned, int ChampionDamage, int BuildingDamage, int TurretPlates, int VisionScore,
    int WardsPlaced, int WardsKilled, int ControlWards, int SoloKills, int LargestMultiKill, int Level,
    int TeamId = 0, string Position = "",
    // combat
    int PhysicalDamage = 0, int MagicDamage = 0, int TrueDamage = 0, double TeamDamageShare = 0, double DamagePerMinute = 0,
    int DamageTaken = 0, int DamageMitigated = 0, int HealsOnTeammates = 0, int ShieldsOnTeammates = 0, int CcSeconds = 0,
    int DoubleKills = 0, int TripleKills = 0, int QuadraKills = 0, int PentaKills = 0, int LargestKillingSpree = 0,
    int TimeDeadSeconds = 0, bool FirstBlood = false, int SavedAllies = 0, int OutnumberedKills = 0,
    int SkillshotsHit = 0, int SkillshotsDodged = 0,
    // economy & laning
    double GoldPerMinute = 0, int CsAt10 = 0, int MaxCsLead = 0,
    // objectives
    int DragonTakedowns = 0, int BaronTakedowns = 0, int HeraldTakedowns = 0, int VoidgrubKills = 0,
    int TurretTakedowns = 0, int ObjectivesStolen = 0,
    // for fun
    int Pings = 0, bool DancedWithHerald = false,
    // minute by minute, from the match timeline (index = minute)
    int[]? GoldByMinute = null, int[]? CsByMinute = null, int[]? XpByMinute = null,
    // final inventory in the stored slot layout (item0-item6, role-quest slot), and gold spent on items
    int[]? Slots = null, int GoldSpent = 0)
{
    public int Multikills => DoubleKills + TripleKills + QuadraKills + PentaKills;
    public int ObjectiveTakedowns => DragonTakedowns + BaronTakedowns + HeraldTakedowns + VoidgrubKills;
    public int HealAndShield => HealsOnTeammates + ShieldsOnTeammates;
    public int? GoldAt(int minute) => GoldByMinute is { } g && g.Length > minute ? g[minute] : null;
    public int? CsAt(int minute) => CsByMinute is { } c && c.Length > minute ? c[minute] : null;
}

public sealed record PostGameStats(string MatchId, List<PostGamePlayer> Players, int Format = 1)
{
    /// <summary>Bump when Parse learns new fields; saved games with an older format are fetched again.</summary>
    public const int CurrentFormat = 3; // 3: final inventory (Slots, GoldSpent)

    public PostGamePlayer? Find(string riotId) => Players.FirstOrDefault(p => RiotIds.Same(p.RiotId, riotId));

    /// <summary>The player in the same position on the other team.</summary>
    public PostGamePlayer? Opponent(PostGamePlayer p) =>
        p.Position.Length == 0 ? null : Players.FirstOrDefault(o => o.TeamId != p.TeamId && o.Position == p.Position);

    public IEnumerable<PostGamePlayer> Team(int teamId) => Players.Where(p => p.TeamId == teamId);

    /// <summary>Adds the minute-by-minute gold, CS and XP from the match timeline.</summary>
    public PostGameStats WithTimeline(JsonElement match, JsonElement timeline)
    {
        var idByParticipant = match.GetProperty("info").GetProperty("participants").EnumerateArray()
            .ToDictionary(p => p.GetProperty("participantId").GetInt32(), p => $"{Str(p, "riotIdGameName")}#{Str(p, "riotIdTagline")}");
        var gold = new Dictionary<string, List<int>>();
        var cs = new Dictionary<string, List<int>>();
        var xp = new Dictionary<string, List<int>>();
        foreach (var frame in timeline.GetProperty("info").GetProperty("frames").EnumerateArray())
        foreach (var pf in frame.GetProperty("participantFrames").EnumerateObject())
        {
            if (!int.TryParse(pf.Name, out var pid) || !idByParticipant.TryGetValue(pid, out var id)) continue;
            var v = pf.Value;
            (gold.TryGetValue(id, out var g) ? g : gold[id] = new()).Add(v.GetProperty("totalGold").GetInt32());
            (cs.TryGetValue(id, out var c) ? c : cs[id] = new()).Add(v.GetProperty("minionsKilled").GetInt32() + v.GetProperty("jungleMinionsKilled").GetInt32());
            (xp.TryGetValue(id, out var x) ? x : xp[id] = new()).Add(v.GetProperty("xp").GetInt32());
        }
        return this with
        {
            Players = Players.Select(p => p with
            {
                GoldByMinute = gold.GetValueOrDefault(p.RiotId)?.ToArray(),
                CsByMinute = cs.GetValueOrDefault(p.RiotId)?.ToArray(),
                XpByMinute = xp.GetValueOrDefault(p.RiotId)?.ToArray(),
            }).ToList(),
        };
    }

    public static PostGameStats Parse(JsonElement match)
    {
        var players = new List<PostGamePlayer>();
        foreach (var p in match.GetProperty("info").GetProperty("participants").EnumerateArray())
        {
            int I(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? (int)v.GetDouble() : 0;
            var ch = p.TryGetProperty("challenges", out var c) ? c : default;
            double C(string name) => ch.ValueKind == JsonValueKind.Object && ch.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            string riotId = $"{Str(p, "riotIdGameName")}#{Str(p, "riotIdTagline")}";
            bool B(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
            int pings = p.EnumerateObject().Where(x => x.Name.EndsWith("Pings") && x.Value.ValueKind == JsonValueKind.Number).Sum(x => x.Value.GetInt32());
            players.Add(new PostGamePlayer(
                riotId, Str(p, "championName"), p.TryGetProperty("win", out var w) && w.GetBoolean(),
                I("kills"), I("deaths"), I("assists"), C("killParticipation"),
                I("totalMinionsKilled") + I("neutralMinionsKilled"), I("goldEarned"), I("totalDamageDealtToChampions"),
                I("damageDealtToBuildings"), (int)C("turretPlatesTaken"), I("visionScore"),
                I("wardsPlaced"), I("wardsKilled"), I("visionWardsBoughtInGame"), (int)C("soloKills"), I("largestMultiKill"), I("champLevel"),
                TeamId: I("teamId"), Position: Str(p, "teamPosition"),
                PhysicalDamage: I("physicalDamageDealtToChampions"), MagicDamage: I("magicDamageDealtToChampions"), TrueDamage: I("trueDamageDealtToChampions"),
                TeamDamageShare: C("teamDamagePercentage"), DamagePerMinute: C("damagePerMinute"),
                DamageTaken: I("totalDamageTaken"), DamageMitigated: I("damageSelfMitigated"),
                HealsOnTeammates: I("totalHealsOnTeammates"), ShieldsOnTeammates: I("totalDamageShieldedOnTeammates"), CcSeconds: I("timeCCingOthers"),
                DoubleKills: I("doubleKills"), TripleKills: I("tripleKills"), QuadraKills: I("quadraKills"), PentaKills: I("pentaKills"),
                LargestKillingSpree: I("largestKillingSpree"), TimeDeadSeconds: I("totalTimeSpentDead"), FirstBlood: B("firstBloodKill"),
                SavedAllies: (int)C("saveAllyFromDeath"), OutnumberedKills: (int)C("outnumberedKills"),
                SkillshotsHit: (int)C("skillshotsHit"), SkillshotsDodged: (int)C("skillshotsDodged"),
                GoldPerMinute: C("goldPerMinute"), CsAt10: (int)(C("laneMinionsFirst10Minutes") + C("jungleCsBefore10Minutes")),
                MaxCsLead: (int)C("maxCsAdvantageOnLaneOpponent"),
                DragonTakedowns: (int)C("dragonTakedowns"), BaronTakedowns: (int)C("baronTakedowns"), HeraldTakedowns: (int)C("riftHeraldTakedowns"),
                VoidgrubKills: (int)C("voidMonsterKill"), TurretTakedowns: I("turretTakedowns"), ObjectivesStolen: I("objectivesStolen"),
                Pings: pings, DancedWithHerald: C("dancedWithRiftHerald") > 0,
                Slots: Enumerable.Range(0, 7).Select(i => I($"item{i}")).Append(I("roleBoundItem")).ToArray(), GoldSpent: I("goldSpent")));
        }
        return new PostGameStats(match.GetProperty("metadata").GetProperty("matchId").GetString() ?? "", players, CurrentFormat);
    }

    private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
}

public static class RiotIds
{
    /// <summary>Riot IDs compared the way Riot does: case-insensitive, spaces ignored.</summary>
    public static bool Same(string? a, string? b) => a is not null && b is not null && Normalize(a) == Normalize(b);

    public static string Normalize(string id) => string.Concat(id.Where(ch => !char.IsWhiteSpace(ch))).ToLowerInvariant();

    /// <summary>"Duo Friend#EUW" -> "Duo Friend".</summary>
    public static string GameName(string id) => Split(id)?.Name ?? id;

    public static (string Name, string Tag)? Split(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        int hash = id.LastIndexOf('#');
        return hash > 0 && hash < id.Length - 1 ? (id[..hash].Trim(), id[(hash + 1)..].Trim()) : null;
    }
}

/// <summary>Finds a saved game in Riot's match history and adds the final numbers (and the definite result) to it.</summary>
public static class PostGameFetcher
{
    /// <summary>Match history usually has the game a minute or two after it ends; give up after this long.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(30);

    /// <summary>One attempt. Returns the enriched record, or null if the match isn't in the history (yet).</summary>
    public static async Task<GameRecord?> TryAsync(GameRecord record, RiotClient client, CancellationToken ct)
    {
        if (record.Me is not { } me || RiotIds.Split(me.Key) is not { } id) return null;
        string puuid;
        using (var account = await client.AccountAsync(id.Name, id.Tag, ct))
            puuid = account.RootElement.GetProperty("puuid").GetString() ?? "";
        if (puuid.Length == 0) return null;

        long since = new DateTimeOffset(record.EndedAt).AddSeconds(-record.Duration).AddMinutes(-15).ToUnixTimeSeconds();
        List<string> ids;
        using (var doc = await client.RecentMatchIdsAsync(puuid, since, 5, ct))
            ids = doc.RootElement.EnumerateArray().Select(x => x.GetString()!).ToList();

        foreach (var matchId in ids)
        {
            using var match = await client.MatchAsync(matchId, ct);
            if (!IsSameGame(match.RootElement, record)) continue;
            var stats = PostGameStats.Parse(match.RootElement);
            try
            {
                using var timeline = await client.TimelineAsync(matchId, ct);
                stats = stats.WithTimeline(match.RootElement, timeline.RootElement);
            }
            catch (HttpRequestException) { } // the timeline is a bonus; keep the rest without it
            var result = stats.Find(me.Key)?.Win is { } win ? (win ? "Win" : "Lose") : record.Result;
            // A saved inventory that's empty (the client blanked it at the very end) is filled from Riot's final numbers.
            var players = record.Players.Select(p => p.Items.All(i => i == 0) && stats.Find(p.Key) is { Slots: { } slots } pg
                ? p with { Items = slots, ItemGold = pg.GoldSpent } : p).ToList();
            return record with { PostGame = stats, Result = result, ResultInferred = false, Players = players };
        }
        return null;
    }

    /// <summary>Same champion for you, and about the same length (the live clock and match history differ by a few seconds).</summary>
    public static bool IsSameGame(JsonElement match, GameRecord record)
    {
        var info = match.GetProperty("info");
        if (Math.Abs(info.GetProperty("gameDuration").GetDouble() - record.Duration) > 120) return false;
        return info.GetProperty("participants").EnumerateArray().Any(p =>
            RiotIds.Same($"{p.GetProperty("riotIdGameName").GetString()}#{p.GetProperty("riotIdTagline").GetString()}", record.Me?.Key) &&
            string.Equals(p.GetProperty("championName").GetString(), record.ChampionKey, StringComparison.OrdinalIgnoreCase));
    }
}
