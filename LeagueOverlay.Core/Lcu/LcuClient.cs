using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using LeagueOverlay.Core.Builds;

namespace LeagueOverlay.Core.Lcu;

/// <summary>Where the League client's local API listens, from its lockfile ("LeagueClient:pid:port:password:https").</summary>
public sealed record LcuCredentials(int Port, string Password, string Protocol)
{
    public static LcuCredentials? Parse(string lockfile)
    {
        var parts = lockfile.Trim().Split(':');
        return parts.Length >= 5 && int.TryParse(parts[2], out int port) && port is > 0 and < 65536 && parts[3].Length > 0
            ? new LcuCredentials(port, parts[3], parts[4] is "http" ? "http" : "https")
            : null;
    }

    public Uri BaseUri => new($"{Protocol}://127.0.0.1:{Port}/");
}

/// <summary>Champion select as far as the overlay cares: your champion (hovered or locked in) and assigned role.</summary>
public sealed record ChampSelectState(int ChampionId, bool Locked, string? Position);

/// <summary>
/// The League client's local API (the "LCU": the launcher / lobby, not the game). Unofficial, so everything here
/// fails quietly: no client, a changed endpoint or bad JSON just means no champion select info.
/// Only ever talks to 127.0.0.1 on the port from the lockfile, and only trusts that port's self-signed certificate.
/// </summary>
public sealed class LcuClient : IDisposable
{
    public const string PageName = "League Overlay";

    private HttpClient? _http;
    private LcuCredentials? _creds;

    /// <summary>The lockfile of the running client: LEAGUE_LCU_LOCKFILE, else next to LeagueClientUx.exe, else the default install folder.</summary>
    public static string? FindLockfile()
    {
        if (Environment.GetEnvironmentVariable("LEAGUE_LCU_LOCKFILE") is { Length: > 0 } env) return File.Exists(env) ? env : null;
        foreach (var p in Process.GetProcessesByName("LeagueClientUx"))
        {
            try
            {
                if (p.MainModule?.FileName is { } exe && Path.Combine(Path.GetDirectoryName(exe)!, "lockfile") is var lf && File.Exists(lf)) return lf;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or UnauthorizedAccessException) { }
            finally { p.Dispose(); }
        }
        const string fallback = @"C:\Riot Games\League of Legends\lockfile";
        return File.Exists(fallback) && Process.GetProcessesByName("LeagueClientUx").Length > 0 ? fallback : null;
    }

    private string? _lockfile;

    private LcuCredentials? ReadCredentials()
    {
        // The lockfile disappears when the client closes, so a cached path stays valid exactly while it exists.
        if (_lockfile is null || !File.Exists(_lockfile)) _lockfile = FindLockfile();
        if (_lockfile is not { } path) return null;
        try
        {
            // The client keeps the file open; share it.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            return LcuCredentials.Parse(reader.ReadToEnd());
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>An HttpClient for the client currently running (a restarted client gets a new port and password).</summary>
    private HttpClient? Connect()
    {
        var creds = ReadCredentials();
        if (creds is null) { Reset(); return null; }
        if (creds == _creds && _http is not null) return _http;
        Reset();
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(1),
            SslOptions = new SslClientAuthenticationOptions
            {
                // The client's certificate is self-signed. This HttpClient only talks to 127.0.0.1 on the lockfile's port,
                // and the exception is limited to that host.
                RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                    errors == SslPolicyErrors.None || sender is SslStream { TargetHostName: "127.0.0.1" },
            },
        };
        var http = new HttpClient(handler) { BaseAddress = creds.BaseUri, Timeout = TimeSpan.FromSeconds(3) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes("riot:" + creds.Password)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http = http;
        _creds = creds;
        return http;
    }

    private void Reset()
    {
        _http?.Dispose();
        _http = null;
        _creds = null;
    }

    public bool IsConnected => _http is not null;

    /// <summary>Your champion and role while in champion select; null otherwise (or when the client isn't running).</summary>
    public async Task<ChampSelectState?> TryChampSelectAsync(CancellationToken ct = default)
    {
        if (Connect() is not { } http) return null;
        try
        {
            var phase = await http.GetFromJsonAsync<string>("lol-gameflow/v1/gameflow-phase", ct);
            if (phase != "ChampSelect") return null;
            using var response = await http.GetAsync("lol-champ-select/v1/session", ct);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return ParseSession(doc.RootElement);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or NotSupportedException
                                      || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            if (e is HttpRequestException { StatusCode: null }) Reset(); // client closed; find it again next time
            return null;
        }
    }

    /// <summary>Your cell in the session: the locked champion, or the hovered one before that.</summary>
    public static ChampSelectState? ParseSession(JsonElement session)
    {
        if (!session.TryGetProperty("localPlayerCellId", out var cellEl) || !session.TryGetProperty("myTeam", out var team)) return null;
        long cell = cellEl.GetInt64();
        foreach (var p in team.EnumerateArray())
        {
            if (!p.TryGetProperty("cellId", out var c) || c.GetInt64() != cell) continue;
            int locked = p.TryGetProperty("championId", out var ch) ? ch.GetInt32() : 0;
            int hover = p.TryGetProperty("championPickIntent", out var pi) ? pi.GetInt32() : 0;
            bool done = locked > 0 && IsPickDone(session, cell);
            string? position = p.TryGetProperty("assignedPosition", out var pos) && pos.GetString() is { Length: > 0 } s ? s : null;
            int champion = locked > 0 ? locked : hover;
            return new ChampSelectState(champion, done, position);
        }
        return null;
    }

    /// <summary>Whether your pick action is completed (championId is also set while just hovering in some queues).</summary>
    private static bool IsPickDone(JsonElement session, long cell)
    {
        if (!session.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array) return true;
        bool any = false;
        foreach (var turn in actions.EnumerateArray())
        foreach (var a in turn.EnumerateArray())
        {
            if (!a.TryGetProperty("actorCellId", out var actor) || actor.GetInt64() != cell) continue;
            if (!a.TryGetProperty("type", out var type) || type.GetString() != "pick") continue;
            any = true;
            if (a.TryGetProperty("completed", out var done) && done.GetBoolean()) return true;
        }
        return !any; // no pick actions (e.g. custom game): championId means picked
    }

    /// <summary>
    /// Creates the rune page in the client and makes it current. Replaces the page this app made before; if the client has
    /// no free page, replaces the current page only when allowed. Returns a short message for the user.
    /// </summary>
    public async Task<string> ApplyPageAsync(RuneSetup page, bool replaceCurrent, CancellationToken ct = default)
    {
        if (Connect() is not { } http) return "League client not found. Is it running?";
        try
        {
            using (var pagesDoc = JsonDocument.Parse(await http.GetStringAsync("lol-perks/v1/pages", ct)))
            {
                foreach (var p in pagesDoc.RootElement.EnumerateArray())
                    if (p.TryGetProperty("name", out var n) && n.GetString() == PageName && Bool(p, "isDeletable"))
                        await http.DeleteAsync($"lol-perks/v1/pages/{p.GetProperty("id").GetInt64()}", ct);
            }
            var body = new
            {
                name = PageName,
                primaryStyleId = page.Primary,
                subStyleId = page.Secondary,
                selectedPerkIds = page.Perks.ToArray(),
                current = true,
            };
            using var created = await http.PostAsJsonAsync("lol-perks/v1/pages", body, ct);
            if (created.IsSuccessStatusCode) return "Rune page applied.";
            if (!replaceCurrent) return "No free rune page. Delete one in the client, or tick 'replace the current one' below.";

            using (var pagesDoc = JsonDocument.Parse(await http.GetStringAsync("lol-perks/v1/pages", ct)))
            {
                var current = pagesDoc.RootElement.EnumerateArray().FirstOrDefault(p => Bool(p, "current") && Bool(p, "isDeletable"));
                if (current.ValueKind != JsonValueKind.Object) return "No rune page the app is allowed to replace.";
                await http.DeleteAsync($"lol-perks/v1/pages/{current.GetProperty("id").GetInt64()}", ct);
            }
            using var retry = await http.PostAsJsonAsync("lol-perks/v1/pages", body, ct);
            return retry.IsSuccessStatusCode ? "Rune page applied (replaced your current page)." : $"The client refused the rune page ({(int)retry.StatusCode}).";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return "Couldn't reach the League client.";
        }
    }

    /// <summary>
    /// Puts an item set into the client: a custom page in the in-game shop for that champion, e.g. "League Overlay: Tank Ahri"
    /// with blocks "Start", "Build", "Boots". Replaces the set this app made before and leaves your own sets alone.
    /// </summary>
    public async Task<string> ApplyItemSetAsync(string title, int championId, IReadOnlyList<(string Name, IReadOnlyList<int> Items)> blocks, CancellationToken ct = default)
    {
        if (Connect() is not { } http) return "League client not found.";
        try
        {
            using var who = JsonDocument.Parse(await http.GetStringAsync("lol-summoner/v1/current-summoner", ct));
            long summonerId = who.RootElement.GetProperty("summonerId").GetInt64();
            string url = $"lol-item-sets/v1/item-sets/{summonerId}/sets";
            var root = System.Text.Json.Nodes.JsonNode.Parse(await http.GetStringAsync(url, ct)) as System.Text.Json.Nodes.JsonObject
                       ?? new System.Text.Json.Nodes.JsonObject();
            var sets = root["itemSets"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
            // Drop the sets this app made earlier.
            foreach (var old in sets.Where(x => x?["title"]?.GetValue<string>() is { } t && t.StartsWith(PageName)).ToList()) sets.Remove(old);
            var blockNodes = new System.Text.Json.Nodes.JsonArray();
            foreach (var (name, ids) in blocks.Where(b => b.Items.Count > 0))
            {
                var items = new System.Text.Json.Nodes.JsonArray();
                foreach (var g in ids.GroupBy(id => id)) items.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = g.Key.ToString(), ["count"] = g.Count() });
                blockNodes.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["type"] = name, ["items"] = items, ["hideIfSummonerSpell"] = "", ["showIfSummonerSpell"] = "",
                });
            }
            sets.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["title"] = $"{PageName}: {title}",
                ["type"] = "custom", ["map"] = "any", ["mode"] = "any", ["priority"] = false, ["sortrank"] = 0, ["startedFrom"] = "blank",
                ["uid"] = Guid.NewGuid().ToString(),
                ["associatedChampions"] = new System.Text.Json.Nodes.JsonArray(championId),
                ["associatedMaps"] = new System.Text.Json.Nodes.JsonArray(11),
                ["preferredItemSlots"] = new System.Text.Json.Nodes.JsonArray(),
                ["blocks"] = blockNodes,
            });
            root["itemSets"] = sets;
            using var content = new StringContent(root.ToJsonString(), Encoding.UTF8, "application/json");
            using var put = await http.PutAsync(url, content, ct);
            return put.IsSuccessStatusCode ? $"Item set \"{PageName}: {title}\" is in the in-game shop." : $"The client refused the item set ({(int)put.StatusCode}).";
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException
                                      || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return "Couldn't create the item set in the League client.";
        }
    }

    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public void Dispose() => Reset();
}

/// <summary>
/// What the overlay and app window show in champion select: the champion, the suggested rune page (popular, or the
/// surprise build's), where it comes from ("312 games · 53% won" / the surprise title) and the start of the item build.
/// </summary>
public sealed record ChampSelectView(
    string ChampionId, string ChampionName, string? Position, bool Locked, RuneSetup? Runes, string Source,
    LeagueOverlay.Core.Analysis.ValueList<int> Items, bool Surprise)
{
    /// <summary>Starting items (an entry per copy).</summary>
    public LeagueOverlay.Core.Analysis.ValueList<int> Starter { get; init; } = LeagueOverlay.Core.Analysis.ValueList<int>.Empty;
    /// <summary>The full item path for the shop item set (surprise path + boots, or the popular path).</summary>
    public LeagueOverlay.Core.Analysis.ValueList<int> FullPath { get; init; } = LeagueOverlay.Core.Analysis.ValueList<int>.Empty;
}
