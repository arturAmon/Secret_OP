using System.Text.Json;
using LeagueOverlay.Core.Analysis;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Core.History;

/// <summary>
/// Builds History entries straight from Riot's match history, for games played without the app running.
/// Everything comes from the match and its timeline; there were no live suggestions, so the build is listed without comparison,
/// and the gold graph uses gold earned per minute (the timeline has no item gold).
/// </summary>
public static class MatchImporter
{
    /// <summary>Remakes and very short games aren't worth a History slot.</summary>
    public const int MinSeconds = 5 * 60;

    public static async Task<string?> PuuidAsync(RiotClient client, string riotId, CancellationToken ct)
    {
        if (RiotIds.Split(riotId) is not { } id) return null;
        using var account = await client.AccountAsync(id.Name, id.Tag, ct);
        return account.RootElement.GetProperty("puuid").GetString();
    }

    /// <summary>
    /// Your most recent games that aren't in History yet (by match id, or a live-recorded game at the same time on the same champion).
    /// Newest first, at most <paramref name="count"/>.
    /// </summary>
    public static async Task<List<GameRecord>> ImportRecentAsync(
        RiotClient client, string myRiotId, StaticData data, IReadOnlyList<GameRecord> existing, int count, CancellationToken ct)
    {
        var result = new List<GameRecord>();
        if (await PuuidAsync(client, myRiotId, ct) is not { Length: > 0 } puuid) return result;

        List<string> ids;
        using (var doc = await client.RecentMatchIdsAsync(puuid, null, count, ct))
            ids = doc.RootElement.EnumerateArray().Select(x => x.GetString()!).ToList();

        var known = existing.Select(g => g.PostGame?.MatchId).OfType<string>().ToHashSet();
        foreach (var id in ids.Where(id => !known.Contains(id)))
        {
            using var match = await client.MatchAsync(id, ct);
            var info = match.RootElement.GetProperty("info");
            var ended = EndedAt(info);
            string? champion = FindMe(info, myRiotId)?.GetProperty("championName").GetString();
            // Recorded live and not enriched yet: leave it to the match-history lookup instead of adding it twice.
            if (existing.Any(g => Math.Abs((g.EndedAt - ended).TotalMinutes) < 4 && string.Equals(g.ChampionKey, champion, StringComparison.OrdinalIgnoreCase))) continue;

            JsonDocument? timeline = null;
            try { timeline = await client.TimelineAsync(id, ct); }
            catch (HttpRequestException) { } // import without the minute-by-minute part
            using (timeline)
            {
                if (FromMatch(match.RootElement, timeline?.RootElement, myRiotId, data) is { } record) result.Add(record);
            }
        }
        return result;
    }

    /// <summary>One History entry from a match (and its timeline, if available). Null if you're not in it or it isn't a regular 5v5 game.</summary>
    public static GameRecord? FromMatch(JsonElement match, JsonElement? timeline, string myRiotId, StaticData data)
    {
        var info = match.GetProperty("info");
        var participants = info.GetProperty("participants").EnumerateArray().ToList();
        double duration = info.GetProperty("gameDuration").GetDouble();
        if (participants.Count != 10 || duration < MinSeconds || FindMe(info, myRiotId) is not { } meJson) return null;

        var post = PostGameStats.Parse(match);
        if (timeline is { } tl) post = post.WithTimeline(match, tl);
        int myTeamId = meJson.GetProperty("teamId").GetInt32();
        string Champ(string key) => data.Champion(key)?.Name ?? key;
        static string Rid(JsonElement p) => $"{p.GetProperty("riotIdGameName").GetString()}#{p.GetProperty("riotIdTagline").GetString()}";
        static int I(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

        var ordered = participants.OrderBy(p => I(p, "teamId") != myTeamId).ThenBy(p => LaneOrder(p.GetProperty("teamPosition").GetString())).ToList();
        var players = ordered.Select(p => new RecordedPlayer(
            Rid(p), Champ(p.GetProperty("championName").GetString() ?? ""), p.GetProperty("championName").GetString() ?? "",
            p.GetProperty("teamPosition").GetString(), I(p, "teamId") == myTeamId, RiotIds.Same(Rid(p), myRiotId),
            I(p, "champLevel"), I(p, "kills"), I(p, "deaths"), I(p, "assists"), I(p, "totalMinionsKilled") + I(p, "neutralMinionsKilled"),
            I(p, "goldSpent"), Slots(p, data.Items), I(p, "visionScore"))).ToList();
        var me = players.First(p => p.IsMe);

        // Lanes at the end like the overlay compares them (gold + 350 per level), but with gold earned, to match the graph:
        // the timeline only has gold earned, and a winning team often ends with gold it hasn't spent.
        int Earned(RecordedPlayer p) => post.Find(p.Key)?.GoldEarned ?? p.ItemGold;
        var lanes = new List<RecordedLane>();
        foreach (var lane in Enum.GetValues<Lane>())
        {
            var mine = players.Where(p => p.MyTeam && LaneOf(p.Position) == lane).ToList();
            var theirs = players.Where(p => !p.MyTeam && LaneOf(p.Position) == lane).ToList();
            if (mine.Count == 0 || theirs.Count == 0) continue;
            int gold = mine.Sum(Earned) - theirs.Sum(Earned), level = mine.Sum(p => p.Level) - theirs.Sum(p => p.Level);
            lanes.Add(new RecordedLane(lane, gold, level, gold + LaneAnalyzer.DefaultLevelWeight * level));
        }

        var (snapshot, build) = Timeline(match, timeline, players, data);
        bool win = meJson.TryGetProperty("win", out var w) && w.GetBoolean();
        return new GameRecord(
            EndedAt(info), info.GetProperty("gameMode").GetString() ?? "", win ? "Win" : "Lose", duration,
            me.ChampionKey, me.Name, me.Position,
            players.Where(p => p.MyTeam).Sum(Earned), players.Where(p => !p.MyTeam).Sum(Earned),
            lanes, players, build, [], 0, snapshot, PostGame: post, Imported: true);
    }

    /// <summary>Gold earned per minute for the graph, plus purchases and level-ups from the timeline events.</summary>
    private static (HistorySnapshot, List<BuildStep>) Timeline(JsonElement match, JsonElement? timeline, List<RecordedPlayer> players, StaticData data)
    {
        var historyPlayers = players.Select(p => new HistoryPlayer(p.Key, p.Name, p.ChampionKey, p.MyTeam, p.IsMe)).ToList();
        if (timeline is not { } tl) return (new HistorySnapshot(historyPlayers, [], [], [], 0), []);

        var keyByParticipant = match.GetProperty("info").GetProperty("participants").EnumerateArray().ToDictionary(
            p => p.GetProperty("participantId").GetInt32(),
            p => $"{p.GetProperty("riotIdGameName").GetString()}#{p.GetProperty("riotIdTagline").GetString()}");
        var myTeamKeys = players.Where(p => p.MyTeam).Select(p => p.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string meKey = players.First(p => p.IsMe).Key;

        var gold = new List<GoldSample>();
        var items = new List<ItemEvent>();
        var levels = new List<LevelEvent>();
        foreach (var frame in tl.GetProperty("info").GetProperty("frames").EnumerateArray())
        {
            double t = frame.GetProperty("timestamp").GetInt64() / 1000.0;
            int mine = 0, theirs = 0;
            foreach (var pf in frame.GetProperty("participantFrames").EnumerateObject())
            {
                if (!int.TryParse(pf.Name, out var pid) || !keyByParticipant.TryGetValue(pid, out var key)) continue;
                int total = pf.Value.GetProperty("totalGold").GetInt32();
                if (myTeamKeys.Contains(key)) mine += total; else theirs += total;
            }
            gold.Add(new GoldSample(t, mine, theirs));

            foreach (var e in frame.GetProperty("events").EnumerateArray())
            {
                int pid = e.TryGetProperty("participantId", out var p) ? p.GetInt32() : 0;
                if (!keyByParticipant.TryGetValue(pid, out var who)) continue;
                double at = e.GetProperty("timestamp").GetInt64() / 1000.0;
                switch (e.GetProperty("type").GetString())
                {
                    case "ITEM_PURCHASED":
                        int id = e.GetProperty("itemId").GetInt32();
                        var info = data.Items.Get(id);
                        if (info?.IsConsumableOrTrinket == true) break;
                        items.Add(new ItemEvent(at, who, id, info?.Name ?? id.ToString(), data.Items.IsCompleted(new PlayerItem { ItemID = id })));
                        break;
                    case "ITEM_SOLD" when e.TryGetProperty("itemId", out var soldId):
                        var soldInfo = data.Items.Get(soldId.GetInt32());
                        if (soldInfo?.IsConsumableOrTrinket == true) break;
                        items.Add(new ItemEvent(at, who, soldId.GetInt32(), soldInfo?.Name ?? soldId.ToString(),
                            data.Items.IsCompleted(new PlayerItem { ItemID = soldId.GetInt32() }), Sold: true));
                        break;
                    case "ITEM_UNDO" when e.TryGetProperty("afterId", out var after) && after.GetInt32() > 0
                                          && e.TryGetProperty("beforeId", out var b0) && b0.GetInt32() == 0:
                        int soldIdx = items.FindLastIndex(x => x.PlayerKey == who && x.ItemId == after.GetInt32() && x.Sold);
                        if (soldIdx >= 0) items.RemoveAt(soldIdx); // a sale taken back
                        break;
                    case "ITEM_UNDO" when e.TryGetProperty("beforeId", out var before):
                        int idx = items.FindLastIndex(x => x.PlayerKey == who && x.ItemId == before.GetInt32() && !x.Sold);
                        if (idx >= 0) items.RemoveAt(idx);
                        break;
                    case "LEVEL_UP" when e.TryGetProperty("level", out var lv):
                        levels.Add(new LevelEvent(at, who, lv.GetInt32()));
                        break;
                }
            }
        }

        // Your finished items in order (tier-3 boots are automatic upgrades, not purchases).
        var build = items.Where(e => e.PlayerKey == meKey && e.Completed && !e.Sold)
            .Where(e => MatchParser.TierTwoBoots(e.ItemId, data.Items) is not { } tierTwo || tierTwo == e.ItemId)
            .Select(e => new BuildStep(e.Time, e.ItemId, e.Name, null, null, null, [], [], StepMatch.NoSuggestion))
            .ToList();
        return (new HistorySnapshot(historyPlayers, gold, items, levels, 0), build);
    }

    /// <summary>item0-item5, the trinket (item6) and, if it holds boots, the role-quest slot (roleBoundItem), in the stored layout.</summary>
    private static int[] Slots(JsonElement p, ItemCatalog items)
    {
        int I(string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        var slots = new int[ItemSlots.StoredLength];
        for (int i = 0; i <= ItemSlots.TrinketIndex; i++) slots[i] = I($"item{i}");
        int role = I("roleBoundItem");
        if (role > 0 && (items.Get(role)?.IsBoots == true || items.IsUpgradedBoots(new PlayerItem { ItemID = role }))) slots[ItemSlots.RoleIndex] = role;
        return slots;
    }

    private static JsonElement? FindMe(JsonElement info, string myRiotId) =>
        info.GetProperty("participants").EnumerateArray()
            .Where(p => RiotIds.Same($"{p.GetProperty("riotIdGameName").GetString()}#{p.GetProperty("riotIdTagline").GetString()}", myRiotId))
            .Select(p => (JsonElement?)p).FirstOrDefault();

    private static DateTime EndedAt(JsonElement info) =>
        info.TryGetProperty("gameEndTimestamp", out var end) && end.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds(end.GetInt64()).LocalDateTime
            : DateTimeOffset.FromUnixTimeMilliseconds(info.GetProperty("gameCreation").GetInt64()).LocalDateTime.AddSeconds(info.GetProperty("gameDuration").GetDouble());

    private static Lane? LaneOf(string? position) => position switch
    {
        "TOP" => Lane.Top, "JUNGLE" => Lane.Jungle, "MIDDLE" => Lane.Mid, "BOTTOM" or "UTILITY" => Lane.Bot, _ => null,
    };

    private static int LaneOrder(string? position) => position switch
    {
        "TOP" => 0, "JUNGLE" => 1, "MIDDLE" => 2, "BOTTOM" => 3, "UTILITY" => 4, _ => 5,
    };
}
