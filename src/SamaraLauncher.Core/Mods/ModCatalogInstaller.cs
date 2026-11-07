using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Mods;

/// <summary>Подготовка и транзакционная установка проверенных файлов Modrinth.</summary>
public sealed class ModCatalogInstaller
{
    private static readonly SemaphoreSlim InstallLock = new(1, 1);
    private static readonly HttpClient Downloads = CreateDownloadClient();
    private readonly ModrinthClient _client;

    public ModCatalogInstaller(ModrinthClient? client = null) => _client = client ?? new ModrinthClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"SamaraLauncher/{LauncherVersion.Current}");
        return client;
    }

    public async Task<IReadOnlyList<ModrinthVersion>> PrepareAsync(
        string projectId, string? versionId, string minecraft, string loader, CancellationToken ct = default, string side = "client")
    {
        if (string.IsNullOrWhiteSpace(minecraft) || string.IsNullOrWhiteSpace(loader))
            throw new LauncherException("Для установки нужны определённые версия Minecraft и загрузчик.");
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool Compatible(ModrinthVersion v) =>
            v.GameVersions.Contains(minecraft, StringComparer.OrdinalIgnoreCase) &&
            v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase);

        async Task Visit(string? id, string? pinnedVersion, bool root)
        {
            ct.ThrowIfCancellationRequested();
            ModrinthVersion? version;
            if (!string.IsNullOrWhiteSpace(pinnedVersion))
                version = await _client.GetVersionAsync(pinnedVersion, ct).ConfigureAwait(false);
            else
            {
                if (string.IsNullOrWhiteSpace(id))
                    throw new LauncherException("Источник не указал проект обязательной зависимости.");
                var versions = await _client.GetProjectVersionsAsync(id, ct).ConfigureAwait(false);
                version = versions.Where(v => Compatible(v) && v.VersionType == "release")
                    .OrderByDescending(v => v.DatePublished).FirstOrDefault();
            }
            if (version is null || !Compatible(version) || string.IsNullOrWhiteSpace(version.ProjectId) ||
                (!string.IsNullOrWhiteSpace(id) && !string.Equals(id, version.ProjectId, StringComparison.OrdinalIgnoreCase)))
                throw new LauncherException($"Не удалось определить совместимую версию для {id ?? pinnedVersion}.");
            var project = await _client.GetProjectAsync(version.ProjectId, ct).ConfigureAwait(false);
            var support = side == "server" ? project?.ServerSide : project?.ClientSide;
            if (support is not ("required" or "optional"))
                throw new LauncherException($"Источник не подтверждает совместимость {version.Name} с {(side == "server" ? "сервером" : "клиентом")}.");
            if (!root && version.VersionType != "release")
                throw new LauncherException($"Зависимость {version.ProjectId} требует предварительную версию; установка отменена.");
            if (result.TryGetValue(version.ProjectId, out var existing))
            {
                if (!string.Equals(existing.Id, version.Id, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException($"Конфликт версий зависимости {version.ProjectId}.");
                return;
            }
            if (!visiting.Add(version.ProjectId))
                throw new LauncherException($"Обнаружен цикл зависимостей: {version.ProjectId}.");
            foreach (var dependency in version.Dependencies.Where(d => d.Type == "required"))
                await Visit(dependency.ProjectId, dependency.VersionId, false).ConfigureAwait(false);
            visiting.Remove(version.ProjectId);
            result.Add(version.ProjectId, version);
        }

        await Visit(projectId, versionId, true).ConfigureAwait(false);
        return result.Values.ToList();
    }

    public async Task InstallAsync(InstanceProfile instance, IReadOnlyList<ModrinthVersion> versions,
        IProgress<string>? progress = null, CancellationToken ct = default, IProgress<double>? overallProgress = null, string? forceProjectId = null)
    {
        await InstallLock.WaitAsync(ct).ConfigureAwait(false);
        var staging = "";
        var moved = new List<string>();
        var backups = new List<(string Original, string Backup)>();
        try
        {
            if (string.IsNullOrWhiteSpace(instance.GameDir)) throw new LauncherException("Не указана папка сборки.");
            staging = Path.Combine(Path.GetFullPath(instance.GameDir), ".samara-staging-" + Guid.NewGuid().ToString("N"));
            var mods = Path.Combine(Path.GetFullPath(instance.GameDir), "mods");
            var metadataPath = Path.Combine(instance.GameDir, ".samara-mods.json");
            var records = File.Exists(metadataPath)
                ? JsonSerializer.Deserialize<List<CatalogModRecord>>(await File.ReadAllTextAsync(metadataPath, ct).ConfigureAwait(false)) ?? new()
                : new List<CatalogModRecord>();
            Directory.CreateDirectory(staging);
            Directory.CreateDirectory(mods);
            var existingPaths = Directory.EnumerateFiles(mods).Where(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)).ToList();
            var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in existingPaths)
            {
                await using var input = File.OpenRead(path);
                hashes[path] = Convert.ToHexString(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false)).ToLowerInvariant();
            }
            var known = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in hashes.Values.Chunk(100))
                foreach (var pair in await _client.GetVersionsByHashesAsync(batch, ct).ConfigureAwait(false)) known[pair.Key] = pair.Value;
            var reader = new ModMetadataReader();
            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var unidentified = 0;
            foreach (var path in existingPaths)
            {
                var metadata = reader.Read(path);
                if (metadata is null || metadata.Loader == ModLoader.Unknown || string.IsNullOrWhiteSpace(metadata.ModId))
                {
                    unidentified++;
                    continue;
                }
                existingIds.Add(metadata.ModId);
            }
            if (unidentified > 0)
                progress?.Report($"У {unidentified} локальных файлов не удалось определить modId; проверяем подтверждённые компоненты.");
            foreach (var version in versions)
            foreach (var conflict in version.Dependencies.Where(d => d.Type == "incompatible"))
            {
                if (string.IsNullOrWhiteSpace(conflict.ProjectId) && string.IsNullOrWhiteSpace(conflict.VersionId))
                    throw new LauncherException($"Невозможно проверить несовместимость {version.Name}: источник не указал проект или версию.");
                var candidates = versions.Concat(known.Values);
                if (candidates.Any(v =>
                    (!string.IsNullOrWhiteSpace(conflict.VersionId) && v.Id == conflict.VersionId) ||
                    (string.IsNullOrWhiteSpace(conflict.VersionId) && !string.IsNullOrWhiteSpace(conflict.ProjectId) && v.ProjectId == conflict.ProjectId)))
                    throw new LauncherException($"Обнаружен несовместимый компонент для {version.Name}: {conflict.ProjectId ?? conflict.VersionId}.");
            }
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var replace = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in hashes)
                if (known.TryGetValue(pair.Value, out var installed) && versions.Any(v => v.ProjectId == installed.ProjectId))
                {
                    var requested = versions.First(v => v.ProjectId == installed.ProjectId);
                    if (pair.Key.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                        throw new LauncherException($"Файл проекта {installed.ProjectId} отключён. Сначала проверьте список модов.");
                    if (present.Contains(installed.ProjectId) || replace.ContainsKey(installed.ProjectId))
                        throw new LauncherException($"Найдено несколько файлов проекта {installed.ProjectId}.");
                    if (requested.Id == installed.Id && !string.Equals(installed.ProjectId, forceProjectId, StringComparison.OrdinalIgnoreCase)) { present.Add(installed.ProjectId); continue; }
                    var managed = records.Any(record => record.Source == "modrinth" && record.ProjectId == installed.ProjectId &&
                        record.VersionId == installed.Id && string.Equals(record.FileName, Path.GetFileName(pair.Key), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(record.Sha512, pair.Value, StringComparison.OrdinalIgnoreCase));
                    if (!managed) throw new LauncherException($"Файл проекта {installed.ProjectId} не подтверждён как установленный каталогом; автоматическая замена отменена.");
                    replace.Add(installed.ProjectId, pair.Key);
                }
            existingIds.Clear();
            foreach (var path in existingPaths.Where(path => !replace.Values.Contains(path, StringComparer.OrdinalIgnoreCase)))
            {
                var metadata = reader.Read(path);
                if (metadata is not null && metadata.Loader != ModLoader.Unknown && !string.IsNullOrWhiteSpace(metadata.ModId))
                    existingIds.Add(metadata.ModId);
            }
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var staged = new List<(ModrinthVersion Version, string Path, string Target)>();
            var pending = versions.Where(v => !present.Contains(v.ProjectId)).ToList();
            var completedFiles = 0;
            foreach (var version in pending)
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(version.FileName);
                var target = Path.Combine(mods, name);
                var replacesTarget = replace.TryGetValue(version.ProjectId, out var oldPath) &&
                    string.Equals(oldPath, target, StringComparison.OrdinalIgnoreCase);
                if (name != version.FileName || !name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                    !targets.Add(name) || (File.Exists(target) && !replacesTarget) || File.Exists(target + ".disabled"))
                    throw new LauncherException($"Недопустимое или уже занятое имя файла: {version.FileName}.");
                if (!Uri.TryCreate(version.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    version.Sha512.Length != 128 || !version.Sha512.All(Uri.IsHexDigit))
                    throw new LauncherException($"Для {name} отсутствует безопасный адрес или SHA-512.");
                var temp = Path.Combine(staging, Guid.NewGuid().ToString("N"));
                progress?.Report($"Загрузка: {name}");
                using var response = await Downloads.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                {
                    var buffer = new byte[81920];
                    long received = 0;
                    var total = response.Content.Headers.ContentLength;
                    var lastReport = DateTime.UtcNow;
                    int count;
                    while ((count = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        await output.WriteAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
                        received += count;
                        if (total is > 0) overallProgress?.Report(0.95 * (completedFiles + Math.Clamp((double)received / total.Value, 0, 1)) / pending.Count);
                        if (DateTime.UtcNow - lastReport < TimeSpan.FromMilliseconds(200)) continue;
                        progress?.Report(total is > 0 ? $"Загрузка {name}: {received * 100d / total.Value:0}%" : $"Загрузка {name}: {received / 1024} КБ");
                        lastReport = DateTime.UtcNow;
                    }
                }
                await using (var input = File.OpenRead(temp))
                {
                    var hash = Convert.ToHexString(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false));
                    if (!string.Equals(hash, version.Sha512, StringComparison.OrdinalIgnoreCase))
                        throw new LauncherException($"Контрольная сумма {name} не совпала.");
                }
                var metadata = reader.Read(temp);
                if (metadata is null || metadata.Loader == ModLoader.Unknown || string.IsNullOrWhiteSpace(metadata.ModId))
                    throw new LauncherException($"Нельзя проверить совместимость {name}: метаданные мода недоступны.");
                if (!existingIds.Add(metadata.ModId))
                    throw new LauncherException($"Конфликт идентификатора мода {metadata.ModId}. Файлы сборки не изменены.");
                staged.Add((version, temp, target));
                completedFiles++;
                overallProgress?.Report(0.95 * completedFiles / pending.Count);
            }
            ct.ThrowIfCancellationRequested();
            foreach (var oldPath in replace.Values)
            {
                await using var input = File.OpenRead(oldPath);
                var currentHash = Convert.ToHexString(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false));
                if (!string.Equals(currentHash, hashes[oldPath], StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException($"Файл {Path.GetFileName(oldPath)} изменился во время загрузки; установка отменена.");
            }
            ct.ThrowIfCancellationRequested();
            if (replace.Count > 0)
            {
                var backupDir = Path.Combine(instance.GameDir, ".samara-mod-backups", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(backupDir);
                var manifest = new CatalogBackupManifest
                {
                    Old = records.Where(record => replace.TryGetValue(record.ProjectId, out var original) &&
                        string.Equals(record.FileName, Path.GetFileName(original), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(record.Sha512, hashes[original], StringComparison.OrdinalIgnoreCase))
                        .GroupBy(record => record.ProjectId, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList(),
                    New = staged.Where(item => replace.ContainsKey(item.Version.ProjectId))
                        .Select(item => new CatalogModRecord { Source = "modrinth", ProjectId = item.Version.ProjectId,
                            VersionId = item.Version.Id, FileName = Path.GetFileName(item.Target), Sha512 = item.Version.Sha512 }).ToList()
                };
                await File.WriteAllTextAsync(Path.Combine(backupDir, "manifest.json"), JsonSerializer.Serialize(manifest), ct).ConfigureAwait(false);
                foreach (var oldPath in replace.Values)
                {
                    var backup = Path.Combine(backupDir, Path.GetFileName(oldPath));
                    File.Move(oldPath, backup);
                    backups.Add((oldPath, backup));
                }
            }
            foreach (var item in staged)
            {
                File.Move(item.Path, item.Target);
                moved.Add(item.Target);
            }
            var stagedProjects = staged.Select(item => item.Version.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            records.RemoveAll(record => stagedProjects.Contains(record.ProjectId));
            var installedAt = DateTimeOffset.UtcNow;
            foreach (var item in staged)
                records.Add(new CatalogModRecord { Source = "modrinth", ProjectId = item.Version.ProjectId, VersionId = item.Version.Id, FileName = Path.GetFileName(item.Target), Sha512 = item.Version.Sha512, InstalledAt = installedAt.AddTicks(staged.IndexOf(item)) });
            var metadataTemp = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(metadataTemp, JsonSerializer.Serialize(records), ct).ConfigureAwait(false);
                File.Move(metadataTemp, metadataPath, true);
            }
            finally { if (File.Exists(metadataTemp)) File.Delete(metadataTemp); }
            overallProgress?.Report(1);
        }
        catch (Exception error)
        {
            var rollbackErrors = new List<string>();
            foreach (var path in moved) try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rollbackErrors.Add(path + ": " + ex.Message); }
            foreach (var backup in backups.AsEnumerable().Reverse())
                try { File.Move(backup.Backup, backup.Original); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { rollbackErrors.Add(backup.Backup + ": " + ex.Message); }
            if (rollbackErrors.Count > 0)
                throw new LauncherException("Не удалось полностью откатить установку. Резервные файлы сохранены: " + string.Join("; ", rollbackErrors), error);
            throw;
        }
        finally
        {
            try { if (staging.Length > 0 && Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            InstallLock.Release();
        }
    }

    public async Task RollbackAsync(InstanceProfile instance, string projectId, CancellationToken ct = default)
    {
        await InstallLock.WaitAsync(ct).ConfigureAwait(false);
        var movedCurrent = new List<(string Current, string Backup)>();
        var restoredOld = new List<(string Current, string Backup)>();
        try
        {
            var root = Path.GetFullPath(instance.GameDir);
            var backupsRoot = Path.Combine(root, ".samara-mod-backups");
            if (!Directory.Exists(backupsRoot)) throw new LauncherException("Резервная версия не найдена.");
            string? selected = null;
            CatalogBackupManifest? manifest = null;
            foreach (var dir in Directory.EnumerateDirectories(backupsRoot).OrderByDescending(Directory.GetCreationTimeUtc))
            {
                var manifestPath = Path.Combine(dir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;
                var candidate = JsonSerializer.Deserialize<CatalogBackupManifest>(await File.ReadAllTextAsync(manifestPath, ct).ConfigureAwait(false));
                if (candidate?.Old.Any(record => record.ProjectId == projectId) == true && candidate.Old.All(record => File.Exists(Path.Combine(dir, Path.GetFileName(record.FileName)))))
                { selected = dir; manifest = candidate; break; }
            }
            if (selected is null || manifest is null || manifest.Old.Count == 0 || manifest.Old.Count != manifest.New.Count ||
                manifest.Old.Select(item => item.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.Old.Count ||
                !manifest.Old.Select(item => item.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(manifest.New.Select(item => item.ProjectId)))
                throw new LauncherException("Подходящая резервная версия не найдена.");
            var metadataPath = Path.Combine(root, ".samara-mods.json");
            var records = JsonSerializer.Deserialize<List<CatalogModRecord>>(await File.ReadAllTextAsync(metadataPath, ct).ConfigureAwait(false)) ?? new();
            var mods = Path.Combine(root, "mods");
            foreach (var item in manifest.New)
            {
                if (Path.GetFileName(item.FileName) != item.FileName || !records.Any(r => r.ProjectId == item.ProjectId && r.VersionId == item.VersionId && r.FileName == item.FileName && r.Sha512 == item.Sha512))
                    throw new LauncherException("Состояние установленного мода изменилось; откат отменён.");
                var path = Path.Combine(mods, item.FileName);
                await using var input = File.OpenRead(path);
                var hash = Convert.ToHexString(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false));
                if (!string.Equals(hash, item.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException($"Файл {item.FileName} изменён; откат отменён.");
            }
            foreach (var item in manifest.Old)
            {
                if (Path.GetFileName(item.FileName) != item.FileName) throw new LauncherException("Недопустимое имя в резервной копии.");
                var target = Path.Combine(mods, item.FileName);
                if (File.Exists(target + ".disabled")) throw new LauncherException($"Отключённый файл {item.FileName} уже существует; откат отменён.");
                if (File.Exists(target) && !manifest.New.Any(n => string.Equals(n.FileName, item.FileName, StringComparison.OrdinalIgnoreCase)))
                    throw new LauncherException($"Файл {item.FileName} уже существует; откат отменён.");
                await using var input = File.OpenRead(Path.Combine(selected, item.FileName));
                var hash = Convert.ToHexString(await SHA512.HashDataAsync(input, ct).ConfigureAwait(false));
                if (!string.Equals(hash, item.Sha512, StringComparison.OrdinalIgnoreCase))
                    throw new LauncherException($"Резервный файл {item.FileName} повреждён.");
            }
            ct.ThrowIfCancellationRequested();
            foreach (var item in manifest.New)
            {
                var current = Path.Combine(mods, item.FileName);
                var backup = Path.Combine(selected, "replaced-" + Guid.NewGuid().ToString("N") + ".jar");
                File.Move(current, backup); movedCurrent.Add((current, backup));
            }
            foreach (var item in manifest.Old)
            {
                var backup = Path.Combine(selected, item.FileName);
                var current = Path.Combine(mods, item.FileName);
                File.Move(backup, current); restoredOld.Add((current, backup));
            }
            var projects = manifest.New.Select(item => item.ProjectId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            records.RemoveAll(record => projects.Contains(record.ProjectId));
            records.AddRange(manifest.Old);
            var temp = metadataPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(records), ct).ConfigureAwait(false); File.Move(temp, metadataPath, true); }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        catch (Exception error)
        {
            var errors = new List<string>();
            foreach (var item in restoredOld.AsEnumerable().Reverse())
                try { File.Move(item.Current, item.Backup); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex.Message); }
            foreach (var item in movedCurrent.AsEnumerable().Reverse())
                try { File.Move(item.Backup, item.Current); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { errors.Add(ex.Message); }
            if (errors.Count > 0) throw new LauncherException("Не удалось полностью отменить откат: " + string.Join("; ", errors), error);
            throw;
        }
        finally { InstallLock.Release(); }
    }
}

public sealed class CatalogBackupManifest
{
    public List<CatalogModRecord> Old { get; set; } = new();
    public List<CatalogModRecord> New { get; set; } = new();
}

public sealed class CatalogModRecord
{
    public string Source { get; set; } = "";
    public string ProjectId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Sha512 { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
}

