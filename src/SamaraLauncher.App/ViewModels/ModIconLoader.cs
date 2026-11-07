using System.IO;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SamaraLauncher.App.ViewModels;

internal static class ModIconLoader
{
    private static readonly SemaphoreSlim IconSlots = new(2);
    private static readonly ConcurrentDictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static Task<ImageSource?> ReadAsync(string path) => Task.Run(async () =>
    {
        await IconSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            var info = new FileInfo(Directory.Exists(path) ? Path.Combine(path, "pack.png") : path);
            var key = path + "|" + (info.Exists ? $"{info.Length}|{info.LastWriteTimeUtc.Ticks}" : "missing");
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var image = Read(path);
            if (image is not null)
            {
                if (Cache.Count >= 512) Cache.Clear();
                Cache[key] = image;
            }
            return image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        finally { IconSlots.Release(); }
    });

    internal static ImageSource? Read(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var file = new[] { "pack.png", "icon.png", "logo.png" }
                    .Select(name => Path.Combine(path, name)).FirstOrDefault(File.Exists);
                if (file is null || new FileInfo(file).Length > 2 * 1024 * 1024) return null;
                using var source = File.OpenRead(file);
                return Decode(source);
            }
            using var archive = ZipFile.OpenRead(path);
            string? iconPath = null;
            var manifest = archive.GetEntry("fabric.mod.json");
            if (manifest is { Length: < 262144 })
            {
                using var stream = manifest.Open();
                using var json = JsonDocument.Parse(stream);
                if (json.RootElement.TryGetProperty("icon", out var icon))
                {
                    if (icon.ValueKind == JsonValueKind.String) iconPath = icon.GetString();
                    else if (icon.ValueKind == JsonValueKind.Object)
                        iconPath = icon.EnumerateObject().Select(p => p.Value).FirstOrDefault(p => p.ValueKind == JsonValueKind.String).GetString();
                }
            }
            var entry = iconPath is null ? null : archive.GetEntry(iconPath.TrimStart('/'));
            entry ??= archive.Entries.FirstOrDefault(e => e.FullName.Equals("icon.png", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("logo.png", StringComparison.OrdinalIgnoreCase) || e.FullName.Equals("pack.png", StringComparison.OrdinalIgnoreCase));
            entry ??= archive.Entries.FirstOrDefault(e => e.FullName.EndsWith("/icon.png", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith("/logo.png", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith("/pack.png", StringComparison.OrdinalIgnoreCase));
            if (entry is null || entry.Length > 2 * 1024 * 1024) return null;
            using var iconSource = entry.Open();
            return Decode(iconSource);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or NotSupportedException or System.IO.FileFormatException) { return null; }
    }

    private static ImageSource Decode(Stream source)
    {
        using var memory = new MemoryStream(); source.CopyTo(memory); memory.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.DecodePixelWidth = 64; image.StreamSource = memory; image.EndInit(); image.Freeze(); return image;
    }
}
