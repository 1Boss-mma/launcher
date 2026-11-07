using System.Globalization;
using System.Text;

namespace SamaraLauncher.Core.ServerHost;

/// <summary>
/// Чтение и запись server.properties с сохранением порядка строк, комментариев
/// и незнакомых ключей. Битые строки не приводят к исключению — они остаются как есть.
/// </summary>
public sealed class ServerProperties
{
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly List<PropertyLine> _lines = new();
    private string _newLine = Environment.NewLine;
    private bool _endsWithNewLine = true;

    /// <summary>Загружает файл. Отсутствующий файл даёт пустой набор свойств.</summary>
    public static ServerProperties Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LauncherException("Не указан путь к server.properties.");
        }

        var properties = new ServerProperties();

        if (!File.Exists(path))
        {
            return properties;
        }

        string text;

        try
        {
            text = File.ReadAllText(path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось прочитать {path}: {ex.Message}", ex);
        }

        properties.Parse(text);
        return properties;
    }

    /// <summary>Значение ключа или null, если ключа нет.</summary>
    public string? Get(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        // java.util.Properties при повторе ключа оставляет последнее значение.
        for (var index = _lines.Count - 1; index >= 0; index--)
        {
            var line = _lines[index];
            if (line.Key is not null && string.Equals(line.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return line.Value;
            }
        }

        return null;
    }

    /// <summary>Значение ключа или <paramref name="fallback"/>, если ключа нет.</summary>
    public string Get(string key, string fallback) => Get(key) ?? fallback;

    /// <summary>Целое значение ключа или <paramref name="fallback"/> при отсутствии/ошибке разбора.</summary>
    public int GetInt(string key, int fallback = 0)
    {
        var value = Get(key);
        return value is not null
               && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    /// <summary>Логическое значение ключа или <paramref name="fallback"/> при отсутствии/ошибке разбора.</summary>
    public bool GetBool(string key, bool fallback = false)
    {
        var value = Get(key);
        return value is not null && bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    /// <summary>Задаёт строковое значение, сохраняя остальные строки файла.</summary>
    public void Set(string key, string value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new LauncherException("Не указан ключ server.properties.");
        }

        key = key.Trim();
        value ??= "";

        for (var index = _lines.Count - 1; index >= 0; index--)
        {
            var line = _lines[index];
            if (line.Key is null || !string.Equals(line.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!string.Equals(line.Value, value, StringComparison.Ordinal))
            {
                line.Value = value;
                line.IsDirty = true;
            }

            return;
        }

        _lines.Add(new PropertyLine
        {
            Key = key,
            Value = value,
            Raw = key + "=" + value
        });
    }

    /// <summary>Задаёт целое значение в инвариантном формате.</summary>
    public void Set(string key, int value) => Set(key, value.ToString(CultureInfo.InvariantCulture));

    /// <summary>Задаёт логическое значение в формате true/false.</summary>
    public void Set(string key, bool value) => Set(key, value ? "true" : "false");

    /// <summary>
    /// Сохраняет файл в UTF-8 без BOM, по возможности сохраняя исходные переводы строк.
    /// </summary>
    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LauncherException("Не указан путь к server.properties.");
        }

        path = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new LauncherException(
                    $"Не удалось создать папку {directory}: {ex.Message}", ex);
            }
        }

        var builder = new StringBuilder();

        for (var index = 0; index < _lines.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(_newLine);
            }

            var line = _lines[index];
            builder.Append(line.IsDirty || line.Raw is null
                ? line.Key + "=" + line.Value
                : line.Raw);
        }

        if (_endsWithNewLine && _lines.Count > 0)
        {
            builder.Append(_newLine);
        }

        try
        {
            File.WriteAllText(path, builder.ToString(), Utf8NoBom);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException($"Не удалось сохранить {path}: {ex.Message}", ex);
        }
    }

    private void Parse(string text)
    {
        _newLine = text.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : text.Contains('\n')
                ? "\n"
                : Environment.NewLine;

        _endsWithNewLine = text.Length == 0 || text[^1] is '\n' or '\r';

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var parts = normalized.Split('\n');
        var count = _endsWithNewLine && parts.Length > 0 ? parts.Length - 1 : parts.Length;

        for (var index = 0; index < count; index++)
        {
            _lines.Add(ParseLine(parts[index]));
        }
    }

    private static PropertyLine ParseLine(string raw)
    {
        var trimmed = raw.Trim();

        if (trimmed.Length == 0 || trimmed[0] is '#' or '!')
        {
            return new PropertyLine { Raw = raw };
        }

        var separator = trimmed.IndexOf('=');
        if (separator <= 0)
        {
            // Строка без '=' или с пустым ключом — сохраняем как есть.
            return new PropertyLine { Raw = raw };
        }

        var key = trimmed[..separator].Trim();
        if (key.Length == 0)
        {
            return new PropertyLine { Raw = raw };
        }

        return new PropertyLine
        {
            Key = key,
            Value = trimmed[(separator + 1)..].Trim(),
            Raw = raw
        };
    }

    private sealed class PropertyLine
    {
        public string? Key { get; init; }

        public string Value { get; set; } = "";

        public string? Raw { get; init; }

        public bool IsDirty { get; set; }
    }
}
