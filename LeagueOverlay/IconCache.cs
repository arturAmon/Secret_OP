using System.IO;
using System.Net.Http;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LeagueOverlay.Core.Data;

namespace LeagueOverlay;

/// <summary>
/// Item and champion icons from Data Dragon, downloaded once per patch into the same on-disk cache as item.json.
/// Get() never blocks: it returns null the first time and raises IconLoaded once the picture is ready.
/// </summary>
public sealed class IconCache
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Dictionary<string, ImageSource?> _loaded = new(); // null = doesn't exist (e.g. item unknown to Data Dragon)
    private readonly HashSet<string> _pending = new();
    private string? _version;

    public event Action? IconLoaded;

    public void SetVersion(string version)
    {
        if (version == _version) return;
        _version = version;
        _loaded.Clear();
    }

    public ImageSource? Item(int id) => Get($"item/{id}.png");
    public ImageSource? Champion(string championKey) => Get($"champion/{championKey}.png");

    private ImageSource? Get(string relative)
    {
        if (_version is not { } version) return null;
        if (_loaded.TryGetValue(relative, out var image)) return image;
        if (_pending.Add(relative)) _ = LoadAsync(version, relative);
        return null;
    }

    private async Task LoadAsync(string version, string relative)
    {
        ImageSource? image = null;
        try
        {
            image = await Task.Run(async () =>
            {
                string path = Path.Combine(DataDragon.DefaultCacheRoot, version, "img", relative.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(path))
                {
                    using var response = await Http.GetAsync($"https://ddragon.leagueoflegends.com/cdn/{version}/img/{relative}");
                    if (!response.IsSuccessStatusCode) return null;
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await File.WriteAllBytesAsync(path, await response.Content.ReadAsByteArrayAsync());
                }
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // read now so the file isn't kept open
                bitmap.DecodePixelWidth = 40;                  // drawn at ~20px; keeps memory small
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return (ImageSource)bitmap;
            });
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or NotSupportedException or UnauthorizedAccessException)
        {
            // offline or a broken file: draw the fallback square
        }

        await _dispatcher.InvokeAsync(() =>
        {
            _pending.Remove(relative);
            if (version != _version) return;
            _loaded[relative] = image;
            IconLoaded?.Invoke();
        });
    }
}
