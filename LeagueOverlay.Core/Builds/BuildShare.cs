using System.IO.Compression;
using System.Text.Json;

namespace LeagueOverlay.Core.Builds;

/// <summary>
/// Popular builds in a single file (*.lobuilds, gzip-compressed JSON) to give to friends, so their app doesn't have to
/// collect the same games again. Importing merges by match id: games already present are skipped, so importing the
/// same file twice changes nothing. The file holds only public match data (champions, roles, items, wins), nothing personal.
/// </summary>
public static class BuildShare
{
    public const string Extension = ".lobuilds";
    private const int Format = 1;

    private sealed record SharedStore(string Platform, string Patch, List<string> Matches, List<BuildRecord[]> Builds);
    private sealed record SharedFile(int Format, DateTime Exported, List<SharedStore> Stores);

    public sealed record Summary(int Games, IReadOnlyList<string> Patches)
    {
        public override string ToString() => Games == 0 ? "no games" : $"{Games:N0} games ({string.Join(", ", Patches)})";
    }

    /// <summary>Writes every saved region and patch into one file. Returns what went in.</summary>
    public static Summary Export(string root, string path)
    {
        var stores = new List<SharedStore>();
        if (Directory.Exists(root))
        {
            foreach (var dir in Directory.GetDirectories(root))
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                var store = BuildStore.Load(root, Path.GetFileName(dir), Path.GetFileNameWithoutExtension(file));
                var groups = store.ByMatch();
                if (groups.Count == 0) continue;
                stores.Add(new SharedStore(store.Platform, store.Patch, groups.Select(g => g.MatchId).ToList(), groups.Select(g => g.Builds).ToList()));
            }
        }
        using (var output = File.Create(path))
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal))
            JsonSerializer.Serialize(gzip, new SharedFile(Format, DateTime.UtcNow, stores));
        return new Summary(stores.Sum(s => s.Matches.Count), stores.Select(s => $"{s.Platform} {s.Patch}").ToList());
    }

    /// <summary>Merges a shared file into the saved builds. Returns the games that were new.</summary>
    public static Summary Import(string root, string path)
    {
        SharedFile? shared;
        using (var input = File.OpenRead(path))
        using (var gzip = new GZipStream(input, CompressionMode.Decompress))
            shared = JsonSerializer.Deserialize<SharedFile>(gzip);
        if (shared is null || shared.Format > Format) throw new InvalidDataException("This builds file is from a newer version of the app.");

        int added = 0;
        var patches = new List<string>();
        foreach (var s in shared.Stores)
        {
            if (s.Matches.Count != s.Builds.Count || !IsSafeName(s.Platform) || !IsSafeName(s.Patch)) continue;
            var store = BuildStore.Load(root, s.Platform, s.Patch);
            int before = store.MatchCount;
            for (int i = 0; i < s.Matches.Count; i++) store.Add(s.Matches[i], s.Builds[i]);
            int n = store.MatchCount - before;
            if (n == 0) continue;
            store.Save();
            added += n;
            patches.Add($"{s.Platform} {s.Patch}");
        }
        return new Summary(added, patches);
    }

    // Region and patch become folder and file names: only allow what Riot uses ("EUW1", "16.23").
    private static bool IsSafeName(string s) => s.Length is > 0 and < 16 && s.All(c => char.IsLetterOrDigit(c) || c == '.') && !s.Contains("..");
}
