using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Mods;
using SamaraLauncher.Core.ServerHost;
using SamaraLauncher.Core.Worlds;

namespace SamaraLauncher.App.ViewModels;

/// <summary>Слот хотбара на главной: сборка или «добавить».</summary>
public sealed class InstanceSlot
{
    public string Key { get; init; } = "1";
    public string ShortName { get; init; } = "СБОРКА";
    public string Color { get; init; } = "#5FBE4A";
    public bool Installed { get; init; }
    public bool IsSelected { get; set; }
    public string Tooltip { get; init; } = "";
    public bool IsAdd { get; init; }
}

/// <summary>Новость в виджете «Маяк».</summary>
public sealed class NewsItem
{
    public string Tag { get; set; } = "НОВОСТЬ";
    public string Title { get; set; } = "";
    public string Date { get; set; } = "";
    public string Accent { get; set; } = "#5FBE4A";
}

/// <summary>Строка списка модов с уведомлениями для WPF.</summary>
public sealed class ModRow : INotifyPropertyChanged
{
    public SamaraLauncher.App.Controls.LauncherGlyph Glyph => SamaraLauncher.App.Controls.LauncherGlyph.Mods;
    private bool _iconLoaded;
    private System.Windows.Media.ImageSource? _iconImage;
    public System.Windows.Media.ImageSource? IconImage
    {
        get { if (!_iconLoaded) { _iconLoaded = true; _ = LoadIconAsync(); } return _iconImage; }
    }
    private async Task LoadIconAsync()
    {
        _iconImage = await ModIconLoader.ReadAsync(Entry.FullPath);
        OnPropertyChanged(nameof(IconImage));
    }
    private bool _enabled;
    private bool _hasUpdate;
    private string _latestVersion = "";
    private string _statusText = "ОК";
    private string _statusBrush = "#7CD964";

    public ModRow(ModEntry entry, ModMetadata? metadata = null, CatalogModRecord? catalogRecord = null)
    {
        Entry = entry;
        Metadata = metadata;
        CatalogRecord = catalogRecord;
        _enabled = entry.Enabled;
        RefreshStatus();
    }

    public ModEntry Entry { get; }

    public ModMetadata? Metadata { get; }

    public CatalogModRecord? CatalogRecord { get; }

    public string FileName => Entry.FileName;

    public string DisplayName => !string.IsNullOrWhiteSpace(Metadata?.Name)
        ? Metadata!.Name
        : BaseName;

    public string Subtitle => (!string.IsNullOrWhiteSpace(Metadata?.ModId)
        ? Metadata!.ModId
        : Metadata is null
            ? "метаданные не прочитаны"
            : "modId неизвестен") + (CatalogRecord is null ? " · локальный" : " · Modrinth");

    public string VersionText => string.IsNullOrWhiteSpace(Metadata?.Version)
        ? "—"
        : Metadata!.Version;

    public string LoaderText => Metadata?.Loader switch
    {
        ModLoader.Forge => "Forge",
        ModLoader.Fabric => "Fabric",
        ModLoader.NeoForge => "NeoForge",
        _ => "Unknown"
    };

    public string SizeText => FormatSize(Entry.Size);

    public string StateText => _enabled ? "ВКЛ" : "ВЫКЛ";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    public bool HasUpdate => _hasUpdate;

    public string LatestVersion => _latestVersion;

    public string UpdateText => _hasUpdate && !string.IsNullOrWhiteSpace(_latestVersion)
        ? $"{VersionText} → {_latestVersion}"
        : VersionText;

    public bool IsProblem => !string.IsNullOrWhiteSpace(ProblemText);

    /// <summary>Короткая причина проблемы для колонки статуса; пусто — проблем нет.</summary>
    public string ProblemText { get; private set; } = "";

    public string StatusText => _statusText;

    public string StatusBrush => _statusBrush;

    public string AutomationName => $"{DisplayName} ({FileName})";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetProblem(string? reason)
    {
        ProblemText = reason?.Trim() ?? "";
        RefreshStatus();
    }

    public void SetUpdate(string? latestVersion)
    {
        _latestVersion = latestVersion?.Trim() ?? "";
        _hasUpdate = !string.IsNullOrWhiteSpace(_latestVersion);
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(LatestVersion));
        OnPropertyChanged(nameof(UpdateText));
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        if (IsProblem)
        {
            _statusText = "ПРОБЛЕМА";
            _statusBrush = "#E5533D";
        }
        else if (_hasUpdate)
        {
            _statusText = "ОБНОВЛЕНИЕ";
            _statusBrush = "#F5C542";
        }
        else
        {
            _statusText = "ОК";
            _statusBrush = "#7CD964";
        }

        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
    }

    private string BaseName => Entry.FileName
        .Replace(".jar.disabled", "", StringComparison.OrdinalIgnoreCase)
        .Replace(".jar", "", StringComparison.OrdinalIgnoreCase);

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024d / 1024d:0.##} ГБ",
        >= 1024L * 1024 => $"{bytes / 1024d / 1024d:0.#} МБ",
        >= 1024 => $"{bytes / 1024d:0.#} КБ",
        _ => $"{bytes} Б"
    };
}

/// <summary>Карточка мира. Цвет и статус зависят от валидности level.dat.</summary>
public sealed class WorldRow
{
    public WorldRow(WorldInfo info)
    {
        Info = info;
    }

    public WorldInfo Info { get; }

    public string FolderName => Info.Name;

    public string Name => string.IsNullOrWhiteSpace(Info.DisplayName)
        ? Info.Name
        : Info.DisplayName;

    public string Path => Info.Path;

    public string SizeText => ModRow.FormatSize(Info.SizeBytes);

    public string DateText => Info.LastPlayed == default
        ? "дата неизвестна"
        : Info.LastPlayed.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public string SeedText => Info.IsValid ? Info.Seed.ToString(CultureInfo.InvariantCulture) : "—";

    public string VersionText => string.IsNullOrWhiteSpace(Info.VersionName) ? "—" : Info.VersionName;

    public string MetaText => $"СИД {SeedText} · {VersionText}";

    public string StatusText => !Info.IsValid
        ? "ПОВРЕЖДЁН"
        : Info.Hardcore
            ? "ХАРДКОР"
            : "ОК";

    public string StatusBrush => Info.IsValid ? "#7CD964" : "#E5533D";

    public string CardBorderBrush => Info.IsValid ? "#05070A" : "#E5533D";

    public bool IsValid => Info.IsValid;

    public string IconColor => string.IsNullOrWhiteSpace(Info.IconColor) ? "#5FBE4A" : Info.IconColor;

    public string AutomationName => $"Мир {Name}";
}

/// <summary>Строка выбора zip-бэкапа мира.</summary>
public sealed class WorldBackupRow
{
    public string Path { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string DateText { get; init; } = "";
    public string SizeText { get; init; } = "";

    public string FileName => System.IO.Path.GetFileName(Path);

    public string AutomationName => $"Бэкап {DisplayName} {DateText}".Trim();
}

/// <summary>Мир сервера в списке «Мой сервер».</summary>
public sealed class ServerWorldRow
{
    public ServerWorldRow(ServerWorldInfo info)
    {
        Info = info;
    }

    public ServerWorldInfo Info { get; }

    public string FolderName => Info.Name;

    public string Name => string.IsNullOrWhiteSpace(Info.DisplayName)
        ? Info.Name
        : Info.DisplayName;

    public bool IsActive => Info.IsActive;

    public bool CanActivate => Info.Exists && !Info.IsActive;

    public bool CanBackup => Info.Exists && Info.IsValid;

    public bool CanDelete => Info.Exists && !Info.IsActive;

    public string StatusText => !Info.Exists
        ? "НЕТ ПАПКИ"
        : !Info.IsValid
            ? "ПОВРЕЖДЁН"
            : Info.IsActive
                ? "АКТИВНЫЙ"
                : "ОК";

    public string StatusBrush => !Info.Exists || !Info.IsValid
        ? "#E5533D"
        : Info.IsActive
            ? "#7CD964"
            : "#94A0AC";

    public string SizeText => ModRow.FormatSize(Info.SizeBytes);

    public string DateText => Info.LastPlayed == default
        ? "дата неизвестна"
        : Info.LastPlayed.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public string MetaText => Info.Exists
        ? $"{SizeText} · СИД {Info.Seed.ToString(CultureInfo.InvariantCulture)} · " +
          $"{(string.IsNullOrWhiteSpace(Info.VersionName) ? "версия неизвестна" : Info.VersionName)}"
        : "папка появится после запуска сервера";

    public string AutomationName => $"Мир сервера {Name}";
}

/// <summary>Zip-бэкап серверного мира в списке «Мой сервер».</summary>
public sealed class ServerWorldBackupRow
{
    public ServerWorldBackupRow(ServerWorldBackupInfo info)
    {
        Info = info;
    }

    public ServerWorldBackupInfo Info { get; }

    public string FileName => Info.FileName;

    public string WorldName => string.IsNullOrWhiteSpace(Info.WorldName)
        ? "неизвестный мир"
        : Info.WorldName;

    public string Name => string.IsNullOrWhiteSpace(Info.DisplayName)
        ? WorldName
        : Info.DisplayName;

    public string DateText => Info.CreatedUtc == default
        ? "дата неизвестна"
        : Info.CreatedUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public string SizeText => ModRow.FormatSize(Info.SizeBytes);

    public string AutomationName => $"Бэкап серверного мира {Name} {DateText}".Trim();
}

/// <summary>Строка найденного обновления мода.</summary>
public sealed class ModUpdateRow
{
    public ModUpdateRow(ModUpdateInfo info)
    {
        Info = info;
    }

    public ModUpdateInfo Info { get; }

    public string FileName => Info.FileName;

    public string ModId => string.IsNullOrWhiteSpace(Info.ModId) ? "modId неизвестен" : Info.ModId;

    public string VersionsText => $"{Info.CurrentVersion} → {Info.LatestVersion}";

    public string PublishedText => Info.PublishedAt == default
        ? ""
        : Info.PublishedAt.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

    public string Changelog => string.IsNullOrWhiteSpace(Info.Changelog)
        ? "Без описания изменений."
        : Info.Changelog.Trim();

    public string AutomationName => $"Обновление {FileName}";
}

/// <summary>Строка подробного отчёта анализа модов.</summary>
public sealed class ModIssueRow
{
    public string Category { get; init; } = "";

    public string Text { get; init; } = "";

    public string Color { get; init; } = "#94A0AC";

    public string AutomationName => $"{Category}: {Text}";
}

/// <summary>Загрузка новостей из news.json рядом с лаунчером или из LocalAppData.</summary>
public static class NewsFeed
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static IReadOnlyList<NewsItem> Load()
    {
        foreach (var path in Candidates())
        {
            try
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                var json = File.ReadAllText(path);
                var items = JsonSerializer.Deserialize<List<NewsItem>>(json, JsonOptions);
                if (items is { Count: > 0 })
                {
                    return items;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // Новости не критичны: показываем встроенные.
            }
        }

        return Defaults();
    }

    private static IEnumerable<string> Candidates()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "news.json");

        var local = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SamaraLauncher",
            "news.json");
        yield return local;
    }

    private static IReadOnlyList<NewsItem> Defaults() => new List<NewsItem>
    {
        new()
        {
            Tag = "РЕЛИЗ",
            Title = "Сборка 1.20.1: 87 модов, Forge 47.4.28",
            Date = "30 октября 2026",
            Accent = "#5FBE4A"
        },
        new()
        {
            Tag = "СБОРКА",
            Title = "Автообновление через packwiz уже в лаунчере",
            Date = "30 октября 2026",
            Accent = "#58C7F0"
        },
        new()
        {
            Tag = "СОВЕТ",
            Title = "Первый запуск займёт больше времени: качаются библиотеки",
            Date = "30 октября 2026",
            Accent = "#F5C542"
        }
    };
}

