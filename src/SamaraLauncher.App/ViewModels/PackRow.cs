using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using SamaraLauncher.App.Controls;
using SamaraLauncher.Core.Resources;

namespace SamaraLauncher.App.ViewModels;

/// <summary>Строка списка ресурспака или шейдерпака.</summary>
public sealed class PackRow : INotifyPropertyChanged
{
    private bool _enabled;
    private bool _iconLoaded;
    private System.Windows.Media.ImageSource? _icon;
    public System.Windows.Media.ImageSource? IconImage { get { if (!_iconLoaded) { _iconLoaded = true; _ = LoadIconAsync(); } return _icon; } }
    private async Task LoadIconAsync()
    {
        _icon = await ModIconLoader.ReadAsync(Pack.FullPath);
        OnPropertyChanged(nameof(IconImage));
    }
    public LauncherGlyph Glyph => IsShader ? LauncherGlyph.Sun : LauncherGlyph.Image;

    public PackRow(PackInfo pack, string kind)
    {
        Pack = pack;
        Kind = kind;
        _enabled = pack.Enabled;
    }

    public string DisplayName => Title;
    public PackRow Metadata => this;
    public string UpdateText => "";
    public string LoaderText => IsShader ? "Шейдер" : "Текстурпак";
    public string Subtitle => FileName + " · " + SizeText;
    public string ProblemText => Tooltip;
    public PackInfo Pack { get; }

    /// <summary>Ключ вкладки: <c>resourcepacks</c> или <c>shaders</c>.</summary>
    public string Kind { get; }

    public bool IsShader => string.Equals(Kind, "shaders", StringComparison.OrdinalIgnoreCase);

    public string FileName => Pack.FileName;

    public string Title => string.IsNullOrWhiteSpace(Pack.Title)
        ? Path.GetFileNameWithoutExtension(Pack.FileName)
        : Pack.Title;

    public string FileNameText => Pack.FileName;

    public string Description => string.IsNullOrWhiteSpace(Pack.Description)
        ? "без описания"
        : Pack.Description.Trim();

    public string SizeText => ModRow.FormatSize(Pack.Size);

    public string FormatText => string.Equals(Pack.Format, "folder", StringComparison.OrdinalIgnoreCase)
        ? "ПАПКА"
        : "ZIP";

    public string ModifiedText => Pack.ModifiedAt == default
        ? "—"
        : Pack.ModifiedAt.ToLocalTime().ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);

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
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            OnPropertyChanged(nameof(ToggleActionText));
            OnPropertyChanged(nameof(ToggleTooltip));
            OnPropertyChanged(nameof(ToggleAutomationName));
            OnPropertyChanged(nameof(Tooltip));
        }
    }

    public string StatusText => Enabled ? "ВКЛ" : "ВЫКЛ";

    public string StatusBrush => Enabled ? "#7CD964" : "#94A0AC";

    public string ToggleActionText => Enabled ? "ВЫКЛ" : "ВКЛ";

    public string ToggleTooltip => Enabled ? "Выключить пак" : "Включить пак";

    public string Tooltip =>
        $"{Title}\n{FileName}\n{FormatText} · {SizeText} · изменён {ModifiedText}\n{ShortDescription}";

    public string AutomationName => $"{Title} ({FileName})";

    public string ToggleAutomationName => Enabled
        ? $"Выключить {Title}"
        : $"Включить {Title}";

    public string DeleteAutomationName => $"Удалить {Title}";

    public PixelIconKind IconKind => IsShader ? PixelIconKind.Mods : PixelIconKind.Folder;

    public string IconBrush => Enabled ? "#7CD964" : "#94A0AC";

    private string ShortDescription => Description.Length <= 240
        ? Description
        : Description[..237] + "...";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

