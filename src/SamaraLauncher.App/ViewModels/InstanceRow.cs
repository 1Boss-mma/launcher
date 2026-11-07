using System.Windows;
using System.Windows.Media;
using SamaraLauncher.App.Controls;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.App.ViewModels;

/// <summary>Строка списка сборок в менеджере инстансов.</summary>
public sealed class InstanceRow
{
    private static readonly SolidColorBrush InactiveFrame =
        PixelPalette.CreateBrush(Color.FromRgb(0x3A, 0x45, 0x50));

    public InstanceRow(InstanceProfile instance, string javaText, string javaPath, bool isActive)
    {
        Instance = instance;
        Name = instance.Name;
        VersionText = MainWindow.FormatInstanceVersion(instance);
        JavaText = javaText;
        JavaPath = javaPath;
        MemoryText = $"{instance.MemoryMb / 1024.0:0.#} ГБ";
        GameDir = instance.GameDir;
        IsActive = isActive;
        StatusText = instance.IsInstalled ? "установлена" : "не установлена";
        StatusBrush = instance.IsInstalled ? PixelPalette.GreenHi : PixelPalette.Gold;
        FavoriteVisibility = instance.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        ActiveVisibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        IconBrush = ParseColor(instance.IconColor);
        FrameBrush = isActive ? PixelPalette.Green : InactiveFrame;
        BarBrush = isActive ? PixelPalette.Green : Brushes.Transparent;
        AutomationName = isActive ? $"Сборка {Name}, активна" : $"Сборка {Name}";
        Tooltip = $"{Name}\n{VersionText}\n{GameDir}\nJava: {javaPath}";
    }

    public InstanceProfile Instance { get; }

    public string Name { get; }

    public string VersionText { get; }

    public string JavaText { get; }

    public string JavaPath { get; }

    public string MemoryText { get; }

    public string GameDir { get; }

    public bool IsActive { get; }

    public string StatusText { get; }

    public Brush StatusBrush { get; }

    public Visibility ActiveVisibility { get; }

    public Visibility FavoriteVisibility { get; }

    public Brush IconBrush { get; }

    public Brush FrameBrush { get; }

    public Brush BarBrush { get; }

    public string AutomationName { get; }

    public string Tooltip { get; }

    private static Brush ParseColor(string? value)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(
                string.IsNullOrWhiteSpace(value) ? "#5FBE4A" : value.Trim());
            return PixelPalette.CreateBrush(color);
        }
        catch (Exception)
        {
            return PixelPalette.Green;
        }
    }
}
