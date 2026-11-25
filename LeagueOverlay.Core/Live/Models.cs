namespace LeagueOverlay.Core.Live;

// Shapes of https://127.0.0.1:2999/liveclientdata/allgamedata (only the fields we use).
// Deserialized case-insensitively, so property names match the camelCase JSON keys.

public sealed class LiveGame
{
    public ActivePlayer? ActivePlayer { get; set; }
    public List<Player> AllPlayers { get; set; } = new();
    public GameData? GameData { get; set; }
    public EventList? Events { get; set; }

    /// <summary>"Win" / "Lose" once the game has ended (the client keeps answering on the end screen for a moment).</summary>
    public string? Result => Events?.Events.LastOrDefault(e => e.EventName == "GameEnd")?.Result;

    /// <summary>
    /// Fallback when the end screen was missed: to win, a team has to destroy both of the other team's nexus turrets
    /// before the nexus. "Win"/"Lose" if exactly one side has lost both, else null.
    /// </summary>
    public string? ResultFromNexusTurrets()
    {
        if (Me?.Team is not { } myTeam || Events is null) return null;
        int Down(string team) => Events.Events
            .Where(e => e.EventName == "TurretKilled" && e.TurretKilled is { } t && TurretTeam(t) == team && IsNexusTurret(t))
            .Select(e => e.TurretKilled).Distinct().Count();
        string enemy = myTeam == "ORDER" ? "CHAOS" : "ORDER";
        bool theirsDown = Down(enemy) >= 2, oursDown = Down(myTeam) >= 2;
        return theirsDown && !oursDown ? "Win" : oursDown && !theirsDown ? "Lose" : null;
    }

    // Turret names look like "Turret_T2_C_01_A": T1 = ORDER (blue side), T2 = CHAOS; C_01 and C_02 guard the nexus.
    private static string? TurretTeam(string name) => name.Contains("_T1_") ? "ORDER" : name.Contains("_T2_") ? "CHAOS" : null;
    private static bool IsNexusTurret(string name) => name.Contains("_C_01_") || name.Contains("_C_02_");

    /// <summary>The entry in AllPlayers that is the person playing on this PC.</summary>
    public Player? Me => ActivePlayer is null ? null : AllPlayers.FirstOrDefault(p => p.IsSameAs(ActivePlayer));
}

public sealed class ActivePlayer
{
    public string? RiotId { get; set; }
    public string? SummonerName { get; set; }
    public double CurrentGold { get; set; }
    public int Level { get; set; }
    public ChampionStats? ChampionStats { get; set; }
    public FullRunes? FullRunes { get; set; }
    public Abilities? Abilities { get; set; }
}

/// <summary>Your abilities' ranks (only R is used, for Cho'Gath's Feast).</summary>
public sealed class Abilities
{
    public Ability? R { get; set; }
}

public sealed class Ability
{
    public int AbilityLevel { get; set; }
}

/// <summary>Your champion's current stats as the game shows them (items, runes and stacks included).</summary>
public sealed class ChampionStats
{
    public double AbilityPower { get; set; }
    public double AttackDamage { get; set; }
    public double Armor { get; set; }
    public double AttackRange { get; set; }
    /// <summary>The client reports this as a fraction (0.25); see CritPercent.</summary>
    public double CritChance { get; set; }
    public double MaxHealth { get; set; }
    public double CritPercent => CritChance <= 1 ? CritChance * 100 : CritChance;
}

public sealed class FullRunes
{
    public List<RuneRef> StatRunes { get; set; } = new();
}

public sealed class RuneRef
{
    public int Id { get; set; }
}

public sealed class Player
{
    public string? ChampionName { get; set; }
    public string? RawChampionName { get; set; }
    public string? RiotId { get; set; }
    public string? RiotIdGameName { get; set; }
    public string? SummonerName { get; set; }
    public string? Team { get; set; }
    public string? Position { get; set; }
    public int Level { get; set; }
    public bool IsBot { get; set; }
    public bool IsDead { get; set; }
    public List<PlayerItem> Items { get; set; } = new();
    public Scores Scores { get; set; } = new();
    public SummonerSpells? SummonerSpells { get; set; }

    /// <summary>Data Dragon champion id, e.g. "game_character_displayname_MonkeyKing" -> "MonkeyKing".</summary>
    public string ChampionKey => (RawChampionName ?? "").Replace("game_character_displayname_", "");

    public bool HasSmite =>
        (SummonerSpells?.SummonerSpellOne?.RawDisplayName ?? "").Contains("Smite", StringComparison.OrdinalIgnoreCase) ||
        (SummonerSpells?.SummonerSpellTwo?.RawDisplayName ?? "").Contains("Smite", StringComparison.OrdinalIgnoreCase);

    public bool IsSameAs(ActivePlayer a) =>
        (!string.IsNullOrEmpty(RiotId) && RiotId == a.RiotId) ||
        (!string.IsNullOrEmpty(SummonerName) && SummonerName == a.SummonerName);
}

public sealed class PlayerItem
{
    public int ItemID { get; set; }
    public string? DisplayName { get; set; }
    public int Price { get; set; }
    public int Count { get; set; } = 1;
    public int Slot { get; set; }
}

public sealed class Scores
{
    public int Kills { get; set; }
    public int Deaths { get; set; }
    public int Assists { get; set; }
    public int CreepScore { get; set; }
    /// <summary>Vision score.</summary>
    public double WardScore { get; set; }
}

public sealed class SummonerSpells
{
    public Spell? SummonerSpellOne { get; set; }
    public Spell? SummonerSpellTwo { get; set; }
}

public sealed class Spell
{
    public string? DisplayName { get; set; }
    public string? RawDisplayName { get; set; }
}

public sealed class EventList
{
    public List<GameEvent> Events { get; set; } = new();
}

public sealed class GameEvent
{
    public string? EventName { get; set; }
    public double EventTime { get; set; }
    public string? Result { get; set; }
    /// <summary>For TurretKilled events, e.g. "Turret_T2_C_01_A".</summary>
    public string? TurretKilled { get; set; }
    /// <summary>For ChampionKill events: player names (game names), or a champion/minion/turret name for non-players.</summary>
    public string? KillerName { get; set; }
    public string? VictimName { get; set; }
    public List<string> Assisters { get; set; } = new();
}

public sealed class GameData
{
    public string? GameMode { get; set; }
    public double GameTime { get; set; }
    public string? MapName { get; set; }
    public int MapNumber { get; set; }
}
