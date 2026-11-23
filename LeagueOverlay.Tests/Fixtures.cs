using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Live;

namespace LeagueOverlay.Tests;

/// <summary>Small hand-made Data Dragon data in the real item.json / champion.json shape, plus a game builder.</summary>
public static class Fixtures
{
    private static string Item(int id, string name, int total, int baseCost, string stats, string tags = "",
        string from = "", string into = "", int depth = 1, string extra = "", string passive = "", string json = "", bool purchasable = true)
    {
        string list(string csv) => string.Join(",", csv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => $"\"{x.Trim()}\""));
        var desc = $"<mainText><stats>{stats}</stats><br><br>{(passive == "" ? "" : $"<passive>{passive}</passive><br>")}{extra}</mainText>";
        return $$"""
            "{{id}}": { "name": "{{name}}", "description": "{{desc}}",
              "gold": { "base": {{baseCost}}, "total": {{total}}, "purchasable": {{(purchasable ? "true" : "false")}} },
              "tags": [{{list(tags)}}], "from": [{{list(from)}}], "into": [{{list(into)}}], "depth": {{depth}},
              "maps": { "11": true } {{json}} }
            """;
    }

    private static string S(params (int v, string label)[] stats) =>
        string.Join("<br>", stats.Select(s => $"<attention>{s.v}</attention> {s.label}"));

    public static readonly string ItemJson = "{ \"data\": {" + string.Join(",\n", new[]
    {
        Item(1001, "Boots", 300, 300, S((25, "Move Speed")), "Boots", into: "3047,3111,3006,3020"),
        Item(1029, "Cloth Armor", 300, 300, S((15, "Armor")), "Armor", into: "3047,3075,3143,3157"),
        Item(1033, "Null-Magic Mantle", 400, 400, S((20, "Magic Resist")), "SpellBlock", into: "3111,3102,3156,3065"),
        Item(1036, "Long Sword", 350, 350, S((10, "Attack Damage")), "Damage", into: "3814"),
        Item(1037, "Pickaxe", 875, 875, S((25, "Attack Damage")), "Damage", into: "3031,3033,3071,3814,3053,3156"),
        Item(1038, "B.F. Sword", 1300, 1300, S((40, "Attack Damage")), "Damage", into: "3031"),
        Item(1018, "Cloak of Agility", 600, 600, "<attention>15%</attention> Critical Strike Chance", "CriticalStrike", into: "3031"),
        Item(1058, "Needlessly Large Rod", 1200, 1200, S((60, "Ability Power")), "SpellDamage", into: "3089,3165,3157,3102"),
        Item(1028, "Ruby Crystal", 400, 400, S((150, "Health")), "Health", into: "3075,3143,3065,3053"),
        Item(3916, "Oblivion Orb", 800, 800, S((25, "Ability Power")), "SpellDamage", into: "3165", passive: "Grievous Wounds", extra: "applies 40% Wounds"),
        Item(3123, "Executioner's Calling", 800, 800, S((15, "Attack Damage")), "Damage", into: "3033", passive: "Grievous Wounds", extra: "applies 40% Wounds"),
        Item(2003, "Health Potion", 50, 50, "", "Consumable"),
        Item(3340, "Stealth Ward", 0, 0, "", "Trinket,Vision"),
        Item(3363, "Farsight Alteration", 0, 0, "", "Trinket,Vision"),
        Item(3364, "Oracle Lens", 0, 0, "", "Trinket,Vision"),
        Item(3865, "World Atlas", 400, 400, S((30, "Health")), "Health,GoldPer,Vision,Lane"),
        Item(3866, "Runic Compass", 400, 0, S((30, "Health")), "Health,GoldPer,Vision,Lane", purchasable: false),
        Item(7000, "Ornn Masterwork", 3500, 3500, S((100, "Attack Damage")), "Damage", json: ", \"requiredAlly\": \"Ornn\""),
        // Removed from the shop (purchasable false), but a special-mode copy with a 6-digit id still claims map 11.
        Item(3193, "Gargoyle Stoneplate", 3150, 1350, S((60, "Armor"), (60, "Magic Resist")), "Armor,SpellBlock", depth: 3,
            json: ", \"inStore\": false", purchasable: false),
        Item(663193, "Gargoyle Stoneplate", 2500, 2500, S((55, "Armor"), (55, "Magic Resist")), "Armor,SpellBlock"),

        Item(3047, "Plated Steelcaps", 1200, 500, S((25, "Armor"), (45, "Move Speed")), "Boots,Armor", "1001,1029", depth: 2, passive: "Plating"),
        Item(3174, "Armored Advance", 1700, 500, S((35, "Armor"), (50, "Move Speed")), "Boots,Armor", "3047", depth: 3),
        Item(3111, "Mercury's Treads", 1250, 450, S((20, "Magic Resist"), (45, "Move Speed")), "Boots,SpellBlock", "1001,1033", depth: 2),
        Item(3006, "Berserker's Greaves", 1100, 800, "<attention>25%</attention> Attack Speed<br><attention>45</attention> Move Speed", "Boots,AttackSpeed", "1001", into: "3172", depth: 2),
        // Like the real data: the bot lane quest upgrade of Berserker's Greaves is not tagged as boots.
        Item(3172, "Gunmetal Greaves", 1100, 0, "<attention>40%</attention> Attack Speed", "AttackSpeed,LifeSteal,NonbootsMovement", "3006", depth: 3),
        Item(3020, "Sorcerer's Shoes", 1100, 800, S((12, "Magic Penetration"), (45, "Move Speed")), "Boots,MagicPenetration", "1001", depth: 2),

        Item(3031, "Infinity Edge", 3500, 725, S((75, "Attack Damage")) + "<br><attention>25%</attention> Critical Strike Chance", "Damage,CriticalStrike", "1038,1037,1018", depth: 2),
        Item(3033, "Mortal Reminder", 3000, 1325, S((35, "Attack Damage")) + "<br><attention>30%</attention> Armor Penetration<br><attention>25%</attention> Critical Strike Chance",
            "Damage,ArmorPenetration", "3123,1037", depth: 3, passive: "Grievous Wounds", extra: "applies 40% Wounds"),
        Item(3071, "Black Cleaver", 3000, 2125, S((40, "Attack Damage"), (400, "Health"), (20, "Ability Haste")), "Damage,Health", "1037", depth: 2, passive: "Carve"),
        Item(3814, "Edge of Night", 2900, 1675, S((50, "Attack Damage"), (15, "Lethality"), (250, "Health")), "Damage", "1037,1036", depth: 2, passive: "Annul"),
        Item(3053, "Sterak's Gage", 3200, 1925, S((50, "Attack Damage"), (400, "Health")), "Damage,Health", "1037,1028", depth: 2, passive: "Lifeline"),
        Item(3156, "Maw of Malmortius", 3100, 1825, S((60, "Attack Damage"), (40, "Magic Resist")), "Damage,SpellBlock", "1037,1033", depth: 2, passive: "Lifeline"),
        Item(3089, "Rabadon's Deathcap", 3600, 1200, S((130, "Ability Power")), "SpellDamage", "1058,1058", depth: 2),
        Item(3165, "Morellonomicon", 2850, 850, S((75, "Ability Power"), (350, "Health")), "SpellDamage,Health", "3916,1058", depth: 3, passive: "Grievous Wounds", extra: "applies 40% Wounds"),
        Item(3157, "Zhonya's Hourglass", 3250, 1750, S((105, "Ability Power"), (50, "Armor")), "SpellDamage,Armor", "1058,1029", depth: 2),
        Item(3102, "Banshee's Veil", 3000, 1400, S((105, "Ability Power"), (40, "Magic Resist")), "SpellDamage,SpellBlock", "1058,1033", depth: 2, passive: "Annul"),
        Item(3075, "Thornmail", 2450, 1750, S((75, "Armor"), (350, "Health")), "Armor,Health", "1029,1028", depth: 2, passive: "Thorns", extra: "attackers get 40% Wounds"),
        Item(3143, "Randuin's Omen", 2700, 2000, S((75, "Armor"), (350, "Health")), "Armor,Health", "1029,1028", depth: 2, passive: "Rock Solid", extra: "Reduces damage taken from Critical Strikes by 30%."),
        Item(3065, "Spirit Visage", 2700, 1900, S((400, "Health"), (50, "Magic Resist"), (10, "Ability Haste")), "Health,SpellBlock", "1028,1033", depth: 2),
    }) + "} }";

    public static readonly string ChampionJson = """
        { "data": {
          "Jinx":     { "key": "222", "name": "Jinx",     "tags": ["Marksman"],          "info": { "attack": 9, "defense": 2, "magic": 4 } },
          "Ahri":     { "key": "103", "name": "Ahri",     "tags": ["Mage","Assassin"],   "info": { "attack": 3, "defense": 4, "magic": 8 } },
          "Malphite": { "name": "Malphite", "tags": ["Tank","Fighter"],    "info": { "attack": 5, "defense": 9, "magic": 7 } },
          "Soraka":   { "name": "Soraka",   "tags": ["Support","Mage"],    "info": { "attack": 2, "defense": 5, "magic": 7 } },
          "Darius":   { "name": "Darius",   "tags": ["Fighter","Tank"],    "info": { "attack": 9, "defense": 5, "magic": 1 } },
          "Zed":      { "name": "Zed",      "tags": ["Assassin"],          "info": { "attack": 9, "defense": 2, "magic": 1 } },
          "Leona":    { "name": "Leona",    "tags": ["Tank","Support"],    "info": { "attack": 4, "defense": 8, "magic": 3 } },
          "Lux":      { "name": "Lux",      "tags": ["Mage","Support"],    "info": { "attack": 2, "defense": 4, "magic": 9 } },
          "Syndra":   { "name": "Syndra",   "tags": ["Mage"],              "info": { "attack": 2, "defense": 3, "magic": 9 } },
          "Annie":    { "name": "Annie",    "tags": ["Mage"],              "info": { "attack": 2, "defense": 3, "magic": 10 } },
          "Caitlyn":  { "key": "51", "name": "Caitlyn",  "tags": ["Marksman"],          "info": { "attack": 8, "defense": 2, "magic": 2 } },
          "MonkeyKing": { "name": "Wukong", "tags": ["Fighter","Tank"],    "info": { "attack": 8, "defense": 5, "magic": 2 } }
        } }
        """;

    public static StaticData Data { get; } = StaticData.Parse("test", ChampionJson, ItemJson);

    // ---- game builder ----

    public static Player P(string champ, string team, string position, int level, params int[] items) => new()
    {
        ChampionName = champ,
        RawChampionName = $"game_character_displayname_{champ}",
        RiotId = $"{champ}#{team}",
        SummonerName = $"{champ}#{team}",
        Team = team,
        Position = position,
        Level = level,
        Items = items.Select((id, slot) => new PlayerItem { ItemID = id, Count = 1, Slot = slot }).ToList(),
        SummonerSpells = new SummonerSpells
        {
            SummonerSpellOne = new Spell { RawDisplayName = "GeneratedTip_SummonerSpell_SummonerFlash_DisplayName" },
            SummonerSpellTwo = new Spell { RawDisplayName = position == "JUNGLE" ? "GeneratedTip_SummonerSpell_SummonerSmite_DisplayName" : "GeneratedTip_SummonerSpell_SummonerDot_DisplayName" },
        },
    };

    public static LiveGame Game(Player me, double gold, params Player[] others)
    {
        var all = new List<Player> { me };
        all.AddRange(others);
        return new LiveGame
        {
            ActivePlayer = new ActivePlayer { RiotId = me.RiotId, SummonerName = me.SummonerName, CurrentGold = gold, Level = me.Level },
            AllPlayers = all,
            GameData = new GameData { GameMode = "CLASSIC", GameTime = 900, MapNumber = 11 },
        };
    }
}
