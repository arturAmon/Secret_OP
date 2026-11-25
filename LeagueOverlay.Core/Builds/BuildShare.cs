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

    /// <summary>Largest builds file accepted (compressed) and how big it may get once decompressed.</summary>
    public const long MaxFileBytes = 20L * 1024 * 1024, MaxJsonBytes = 200L * 1024 * 1024;

    private static readonly System.Text.RegularExpressions.Regex MatchIdPattern = new(@"^[A-Z0-9]{2,6}_\d{5,13}$");

    /// <summary>
    /// Merges a shared file into the saved builds. Returns the games that were new. Files can come from friends or the
    /// internet, so: size limits (also once decompressed), match ids must look like Riot's ("EUW1_7234567890"), and
    /// optionally only some regions / patches (<paramref name="accept"/>) and only builds that make sense with this
    /// patch's data (<paramref name="validate"/>: known champion and items). A match with an invalid build is skipped whole.
    /// </summary>
    public static Summary Import(string root, string path, Func<string, string, bool>? accept = null, Func<BuildRecord, bool>? validate = null)
    {
        SharedFile? shared;
        using (var input = File.OpenRead(path))
        {
            if (input.Length > MaxFileBytes) throw new InvalidDataException($"The builds file is too big ({input.Length / 1024 / 1024} MB).");
            using var gzip = new GZipStream(input, CompressionMode.Decompress);
            using var limited = new LimitedStream(gzip, MaxJsonBytes);
            shared = JsonSerializer.Deserialize<SharedFile>(limited);
        }
        if (shared is null || shared.Format > Format) throw new InvalidDataException("This builds file is from a newer version of the app.");

        int added = 0;
        var patches = new List<string>();
        foreach (var s in shared.Stores ?? [])
        {
            if (s?.Matches is null || s.Builds is null || s.Matches.Count != s.Builds.Count || !IsSafeName(s.Platform) || !IsSafeName(s.Patch)) continue;
            if (accept is not null && !accept(s.Platform, s.Patch)) continue;
            var store = BuildStore.Load(root, s.Platform, s.Patch);
            int before = store.MatchCount;
            for (int i = 0; i < s.Matches.Count; i++)
            {
                var builds = s.Builds[i];
                if (s.Matches[i] is not { } id || !MatchIdPattern.IsMatch(id) || builds is not { Length: > 0 and <= 10 }) continue;
                if (builds.Any(b => b?.Items is null || b.Items.Length > 12 || (validate is not null && !validate(b)))) continue;
                store.Add(id, builds);
            }
            int n = store.MatchCount - before;
            if (n == 0) continue;
            store.Save();
            added += n;
            patches.Add($"{s.Platform} {s.Patch}");
        }
        return new Summary(added, patches);
    }

    /// <summary>Reads at most <c>limit</c> bytes from the inner stream, then fails (a tiny gzip file can expand enormously).</summary>
    private sealed class LimitedStream(Stream inner, long limit) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, count);
            _read += n;
            if (_read > limit) throw new InvalidDataException("The builds file is too big once unpacked.");
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // Region and patch become folder and file names: only allow what Riot uses ("EUW1", "16.23").
    private static bool IsSafeName(string s) => s.Length is > 0 and < 16 && s.All(c => char.IsLetterOrDigit(c) || c == '.') && !s.Contains("..");
}
