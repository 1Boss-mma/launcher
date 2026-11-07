using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Launcher.Setup;

internal static class CleanInstallation
{
    internal const string Marker = ".launcher-install";
    internal const string ProductId = "Launcher-9ed546d7";
    internal const string Manifest = ".launcher-files.json";

    internal static void WriteManifest(string root, IEnumerable<string> files) =>
        File.WriteAllText(Path.Combine(root, Manifest), JsonSerializer.Serialize(files.ToArray()));

    internal static IReadOnlyList<string> Clean(string activeRoot, IEnumerable<string?> previousRoots, IEnumerable<string> payloadFiles)
    {
        var active = Normalize(activeRoot);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var retained = new List<string>();
        foreach (var previous in previousRoots.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                var root = Normalize(previous!);
                candidates.Add(root);
                var parent = Path.GetDirectoryName(root);
                if (parent is null || !Directory.Exists(parent)) continue;
                foreach (var prefix in new[] { root + ".backup-", root + ".install-" })
                    foreach (var directory in Directory.EnumerateDirectories(parent, Path.GetFileName(prefix) + "*"))
                        if (Guid.TryParseExact(directory.Substring(prefix.Length), "N", out _)) candidates.Add(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { retained.Add(previous!); }
        }

        foreach (var root in candidates)
        {
            if (string.Equals(root, active, StringComparison.OrdinalIgnoreCase) || !Directory.Exists(root)) continue;
            try
            {
                RejectLink(root);
                var marker = Path.Combine(root, Marker);
                if (!File.Exists(marker)) continue;
                RejectLink(marker);
                if (File.ReadAllText(marker) != ProductId) continue;

                var files = payloadFiles.ToList();
                var manifest = Path.Combine(root, Manifest);
                if (File.Exists(manifest))
                {
                    RejectLink(manifest);
                    files.AddRange(JsonSerializer.Deserialize<string[]>(File.ReadAllText(manifest)) ?? Array.Empty<string>());
                }
                files.AddRange(new[] { "Uninstall.exe", "Launcher.exe.old", Manifest, Marker });
                // Validate the entire list before deleting any file in this installation.
                var paths = files.Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(name => ValidateFile(root, name))
                    .Where(path => !IsPlayerData(Path.GetRelativePath(root, path)) &&
                                   !path.StartsWith(active + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var path in paths)
                {
                    if (File.Exists(path)) File.Delete(path);
                    var parent = Path.GetDirectoryName(path);
                    while (parent is not null && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) &&
                           Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                    { Directory.Delete(parent); parent = Path.GetDirectoryName(parent); }
                }
                if (!Directory.EnumerateFileSystemEntries(root).Any()) Directory.Delete(root);
                else retained.Add(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
            { retained.Add(root); }
        }
        return retained.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string Normalize(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (string.Equals(root, Path.TrimEndingDirectorySeparator(Path.GetPathRoot(root)!), StringComparison.OrdinalIgnoreCase) || root.StartsWith(@"\\"))
            throw new IOException("Недопустимая папка старой установки.");
        return root;
    }

    private static string ValidateFile(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name)) throw new IOException("Некорректный список файлов установки.");
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("Файл находится вне папки установки.");
        for (var current = path; !string.Equals(current, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            if (File.Exists(current) || Directory.Exists(current)) RejectLink(current);
        return path;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Очистка ссылок на другие папки запрещена.");
    }

    private static bool IsPlayerData(string name)
    {
        var first = name.Replace('\\', '/').Split('/')[0];
        return new[] { "instances", "worlds", "saves", "mods", "config", "screenshots", "resourcepacks", "shaderpacks", "logs", "backups", "cache", "runtime", "updates",
            "config.json", "distribution.json", "distribution.local.json", "secrets.json", "launcher_profiles.json", "options.txt", "servers.dat" }
            .Contains(first, StringComparer.OrdinalIgnoreCase);
    }
}
