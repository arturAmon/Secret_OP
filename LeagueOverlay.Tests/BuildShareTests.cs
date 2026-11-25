using LeagueOverlay.Core.Builds;

namespace LeagueOverlay.Tests;

public class BuildShareTests
{
    private static BuildRecord[] Game(int champ) => Enumerable.Range(0, 10).Select(i => new BuildRecord(champ + i, "TOP", i % 2 == 0, [3031], 3006)).ToArray();

    [Fact]
    public void Export_then_import_merges_by_match_and_is_idempotent()
    {
        string mine = Temp(), friend = Temp(), file = Path.Combine(Path.GetTempPath(), $"lo_{Guid.NewGuid():N}.lobuilds");
        try
        {
            var a = BuildStore.Load(mine, "EUW1", "16.23");
            a.Add("EUW1_7000000001", Game(100)); a.Add("EUW1_7000000002", Game(200));
            a.Save();
            var b = BuildStore.Load(mine, "EUW1", "16.22");
            b.Add("EUW1_7000000000", Game(300));
            b.Save();

            var exported = BuildShare.Export(mine, file);
            Assert.Equal(3, exported.Games);

            var theirs = BuildStore.Load(friend, "EUW1", "16.23");
            theirs.Add("EUW1_7000000002", Game(200)); // they already have one of the games
            theirs.Save();

            var first = BuildShare.Import(friend, file);
            Assert.Equal(2, first.Games);                       // EUW1_1 and EUW1_0 were new
            var merged = BuildStore.Load(friend, "EUW1", "16.23");
            Assert.Equal(2, merged.MatchCount);
            Assert.Equal(20, merged.Builds.Count);
            Assert.Equal(1, BuildStore.Load(friend, "EUW1", "16.22").MatchCount);

            Assert.Equal(0, BuildShare.Import(friend, file).Games); // second time: nothing new
        }
        finally
        {
            foreach (var d in new[] { mine, friend }) if (Directory.Exists(d)) Directory.Delete(d, true);
            File.Delete(file);
        }
    }

    private static string Temp() => Path.Combine(Path.GetTempPath(), "lo_share_" + Guid.NewGuid().ToString("N"));
}
