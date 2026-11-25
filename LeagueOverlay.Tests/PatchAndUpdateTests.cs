using System.IO.Compression;
using System.Text;
using LeagueOverlay.Core.Builds;
using LeagueOverlay.Core.Data;
using LeagueOverlay.Core.Diagnostics;
using LeagueOverlay.Core.Updates;

namespace LeagueOverlay.Tests;

public class PatchChangesTests
{
    private static string Champs(string ahriQ, int garenHp, bool withNew = false) =>
        "{\"data\":{" +
        "\"Ahri\":{\"key\":\"103\",\"stats\":{\"hp\":590},\"spells\":[{\"cooldown\":[7],\"tooltip\":\"" + ahriQ + "\"}],\"passive\":{\"description\":\"orbs\"}}," +
        "\"Garen\":{\"key\":\"86\",\"stats\":{\"hp\":" + garenHp + "},\"spells\":[{\"cooldown\":[8],\"tooltip\":\"spin\"}],\"passive\":{\"description\":\"regen\"}}," +
        "\"Jinx\":{\"key\":\"222\",\"stats\":{\"hp\":630},\"spells\":[],\"passive\":{\"description\":\"excited\"}}" +
        (withNew ? ",\"Zaahen\":{\"key\":\"904\",\"stats\":{\"hp\":600},\"spells\":[]}" : "") +
        "}}";

    private static string Items(int bootsCost, string ieText) =>
        "{\"data\":{\"1001\":{\"gold\":{\"total\":" + bootsCost + "},\"stats\":{},\"description\":\"boots\"}," +
        "\"3031\":{\"gold\":{\"total\":3450},\"stats\":{\"FlatPhysicalDamageMod\":65},\"description\":\"" + ieText + "\"}," +
        "\"3089\":{\"gold\":{\"total\":3600},\"stats\":{},\"description\":\"cap\"}}}";

    private static string Runes(string conqDesc) =>
        "[{\"id\":8000,\"slots\":[{\"runes\":[{\"id\":8010,\"longDesc\":\"" + conqDesc + "\"},{\"id\":8005,\"longDesc\":\"pta\"}]}]}]";

    [Fact]
    public void Compare_finds_changed_champions_items_and_runes()
    {
        var c = PatchChanges.Compare("16.22", "16.23",
            Champs("Q deals 40", 690), Champs("Q deals 50", 700, withNew: true),
            Items(300, "crit"), Items(350, "crit"),
            Runes("heal 8%"), Runes("heal 9%"));
        Assert.Equal(new[] { 86, 103, 904 }, c.Champions.Order());   // Ahri's Q, Garen's health, new Zaahen
        Assert.Equal(new[] { 1001 }, c.Items.Order());               // boots got pricier
        Assert.Equal(new[] { 8010 }, c.Runes.Order());

        var same = PatchChanges.Compare("16.22", "16.23", Champs("x", 1), Champs("x", 1), Items(1, "a"), Items(1, "a"), null, null);
        Assert.Empty(same.Champions);
        Assert.Empty(same.Items);
        Assert.Empty(same.Runes);

        var roundTrip = PatchChanges.FromJson(c.ToJson())!;
        Assert.Equal(c.Champions.Order(), roundTrip.Champions.Order());
        Assert.Equal("16.22", roundTrip.FromPatch);
    }

    [Fact]
    public void Library_uses_last_patch_only_for_what_did_not_change()
    {
        var root = Path.Combine(Path.GetTempPath(), "lo_changes_" + Guid.NewGuid().ToString("N"));
        try
        {
            var current = BuildStore.Load(root, "EUW1", "16.23");
            var previous = BuildStore.Load(root, "EUW1", "16.22");
            BuildRecord Rec(int champ, int item) => new(champ, "BOTTOM", true, [item, 3089], 3006);
            for (int i = 0; i < 6; i++) current.Add($"c{i}", [Rec(222, 3031), Rec(103, 3089)]);
            for (int i = 0; i < 10; i++) previous.Add($"p{i}", [Rec(222, 3031), Rec(103, 3089)]);
            for (int i = 0; i < 10; i++) previous.Add($"q{i}", [Rec(222, 6672)]);
            var library = new BuildLibrary(current, previous);

            Assert.Equal(26, library.For(222, "BOTTOM")!.Games);  // unknown changes: both patches
            library.Changes = new PatchChanges("16.22", "16.23", new HashSet<int> { 103 }, new HashSet<int> { 6672 }, new HashSet<int>());

            var jinx = library.For(222, "BOTTOM")!;              // unchanged champion: last patch too, minus games with the changed item
            Assert.Equal(16, jinx.Games);
            Assert.DoesNotContain(6672, jinx.ItemShare.Keys);
            Assert.Null(library.For(103, "BOTTOM"));             // changed champion: only 6 games this patch, not enough
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

public class BuildImportSafetyTests
{
    private static string Write(string json)
    {
        string path = Path.Combine(Path.GetTempPath(), $"lo_{Guid.NewGuid():N}.lobuilds");
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        gzip.Write(Encoding.UTF8.GetBytes(json));
        return path;
    }

    private static string Game(int champ) =>
        "[" + string.Join(",", Enumerable.Range(0, 10).Select(i => "{\"c\":" + (champ + i) + ",\"p\":\"TOP\",\"w\":true,\"i\":[3031],\"b\":3006}")) + "]";

    [Fact]
    public void Import_skips_odd_match_ids_old_patches_and_invalid_builds()
    {
        string root = Path.Combine(Path.GetTempPath(), "lo_safe_" + Guid.NewGuid().ToString("N"));
        string file = Write(
            "{\"Format\":1,\"Exported\":\"2026-11-25T00:00:00Z\",\"Stores\":[" +
            "{\"Platform\":\"EUW1\",\"Patch\":\"16.23\",\"Matches\":[\"EUW1_7000000001\",\"../../evil\",\"EUW1_7000000002\",\"EUW1_7000000003\"]," +
            "\"Builds\":[" + Game(100) + "," + Game(200) + "," + Game(9000) + "," + Game(300) + "]}," +
            "{\"Platform\":\"EUW1\",\"Patch\":\"15.1\",\"Matches\":[\"EUW1_6000000001\"],\"Builds\":[" + Game(100) + "]}," +
            "{\"Platform\":\"..\",\"Patch\":\"16.23\",\"Matches\":[\"EUW1_7000000009\"],\"Builds\":[" + Game(100) + "]}" +
            "]}");
        try
        {
            var summary = BuildShare.Import(root, file, accept: (_, patch) => patch is "16.23" or "16.22", validate: b => b.Champion < 1000);
            Assert.Equal(2, summary.Games);                  // 0001 and 0003: bad id, unknown champions (9000+) and old patch skipped
            Assert.Equal(2, BuildStore.Load(root, "EUW1", "16.23").MatchCount);
            Assert.False(File.Exists(Path.Combine(root, "EUW1", "15.1.json")));
        }
        finally { File.Delete(file); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void Import_rejects_files_that_unpack_to_something_huge()
    {
        string root = Path.Combine(Path.GetTempPath(), "lo_bomb_" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(Path.GetTempPath(), $"lo_{Guid.NewGuid():N}.lobuilds");
        using (var f = File.Create(file))
        using (var gzip = new GZipStream(f, CompressionLevel.SmallestSize))
        {
            gzip.Write("{\"Format\":1,\"Stores\":[],\"Pad\":\""u8);
            var spaces = new byte[1024 * 1024];
            Array.Fill(spaces, (byte)'a');
            for (int i = 0; i < 210; i++) gzip.Write(spaces); // 210 MB of padding, a few hundred KB compressed
            gzip.Write("\"}"u8);
        }
        try { Assert.Throws<InvalidDataException>(() => BuildShare.Import(root, file)); }
        finally { File.Delete(file); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}

public class UpdateTests
{
    private static string Release(string tag, bool prerelease = false, string? asset = "SecretOP-Setup-v1.15.0.exe",
        string url = "https://github.com/arturAmon/Secret_OP/releases/download/v1.15.0/SecretOP-Setup-v1.15.0.exe") =>
        "{\"tag_name\":\"" + tag + "\",\"html_url\":\"https://github.com/arturAmon/Secret_OP/releases/tag/" + tag + "\",\"draft\":false,\"prerelease\":" + (prerelease ? "true" : "false") + "," +
        "\"assets\":[{\"name\":\"SecretOP-v1.15.0.zip\",\"browser_download_url\":\"https://github.com/arturAmon/Secret_OP/releases/download/v1.15.0/SecretOP-v1.15.0.zip\",\"size\":5}" +
        (asset is null ? "" : ",{\"name\":\"" + asset + "\",\"browser_download_url\":\"" + url + "\",\"size\":1234,\"digest\":\"sha256:ABCDEF\"}") +
        "]}";

    [Fact]
    public void Parse_reports_only_newer_full_releases_with_their_installer()
    {
        var info = UpdateChecker.Parse(Release("v1.15.0"), new Version(1, 14, 0))!;
        Assert.Equal(new Version(1, 15, 0), info.Version);
        Assert.EndsWith("SecretOP-Setup-v1.15.0.exe", info.InstallerUrl);
        Assert.Equal(1234, info.InstallerSize);
        Assert.Equal("abcdef", info.Sha256);

        Assert.Null(UpdateChecker.Parse(Release("v1.15.0"), new Version(1, 15, 0, 0)));   // same version
        Assert.Null(UpdateChecker.Parse(Release("v1.13.1"), new Version(1, 14, 0)));      // older
        Assert.Null(UpdateChecker.Parse(Release("v1.16.0", prerelease: true), new Version(1, 14, 0)));
        Assert.Null(UpdateChecker.Parse(Release("v1.15.0", asset: null), new Version(1, 14, 0))!.InstallerUrl);
        // An installer link outside this repo's releases is never used.
        Assert.Null(UpdateChecker.Parse(Release("v1.15.0", url: "https://example.com/SecretOP-Setup-v1.15.0.exe"), new Version(1, 14, 0))!.InstallerUrl);
    }

    [Fact]
    public void Shared_builds_asset_is_read_from_the_builds_release()
    {
        var asset = SharedBuilds.Parse("""
            {"assets":[{"name":"PopularBuilds.lobuilds","browser_download_url":"https://github.com/arturAmon/Secret_OP/releases/download/builds/PopularBuilds.lobuilds",
                        "size":900000,"digest":"sha256:00FF","updated_at":"2026-11-25T10:00:00Z"}]}
            """)!;
        Assert.Equal(900000, asset.Size);
        Assert.Equal("00ff", asset.Sha256);
        Assert.Null(SharedBuilds.Parse("""{"assets":[{"name":"PopularBuilds.lobuilds","browser_download_url":"https://evil.example/PopularBuilds.lobuilds"}]}"""));
    }
}

public class LogTests
{
    [Fact]
    public void Log_writes_lines_and_skips_repeats()
    {
        var old = Log.Root;
        Log.Root = Path.Combine(Path.GetTempPath(), "lo_log_" + Guid.NewGuid().ToString("N"));
        try
        {
            Log.Info("hello");
            Log.Error("boom", new InvalidOperationException("bad thing"));
            Log.Error("boom", new InvalidOperationException("bad thing")); // same within a minute: written once
            var text = File.ReadAllText(Log.CurrentFile);
            Assert.Contains("INFO hello", text);
            Assert.Contains("bad thing", text);
            Assert.Equal(1, text.Split("ERROR boom").Length - 1);
        }
        finally { Directory.Delete(Log.Root, true); Log.Root = old; }
    }
}

public class MatchupChangesTests
{
    [Fact]
    public void Previous_patch_matchups_leave_out_changed_champions_only()
    {
        BuildRecord[] Match(int first) => Enumerable.Range(0, 10).Select(i => new BuildRecord(first + i, i % 5 == 0 ? "TOP" : "MIDDLE", i < 5, [3031], 0)).ToArray();
        var stats = Core.Builds.MatchupStats.From([Match(100)], [Match(100)], new HashSet<int> { 105 });
        Assert.Equal(2, stats.Role(100, "TOP").Games);     // unchanged: this patch and last
        Assert.Equal(1, stats.Role(105, "TOP").Games);     // changed: this patch only
        Assert.Equal(1, stats.Lane(100, "TOP", 105).Games); // against a changed champion: this patch only
    }
}
