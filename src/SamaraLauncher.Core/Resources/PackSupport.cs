using System.ComponentModel;
using System.Globalization;
using System.Text;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Core.Resources;

/// <summary>Общие файловые операции модуля ресурспаков и шейдерпаков.</summary>
internal static class PackStorage
{
    public const string ZipFormat = "zip";

    public const string FolderFormat = "folder";

    private static readonly EnumerationOptions RecursiveFiles = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false
    };

    public static string GetGameDir(InstanceProfile instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("У инстанса не указана папка игры.");
        }

        try
        {
            return Path.GetFullPath(instance.GameDir.Trim());
        }
        catch (Exception ex) when (IsExpectedPathError(ex))
        {
            throw new LauncherException(
                $"Недопустимая папка игры инстанса: {instance.GameDir}",
                ex);
        }
    }

    public static string GetPackFolder(InstanceProfile instance, string folderName) =>
        Path.Combine(GetGameDir(instance), folderName);

    public static PackInfo CreateInfo(
        string path,
        bool enabled,
        PackInspection inspection)
    {
        var isFolder = Directory.Exists(path);
        var fileName = Path.GetFileName(path);
        var title = isFolder ? fileName : Path.GetFileNameWithoutExtension(fileName);

        var info = new PackInfo
        {
            FileName = fileName,
            FullPath = path,
            Enabled = enabled,
            Title = string.IsNullOrWhiteSpace(title) ? fileName : title,
            Description = inspection.Description,
            IconPng = inspection.IconPng,
            Format = isFolder ? FolderFormat : ZipFormat,
            ModifiedAt = GetModifiedAt(path)
        };

        if (isFolder)
        {
            info.Size = GetDirectorySize(path);
        }
        else
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists)
                {
                    info.Size = file.Length;
                }
            }
            catch (Exception ex) when (IsExpectedFileError(ex))
            {
                info.Size = 0;
            }
        }

        return info;
    }

    public static long GetDirectorySize(string directory)
    {
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", RecursiveFiles))
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Exists)
                    {
                        total += info.Length;
                    }
                }
                catch (Exception ex) when (IsExpectedFileError(ex))
                {
                    // Пропускаем файл, который не удалось прочитать.
                }
            }
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            // Возвращаем то, что успели посчитать.
        }

        return total;
    }

    public static DateTimeOffset GetModifiedAt(string path)
    {
        try
        {
            var utc = Directory.Exists(path)
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);

            return new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            return DateTimeOffset.MinValue;
        }
    }

    public static void CopyFileAtomic(string source, string target)
    {
        var directory = Path.GetDirectoryName(target);
        var tempPath = target + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var input = new FileStream(
                       source,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       81920,
                       FileOptions.SequentialScan))
            using (var output = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.SequentialScan))
            {
                input.CopyTo(output, 81920);
            }

            File.Move(tempPath, target);
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            throw new LauncherException(
                $"Не удалось скопировать {Path.GetFileName(source)}: {ex.Message}",
                ex);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public static string MoveToBackup(string source, string backupsRoot, string fileName)
    {
        if (!File.Exists(source) && !Directory.Exists(source))
        {
            throw new LauncherException($"Пак не найден: {source}");
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var directory = Path.Combine(backupsRoot, stamp);
        var target = MakeUniquePath(directory, fileName);

        try
        {
            Directory.CreateDirectory(directory);

            if (Directory.Exists(source))
            {
                MoveDirectory(source, target);
            }
            else
            {
                File.Move(source, target);
            }
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            throw new LauncherException(
                $"Не удалось перенести {Path.GetFileName(source)} в {directory}: {ex.Message}",
                ex);
        }

        return target;
    }

    public static void DeletePermanently(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
            else
            {
                throw new LauncherException($"Пак не найден: {path}");
            }
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (IsExpectedFileError(ex))
        {
            throw new LauncherException(
                $"Не удалось удалить {Path.GetFileName(path)}: {ex.Message}",
                ex);
        }
    }

    public static void OpenFolder(string folder, string errorPrefix)
    {
        try
        {
            Directory.CreateDirectory(folder);
            ProcessHost.ShellOpen(folder);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is Win32Exception
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            throw new LauncherException($"{errorPrefix}: {ex.Message}", ex);
        }
    }

    public static string RequireFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new LauncherException("Не указано имя файла пака.");
        }

        var name = fileName.Trim();

        if (name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.IndexOf('/') >= 0
            || name.IndexOf('\\') >= 0
            || Path.IsPathRooted(name))
        {
            throw new LauncherException($"Недопустимое имя файла пака: {fileName}");
        }

        return name;
    }

    public static string RequirePackPath(PackInfo pack)
    {
        if (pack is null)
        {
            throw new LauncherException("Не передан пак.");
        }

        var name = RequireFileName(pack.FileName);

        if (string.IsNullOrWhiteSpace(pack.FullPath))
        {
            throw new LauncherException($"Не указан путь к паку: {name}");
        }

        string path;
        try
        {
            path = Path.GetFullPath(pack.FullPath.Trim());
        }
        catch (Exception ex) when (IsExpectedPathError(ex))
        {
            throw new LauncherException($"Недопустимый путь к паку: {pack.FullPath}", ex);
        }

        var actualName = Path.GetFileName(
            path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        if (!string.Equals(actualName, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new LauncherException(
                $"Путь пака не соответствует имени файла: {pack.FullPath}");
        }

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new LauncherException($"Пак не найден: {path}");
        }

        return path;
    }

    public static void TryDeleteFile(string path)
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

    public static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string MakeUniquePath(string directory, string fileName)
    {
        var name = string.IsNullOrWhiteSpace(fileName) ? "pack" : fileName;
        var candidate = Path.Combine(directory, name);

        if (!File.Exists(candidate) && !Directory.Exists(candidate))
        {
            return candidate;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var index = 2; index < 10_000; index++)
        {
            candidate = Path.Combine(directory, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new LauncherException($"Не удалось подобрать имя для {name}.");
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // Другой диск или занятый файл: пробуем копирование.
        }

        try
        {
            CopyDirectory(source, destination);
            Directory.Delete(source, recursive: true);
        }
        catch
        {
            TryDeleteDirectory(destination);
            throw;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", RecursiveFiles))
        {
            var relativePath = Path.GetRelativePath(source, file);
            var targetPath = Path.Combine(destination, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetPath);

            if (!string.IsNullOrEmpty(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            using var input = new FileStream(
                file,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            using var output = new FileStream(
                targetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                81920,
                FileOptions.SequentialScan);

            input.CopyTo(output, 81920);
        }
    }

    private static bool IsExpectedFileError(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or NotSupportedException
            or ArgumentException
            or PathTooLongException
            or System.Security.SecurityException;

    private static bool IsExpectedPathError(Exception ex) =>
        ex is ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException;
}

/// <summary>
/// options.txt / optionsshaders.txt с сохранением кодировки, BOM, переводов строк
/// и порядка остальных строк. Значение ключа меняется построчно.
/// </summary>
internal sealed class OptionsFile
{
    private readonly string _path;
    private readonly List<string> _lines;
    private readonly Encoding _encoding;
    private readonly string _newLine;
    private readonly bool _trailingNewLine;
    private bool _exists;
    private bool _dirty;

    private OptionsFile(
        string path,
        List<string> lines,
        Encoding encoding,
        string newLine,
        bool trailingNewLine,
        bool exists)
    {
        _path = path;
        _lines = lines;
        _encoding = encoding;
        _newLine = newLine;
        _trailingNewLine = trailingNewLine;
        _exists = exists;
    }

    public static OptionsFile Load(string path)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Недопустимый путь к файлу настроек: {path}", ex);
        }

        byte[] bytes;
        try
        {
            using var stream = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            bytes = memory.ToArray();
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return new OptionsFile(
                fullPath,
                new List<string>(),
                new UTF8Encoding(false, false),
                Environment.NewLine,
                true,
                exists: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось прочитать {Path.GetFileName(fullPath)}: {ex.Message}",
                ex);
        }

        var (encoding, bomLength) = DetectEncoding(bytes);
        var text = encoding.GetString(bytes, bomLength, bytes.Length - bomLength);
        var (lines, newLine, trailingNewLine) = SplitLines(text);

        return new OptionsFile(fullPath, lines, encoding, newLine, trailingNewLine, exists: true);
    }

    public string? GetValue(string key)
    {
        foreach (var line in _lines)
        {
            if (TryFindKey(line, key, out var colon))
            {
                return line[(colon + 1)..];
            }
        }

        return null;
    }

    /// <summary>Меняет только строку ключа; если ключа нет — добавляет в конец.</summary>
    public void SetValue(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new LauncherException("Не указан ключ настройки.");
        }

        var safeValue = value ?? "";
        if (safeValue.IndexOf('\r') >= 0 || safeValue.IndexOf('\n') >= 0)
        {
            throw new LauncherException($"Недопустимое значение настройки {key}.");
        }

        for (var index = 0; index < _lines.Count; index++)
        {
            var line = _lines[index];
            if (!TryFindKey(line, key, out var colon))
            {
                continue;
            }

            var replacement = line[..(colon + 1)] + safeValue;
            if (!string.Equals(line, replacement, StringComparison.Ordinal))
            {
                _lines[index] = replacement;
                _dirty = true;
            }

            return;
        }

        _lines.Add(key + ":" + safeValue);
        _dirty = true;
    }

    /// <summary>Пишет временный файл и атомарно заменяет им целевой.</summary>
    public bool Save()
    {
        if (!_dirty)
        {
            return false;
        }

        var directory = Path.GetDirectoryName(_path);
        var text = BuildText();
        var bytes = Encode(text);
        var tempPath = _path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       81920,
                       FileOptions.SequentialScan))
            {
                stream.Write(bytes, 0, bytes.Length);
            }

            if (_exists && File.Exists(_path))
            {
                var backupPath = BuildBackupPath();
                try
                {
                    File.Replace(tempPath, _path, backupPath, ignoreMetadataErrors: true);
                }
                catch (PlatformNotSupportedException)
                {
                    CopyForBackup(_path, backupPath);
                    File.Move(tempPath, _path, overwrite: true);
                }
            }
            else
            {
                File.Move(tempPath, _path);
            }

            _exists = true;
            _dirty = false;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось сохранить {Path.GetFileName(_path)}: {ex.Message}",
                ex);
        }
        finally
        {
            PackStorage.TryDeleteFile(tempPath);
        }
    }

    private static bool TryFindKey(string line, string key, out int colon)
    {
        if (line.Length > key.Length
            && line[key.Length] == ':'
            && line.StartsWith(key, StringComparison.OrdinalIgnoreCase))
        {
            colon = key.Length;
            return true;
        }

        colon = -1;
        return false;
    }

    private string BuildText()
    {
        var text = string.Join(_newLine, _lines);
        return _trailingNewLine ? text + _newLine : text;
    }

    private byte[] Encode(string text)
    {
        var preamble = _encoding.GetPreamble();
        var body = _encoding.GetBytes(text);

        if (preamble.Length == 0)
        {
            return body;
        }

        var bytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, bytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, bytes, preamble.Length, body.Length);
        return bytes;
    }

    private string BuildBackupPath()
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var basePath = _path + "." + stamp;
        var candidate = basePath + ".bak";

        for (var index = 2; index < 10_000; index++)
        {
            if (!File.Exists(candidate))
            {
                return candidate;
            }

            candidate = basePath + "-" + index + ".bak";
        }

        throw new LauncherException(
            $"Не удалось подобрать имя для бэкапа {Path.GetFileName(_path)}.");
    }

    private static void CopyForBackup(string source, string backupPath)
    {
        using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.SequentialScan);

        using var output = new FileStream(
            backupPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.SequentialScan);

        input.CopyTo(output, 81920);
    }

    private static (Encoding Encoding, int BomLength) DetectEncoding(byte[] bytes)
    {
        if (bytes.Length >= 4)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
            {
                return (new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: false), 4);
            }

            if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            {
                return (new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: false), 4);
            }
        }

        if (bytes.Length >= 3
            && bytes[0] == 0xEF
            && bytes[1] == 0xBB
            && bytes[2] == 0xBF)
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: false), 3);
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return (new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: false), 2);
            }

            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return (new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: false), 2);
            }
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false), 0);
    }

    private static (List<string> Lines, string NewLine, bool TrailingNewLine) SplitLines(string text)
    {
        var newLine = DetectNewLine(text);

        if (text.Length == 0)
        {
            return (new List<string>(), newLine, true);
        }

        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var trailingNewLine = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').ToList();

        if (trailingNewLine)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return (lines, newLine, trailingNewLine);
    }

    private static string DetectNewLine(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\n')
            {
                return index > 0 && text[index - 1] == '\r' ? "\r\n" : "\n";
            }

            if (text[index] == '\r')
            {
                return index + 1 < text.Length && text[index + 1] == '\n' ? "\r\n" : "\r";
            }
        }

        return Environment.NewLine;
    }
}
