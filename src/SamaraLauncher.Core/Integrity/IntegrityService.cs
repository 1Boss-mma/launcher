using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Integrity;

/// <summary>
/// Проверка хешей сборки по packwiz и защита локальных патчей (overrides.json).
/// </summary>
public sealed class IntegrityService
{
    private const int ProgressStep = 20;

    /// <summary>
    /// Файлы, которые игра и пользователь меняют штатно. Их не сравниваем с packwiz,
    /// иначе проверка всегда показывает «изменено» после смены настроек или серверов.
    /// </summary>
    private static readonly HashSet<string> MutableRootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "servers.dat",
        "servers.dat_old",
        "options.txt",
        "optionsof.txt",
        "usercache.json",
        "usernamecache.json",
        "launcher_profiles.json",
        "realms_persistence.json",
        "debug.log"
    };

    private static readonly string[] MutableDirectories =
    {
        "config/",
        "defaultconfigs/",
        "saves/",
        "screenshots/",
        "logs/",
        "crash-reports/",
        "backups/",
        ".mixin.out/"
    };

    /// <summary>
    /// Ищет папку packwiz-проекта: рядом с локальным PackUrl или в gameDir\packwiz.
    /// </summary>
    public static string ResolvePackwizDirectory(InstanceProfile instance)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан инстанс для проверки целостности.");
        }

        var gameDir = GetGameDirectory(instance);

        var localPack = TryResolveLocalPackPath(instance.PackUrl, gameDir);
        if (localPack is not null)
        {
            var packFile = Directory.Exists(localPack)
                ? Path.Combine(localPack, "pack.toml")
                : localPack;

            var packDirectory = Path.GetDirectoryName(Path.GetFullPath(packFile));
            if (!string.IsNullOrEmpty(packDirectory)
                && File.Exists(Path.Combine(packDirectory, "index.toml")))
            {
                return packDirectory;
            }
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(gameDir, "packwiz"),
                     gameDir
                 })
        {
            if (File.Exists(Path.Combine(candidate, "index.toml")))
            {
                return candidate;
            }
        }

        throw new LauncherException(
            "Не найден packwiz index.toml. Укажите локальный pack.toml в настройках инстанса " +
            $"или положите packwiz-проект в {Path.Combine(gameDir, "packwiz")}.");
    }

    public async Task<IntegrityReport> ScanAsync(
        InstanceProfile instance,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        var gameDir = GetGameDirectory(instance);
        var packwizDir = ResolvePackwizDirectory(instance);
        var index = ParseIndex(Path.Combine(packwizDir, "index.toml"));
        var packMods = LoadPackMods(packwizDir);
        var manifest = ProtectedFilesManifest.Load(gameDir);
        var report = new IntegrityReport();
        var handledProtected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        progress?.Report(new LauncherProgress(
            ProgressStage.Verify,
            $"Проверка файлов сборки ({index.Count} записей index.toml)..."));

        // Конфиги и прочие файлы из index.toml проверяются по sha256.
        foreach (var entry in index)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Metafile || entry.File.EndsWith(".pw.toml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var relative = ProtectedFilesManifest.NormalizePath(entry.File);
            var fullPath = ProtectedFilesManifest.GetFullPath(gameDir, relative);
            var exists = File.Exists(fullPath);
            var size = exists ? new FileInfo(fullPath).Length : 0;

            if (IsUserDataPath(relative))
            {
                continue;
            }

            if (TryGetProtected(manifest, relative, out var protectedKey, out var protectedInfo))
            {
                handledProtected.Add(protectedKey);
                var actual = exists
                    ? await ComputeHashAsync(fullPath, "sha256", ct)
                    : null;

                AddProtected(report, protectedKey, protectedInfo, actual, size, exists);
            }
            else if (!exists)
            {
                report.Missing.Add(new FileState
                {
                    Path = relative,
                    Kind = FileStateKind.Missing,
                    ExpectedHash = entry.Hash
                });
            }
            else
            {
                var format = NormalizeHashFormat(entry.HashFormat, entry.Hash);
                var actual = await ComputeHashAsync(fullPath, format, ct);
                var matches = string.Equals(actual, NormalizeHash(entry.Hash), StringComparison.OrdinalIgnoreCase);

                if (!matches)
                {
                    report.Modified.Add(new FileState
                    {
                        Path = relative,
                        Kind = FileStateKind.Modified,
                        ExpectedHash = entry.Hash,
                        ActualHash = actual,
                        Size = size
                    });
                }
            }

            report.CheckedFiles++;
            ReportProgress(progress, report.CheckedFiles, relative);
        }

        // Моды проверяются по metadata из mods\*.pw.toml (обычно sha512).
        var modsDir = Path.Combine(gameDir, "mods");
        var modFiles = EnumerateModFiles(modsDir);
        var actualByBaseName = new Dictionary<string, (string Name, string FullPath)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var file in modFiles)
        {
            var baseName = StripDisabledSuffix(file.Name);
            if (!actualByBaseName.ContainsKey(baseName)
                || !file.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                actualByBaseName[baseName] = (file.Name, file.FullPath);
            }
        }

        foreach (var packMod in packMods.Values)
        {
            ct.ThrowIfCancellationRequested();

            var relative = ProtectedFilesManifest.NormalizePath("mods/" + packMod.FileName);

            if (actualByBaseName.TryGetValue(packMod.FileName, out var actualFile))
            {
                var size = new FileInfo(actualFile.FullPath).Length;
                var format = NormalizeHashFormat(packMod.HashFormat, packMod.Hash);
                var actual = await ComputeHashAsync(actualFile.FullPath, format, ct);

                if (TryGetProtected(manifest, relative, out var protectedKey, out var protectedInfo))
                {
                    handledProtected.Add(protectedKey);
                    var sha256 = format == "sha256"
                        ? actual
                        : await ComputeHashAsync(actualFile.FullPath, "sha256", ct);

                    AddProtected(report, protectedKey, protectedInfo, sha256, size, true);
                }
                else
                {
                    var matches = string.Equals(
                        actual,
                        NormalizeHash(packMod.Hash),
                        StringComparison.OrdinalIgnoreCase);

                    if (!matches)
                    {
                        report.Modified.Add(new FileState
                        {
                            Path = "mods/" + actualFile.Name,
                            Kind = FileStateKind.Modified,
                            ExpectedHash = packMod.Hash,
                            ActualHash = actual,
                            Size = size
                        });
                    }
                }
            }
            else if (TryGetProtected(manifest, relative, out var protectedKey, out var protectedInfo))
            {
                handledProtected.Add(protectedKey);
                AddProtected(report, protectedKey, protectedInfo, null, 0, false);
            }
            else
            {
                report.Missing.Add(new FileState
                {
                    Path = relative,
                    Kind = FileStateKind.Missing,
                    ExpectedHash = packMod.Hash
                });
            }

            report.CheckedFiles++;
            ReportProgress(progress, report.CheckedFiles, relative);
        }

        // Лишние jar'ы: есть в mods, но нет в packwiz.
        foreach (var file in modFiles)
        {
            ct.ThrowIfCancellationRequested();

            var baseName = StripDisabledSuffix(file.Name);
            if (packMods.ContainsKey(baseName))
            {
                continue;
            }

            var relative = ProtectedFilesManifest.NormalizePath("mods/" + file.Name);
            var size = new FileInfo(file.FullPath).Length;

            if (TryGetProtected(manifest, relative, out var protectedKey, out var protectedInfo))
            {
                handledProtected.Add(protectedKey);
                var sha256 = await ComputeHashAsync(file.FullPath, "sha256", ct);
                AddProtected(report, protectedKey, protectedInfo, sha256, size, true);
            }
            else
            {
                report.Extra.Add(new FileState
                {
                    Path = relative,
                    Kind = FileStateKind.Extra,
                    Size = size
                });
            }

            report.CheckedFiles++;
            ReportProgress(progress, report.CheckedFiles, relative);
        }

        // Защищённые файлы, которых нет ни в index.toml, ни среди jar'ов.
        foreach (var (relative, info) in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            if (handledProtected.Contains(relative))
            {
                continue;
            }

            var fullPath = ProtectedFilesManifest.GetFullPath(gameDir, relative);
            if (File.Exists(fullPath))
            {
                var actual = await ComputeHashAsync(fullPath, "sha256", ct);
                AddProtected(report, relative, info, actual, new FileInfo(fullPath).Length, true);
            }
            else if (File.Exists(fullPath + ".disabled"))
            {
                var disabledHash = await ComputeHashAsync(fullPath + ".disabled", "sha256", ct);
                AddProtected(
                    report,
                    relative,
                    info,
                    disabledHash,
                    new FileInfo(fullPath + ".disabled").Length,
                    true,
                    "мод отключён");
            }
            else
            {
                AddProtected(report, relative, info, null, 0, false);
            }

            report.CheckedFiles++;
        }

        report.Missing.Sort(ByPath);
        report.Modified.Sort(ByPath);
        report.Extra.Sort(ByPath);
        report.Protected.Sort(ByPath);

        progress?.Report(new LauncherProgress(
            ProgressStage.Verify,
            report.IsHealthy
                ? "Проверка завершена: расхождений нет."
                : $"Проверка завершена: отсутствует {report.Missing.Count}, изменено {report.Modified.Count}, лишних {report.Extra.Count}.",
            report.CheckedFiles,
            report.CheckedFiles));

        return report;
    }

    /// <summary>Запоминает текущий sha256 файла как локальный патч.</summary>
    public async Task<ProtectedFileInfo> ProtectAsync(
        InstanceProfile instance,
        string relativePath,
        string reason,
        CancellationToken ct = default)
    {
        var gameDir = GetGameDirectory(instance);
        var relative = ProtectedFilesManifest.NormalizePath(relativePath);
        var fullPath = ProtectedFilesManifest.GetFullPath(gameDir, relative);

        if (!File.Exists(fullPath))
        {
            throw new LauncherException($"Нельзя защитить отсутствующий файл: {relative}");
        }

        var manifest = ProtectedFilesManifest.Load(gameDir);
        var hash = await ComputeHashAsync(fullPath, "sha256", ct);
        var size = new FileInfo(fullPath).Length;
        var info = manifest.Add(relative, hash, size, reason);
        manifest.Save();

        return info;
    }

    /// <summary>
    /// Возвращает защищённые файлы из бэкапа, если их текущий хеш не совпадает
    /// с сохранённым в overrides.json. backupPath — zip или распакованная папка.
    /// </summary>
    public async Task<IReadOnlyList<string>> RestoreProtectedAsync(
        string gameDir,
        string backupPath,
        IProgress<LauncherProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new LauncherException("Не указана папка игры для восстановления патчей.");
        }

        var gameRoot = Path.GetFullPath(gameDir);
        var manifest = ProtectedFilesManifest.Load(gameRoot);
        if (manifest.Files.Count == 0)
        {
            return Array.Empty<string>();
        }

        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new LauncherException("Не указан бэкап для восстановления защищённых файлов.");
        }

        var restored = new List<string>();
        ZipArchive? archive = null;

        try
        {
            Dictionary<string, ZipArchiveEntry>? zipEntries = null;
            string? backupRoot = null;

            if (File.Exists(backupPath))
            {
                try
                {
                    archive = ZipFile.OpenRead(backupPath);
                }
                catch (InvalidDataException ex)
                {
                    throw new LauncherException(
                        $"Бэкап повреждён: {Path.GetFileName(backupPath)}",
                        ex);
                }

                zipEntries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;
                    }

                    zipEntries[NormalizeArchivePath(entry.FullName)] = entry;
                }
            }
            else if (Directory.Exists(backupPath))
            {
                backupRoot = Path.GetFullPath(backupPath);
            }
            else
            {
                throw new LauncherException($"Бэкап не найден: {backupPath}");
            }

            foreach (var (relative, info) in manifest.Files)
            {
                ct.ThrowIfCancellationRequested();

                var target = ProtectedFilesManifest.GetFullPath(gameRoot, relative);

                // Отключённый пользователем мод не воскрешаем.
                if (File.Exists(target + ".disabled"))
                {
                    progress?.Report(new LauncherProgress(
                        ProgressStage.Verify,
                        $"Защищённый файл пропущен (мод отключён): {relative}"));
                    continue;
                }

                if (File.Exists(target))
                {
                    var current = await ComputeHashAsync(target, "sha256", ct);
                    if (string.Equals(current, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                var tempPath = target + ".restore-" + Guid.NewGuid().ToString("N");
                try
                {
                    if (zipEntries is not null)
                    {
                        if (!zipEntries.TryGetValue(relative, out var entry))
                        {
                            progress?.Report(new LauncherProgress(
                                ProgressStage.Verify,
                                $"В бэкапе нет защищённого файла: {relative}"));
                            continue;
                        }

                        var directory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        await using var input = entry.Open();
                        await using var output = new FileStream(
                            tempPath,
                            FileMode.Create,
                            FileAccess.Write,
                            FileShare.None,
                            81920,
                            FileOptions.Asynchronous | FileOptions.SequentialScan);

                        await input.CopyToAsync(output, 81920, ct);
                        await output.FlushAsync(ct);
                    }
                    else
                    {
                        var source = ProtectedFilesManifest.GetFullPath(backupRoot!, relative);
                        if (!File.Exists(source))
                        {
                            progress?.Report(new LauncherProgress(
                                ProgressStage.Verify,
                                $"В бэкапе нет защищённого файла: {relative}"));
                            continue;
                        }

                        var directory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(directory))
                        {
                            Directory.CreateDirectory(directory);
                        }

                        File.Copy(source, tempPath, overwrite: true);
                    }

                    var restoredHash = await ComputeHashAsync(tempPath, "sha256", ct);
                    if (!string.Equals(restoredHash, info.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new LauncherException(
                            $"Бэкап содержит файл {relative} с неверным хешем — восстановление остановлено.");
                    }

                    File.Move(tempPath, target, overwrite: true);
                    restored.Add(relative);

                    progress?.Report(new LauncherProgress(
                        ProgressStage.Verify,
                        $"Защищённый файл восстановлен: {relative}"));
                }
                finally
                {
                    TryDelete(tempPath);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new LauncherException(
                $"Бэкап повреждён: {Path.GetFileName(backupPath)}",
                ex);
        }
        catch (IOException ex)
        {
            throw new LauncherException(
                $"Не удалось восстановить защищённые файлы: {ex.Message}",
                ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherException(
                $"Нет доступа при восстановлении защищённых файлов: {ex.Message}",
                ex);
        }
        finally
        {
            archive?.Dispose();
        }

        return restored;
    }

    /// <summary>Проверяет только файлы из overrides.json.</summary>
    public async Task<IntegrityReport> VerifyAsync(
        InstanceProfile instance,
        CancellationToken ct = default)
    {
        var gameDir = GetGameDirectory(instance);
        var manifest = ProtectedFilesManifest.Load(gameDir);
        var report = new IntegrityReport();

        foreach (var (relative, info) in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var fullPath = ProtectedFilesManifest.GetFullPath(gameDir, relative);
            if (File.Exists(fullPath))
            {
                var actual = await ComputeHashAsync(fullPath, "sha256", ct);
                AddProtected(report, relative, info, actual, new FileInfo(fullPath).Length, true);
            }
            else if (File.Exists(fullPath + ".disabled"))
            {
                var disabledHash = await ComputeHashAsync(fullPath + ".disabled", "sha256", ct);
                AddProtected(
                    report,
                    relative,
                    info,
                    disabledHash,
                    new FileInfo(fullPath + ".disabled").Length,
                    true,
                    "мод отключён");
            }
            else
            {
                AddProtected(report, relative, info, null, 0, false);
            }

            report.CheckedFiles++;
        }

        report.Protected.Sort(ByPath);
        return report;
    }

    private static string GetGameDirectory(InstanceProfile instance)
    {
        if (instance is null)
        {
            throw new LauncherException("Не передан инстанс.");
        }

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры.");
        }

        var gameDir = Path.GetFullPath(instance.GameDir);
        if (!Directory.Exists(gameDir))
        {
            throw new LauncherException($"Папка игры не найдена: {gameDir}");
        }

        return gameDir;
    }

    private static string? TryResolveLocalPackPath(string? rawPackUrl, string gameDir)
    {
        var raw = (rawPackUrl ?? "").Trim().Trim('"');
        if (raw.Length == 0)
        {
            return null;
        }

        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme is "http" or "https")
            {
                return null;
            }

            if (uri.IsFile)
            {
                return uri.LocalPath;
            }

            if (raw.Contains("://", StringComparison.Ordinal)
                && !raw.Contains(":\\", StringComparison.Ordinal))
            {
                return null;
            }
        }

        var path = Environment.ExpandEnvironmentVariables(raw);
        if (!Path.IsPathRooted(path))
        {
            path = Path.Combine(gameDir, path);
        }

        return Path.GetFullPath(path);
    }

    private static List<PackIndexEntry> ParseIndex(string indexPath)
    {
        var blocks = new List<Dictionary<string, string>>();
        Dictionary<string, string>? current = null;
        string? defaultHashFormat = null;

        foreach (var rawLine in File.ReadLines(indexPath))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("[[files]]", StringComparison.OrdinalIgnoreCase))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                blocks.Add(current);
                continue;
            }

            if (line.StartsWith("[[", StringComparison.Ordinal) || line.StartsWith('['))
            {
                current = null;
                continue;
            }

            if (TryParseKeyValue(line, out var key, out var value))
            {
                if (current is not null)
                {
                    current[key] = value;
                }
                else if (string.Equals(key, "hash-format", StringComparison.OrdinalIgnoreCase))
                {
                    defaultHashFormat = value;
                }
            }
        }

        var result = new List<PackIndexEntry>(blocks.Count);
        foreach (var block in blocks)
        {
            if (!block.TryGetValue("file", out var file) || string.IsNullOrWhiteSpace(file))
            {
                continue;
            }

            block.TryGetValue("hash", out var hash);
            block.TryGetValue("hash-format", out var hashFormat);
            block.TryGetValue("metafile", out var metafile);

            if (string.IsNullOrWhiteSpace(hashFormat))
            {
                hashFormat = defaultHashFormat;
            }

            result.Add(new PackIndexEntry(
                file,
                hash,
                hashFormat,
                string.Equals(metafile, "true", StringComparison.OrdinalIgnoreCase)));
        }

        return result;
    }

    private static Dictionary<string, PackModEntry> LoadPackMods(string packwizDir)
    {
        var result = new Dictionary<string, PackModEntry>(StringComparer.OrdinalIgnoreCase);
        var modsDir = Path.Combine(packwizDir, "mods");

        if (!Directory.Exists(modsDir))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(modsDir, "*.pw.toml", SearchOption.TopDirectoryOnly))
        {
            var sections = ParseTomlSections(path);

            if (!sections.TryGetValue("", out var root)
                || !root.TryGetValue("filename", out var fileName)
                || string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            sections.TryGetValue("download", out var download);
            var hash = "";
            var hashFormat = "";
            download?.TryGetValue("hash", out hash);
            download?.TryGetValue("hash-format", out hashFormat);

            result[StripDisabledSuffix(fileName)] = new PackModEntry(
                fileName,
                hash,
                hashFormat);
        }

        return result;
    }

    private static Dictionary<string, Dictionary<string, string>> ParseTomlSections(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = new(StringComparer.OrdinalIgnoreCase)
        };

        var section = "";

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                var isArray = line.StartsWith("[[", StringComparison.Ordinal);
                var start = isArray ? 2 : 1;
                var end = line.IndexOf(isArray ? "]]" : "]", start, StringComparison.Ordinal);

                section = end > start
                    ? line[start..end].Trim().ToLowerInvariant()
                    : "";

                if (!sections.ContainsKey(section))
                {
                    sections[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                continue;
            }

            if (TryParseKeyValue(line, out var key, out var value))
            {
                sections[section][key] = value;
            }
        }

        return sections;
    }

    private static bool TryParseKeyValue(string line, out string key, out string value)
    {
        var separator = line.IndexOf('=');
        if (separator <= 0)
        {
            key = "";
            value = "";
            return false;
        }

        key = line[..separator].Trim().ToLowerInvariant();
        value = Unquote(line[(separator + 1)..].Trim());
        return key.Length > 0;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\''))
        {
            var quote = value[0];
            var builder = new StringBuilder();

            for (var index = 1; index < value.Length; index++)
            {
                var ch = value[index];

                if (ch == '\\' && quote == '"' && index + 1 < value.Length)
                {
                    var next = value[++index];
                    builder.Append(next switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        _ => next
                    });
                    continue;
                }

                if (ch == quote)
                {
                    break;
                }

                builder.Append(ch);
            }

            return builder.ToString();
        }

        var comment = value.IndexOf('#');
        return (comment >= 0 ? value[..comment] : value).Trim();
    }

    private static string NormalizeHashFormat(string? format, string? hash)
    {
        var normalized = (format ?? "").Trim().ToLowerInvariant();
        if (normalized.Length > 0)
        {
            return normalized;
        }

        return NormalizeHash(hash).Length == 128 ? "sha512" : "sha256";
    }

    private static string NormalizeHash(string? hash) =>
        (hash ?? "").Trim().Trim('"').ToLowerInvariant();

    private static async Task<string> ComputeHashAsync(
        string path,
        string format,
        CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            byte[] hash = format switch
            {
                "sha512" => await SHA512.HashDataAsync(stream, ct),
                "sha256" => await SHA256.HashDataAsync(stream, ct),
                "sha1" => await SHA1.HashDataAsync(stream, ct),
                _ => throw new LauncherException($"Неподдерживаемый формат хеша packwiz: {format}")
            };

            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (IOException ex)
        {
            throw new LauncherException($"Не удалось прочитать файл {path}: {ex.Message}", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new LauncherException($"Нет доступа к файлу {path}: {ex.Message}", ex);
        }
    }

    private static List<(string Name, string FullPath)> EnumerateModFiles(string modsDir)
    {
        var result = new List<(string Name, string FullPath)>();

        if (!Directory.Exists(modsDir))
        {
            return result;
        }

        foreach (var path in Directory.EnumerateFiles(modsDir, "*", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            {
                result.Add((name, path));
            }
        }

        // Включённые моды должны попадать в actualByBaseName раньше отключённых.
        return result
            .OrderBy(file => file.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string StripDisabledSuffix(string fileName) =>
        fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".disabled".Length]
            : fileName;

    private static bool TryGetProtected(
        ProtectedFilesManifest manifest,
        string relativePath,
        out string key,
        out ProtectedFileInfo info)
    {
        key = ProtectedFilesManifest.NormalizePath(relativePath);

        if (manifest.Files.TryGetValue(key, out info!))
        {
            return true;
        }

        if (key.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
        {
            var baseKey = key[..^".disabled".Length];
            if (manifest.Files.TryGetValue(baseKey, out info!))
            {
                key = baseKey;
                return true;
            }
        }

        info = null!;
        return false;
    }

    private static void AddProtected(
        IntegrityReport report,
        string path,
        ProtectedFileInfo info,
        string? actualHash,
        long size,
        bool exists,
        string? note = null)
    {
        var reason = info.Reason;
        if (!string.IsNullOrWhiteSpace(note))
        {
            reason = string.IsNullOrWhiteSpace(reason) ? note : $"{reason} ({note})";
        }

        report.Protected.Add(new FileState
        {
            Path = path,
            Kind = FileStateKind.Protected,
            ExpectedHash = info.Sha256,
            ActualHash = actualHash,
            Size = size,
            HashMatches = exists
                && actualHash is not null
                && string.Equals(actualHash, info.Sha256, StringComparison.OrdinalIgnoreCase),
            Reason = reason
        });
    }

    private static bool IsUserDataPath(string relativePath)
    {
        var path = relativePath.Replace('\\', '/');
        if (MutableRootFiles.Contains(path))
        {
            return true;
        }

        foreach (var directory in MutableDirectories)
        {
            if (path.StartsWith(directory, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeArchivePath(string path)
    {        var normalized = path.Replace('\\', '/').TrimStart('/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        if (normalized.Length == 0
            || normalized.Split('/').Any(part => part == ".."))
        {
            throw new LauncherException($"Бэкап содержит недопустимый путь: {path}");
        }

        return normalized;
    }

    private static void ReportProgress(
        IProgress<LauncherProgress>? progress,
        int checkedFiles,
        string path)
    {
        if (checkedFiles % ProgressStep == 0)
        {
            progress?.Report(new LauncherProgress(
                ProgressStage.Verify,
                $"Проверка: {path}",
                checkedFiles));
        }
    }

    private static int ByPath(FileState left, FileState right) =>
        string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record PackIndexEntry(
        string File,
        string? Hash,
        string? HashFormat,
        bool Metafile);

    private sealed record PackModEntry(
        string FileName,
        string? Hash,
        string? HashFormat);
}
