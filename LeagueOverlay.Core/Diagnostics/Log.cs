using System.Text;

namespace LeagueOverlay.Core.Diagnostics;

/// <summary>
/// A small daily log file for troubleshooting (%LOCALAPPDATA%\LeagueOverlay\logs\log-yyyyMMdd.txt). Thread-safe and never
/// throws: logging must not be the thing that breaks the app. Keeps two weeks of files and stops writing a day's file past
/// 5 MB. Never log settings or keys: the Riot API key travels in a header, not in URLs, so exception messages don't carry it.
/// </summary>
public static class Log
{
    public const int KeepDays = 14;
    public const long MaxBytesPerDay = 5 * 1024 * 1024;

    private static readonly object Lock = new();
    private static bool _cleaned;

    public static string Root { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LeagueOverlay", "logs");

    /// <summary>Today's log file.</summary>
    public static string CurrentFile => Path.Combine(Root, $"log-{DateTime.Now:yyyyMMdd}.txt");

    public static void Info(string message) => Write("INFO", message, null);
    public static void Warn(string message, Exception? e = null) => Write("WARN", message, e);
    public static void Error(string message, Exception? e = null) => Write("ERROR", message, e);

    // The same message within a minute is written once (a loop failing every 2 seconds shouldn't fill the file).
    private static readonly Dictionary<string, DateTime> Recent = new();

    private static void Write(string level, string message, Exception? e)
    {
        try
        {
            string key = $"{level}|{message}|{e?.GetType().Name}|{e?.Message}";
            lock (Lock)
            {
                var now = DateTime.UtcNow;
                if (Recent.TryGetValue(key, out var last) && now - last < TimeSpan.FromMinutes(1)) return;
                if (Recent.Count > 500) Recent.Clear();
                Recent[key] = now;
            }
            var line = new StringBuilder().Append(DateTime.Now.ToString("HH:mm:ss.fff")).Append(' ').Append(level).Append(' ').Append(message);
            if (e is not null) line.AppendLine().Append(e);
            line.AppendLine();
            System.Diagnostics.Debug.Write(line.ToString());
            lock (Lock)
            {
                Directory.CreateDirectory(Root);
                if (!_cleaned) { _cleaned = true; Clean(); }
                var file = CurrentFile;
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytesPerDay) return;
                File.AppendAllText(file, line.ToString());
            }
        }
        catch (Exception) { /* never let logging break the app */ }
    }

    /// <summary>Deletes log files older than <see cref="KeepDays"/>.</summary>
    private static void Clean()
    {
        foreach (var f in Directory.GetFiles(Root, "log-*.txt"))
            if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-KeepDays)) try { File.Delete(f); } catch (IOException) { }
    }
}
