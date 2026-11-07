using System.Windows;
using System.Windows.Media;
using SamaraLauncher.App.Controls;
using SamaraLauncher.Core.Diagnostics;

namespace SamaraLauncher.App.ViewModels;

/// <summary>Строка отчёта диагностики с готовыми бейджем и цветом статуса.</summary>
public sealed class DoctorCheckRow
{
    public DoctorCheckRow(DoctorCheck check)
    {
        Check = check;
        BadgeText = check.Status switch
        {
            DoctorStatus.Ok => "OK",
            DoctorStatus.Warning => "ВНИМАНИЕ",
            DoctorStatus.Error => "ОШИБКА",
            DoctorStatus.Skipped => "ПРОПУЩЕНО",
            _ => check.Status.ToString().ToUpperInvariant()
        };

        BadgeBrush = check.Status switch
        {
            DoctorStatus.Ok => PixelPalette.GreenHi,
            DoctorStatus.Warning => PixelPalette.Gold,
            DoctorStatus.Error => PixelPalette.Red,
            _ => PixelPalette.Muted
        };

        FixVisibility = check.CanAutoFix ? Visibility.Visible : Visibility.Collapsed;
        DetailsVisibility = string.IsNullOrWhiteSpace(check.Details)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    public DoctorCheck Check { get; }

    public string Title => string.IsNullOrWhiteSpace(Check.Title) ? Check.Id : Check.Title;

    public string CategoryText => Check.Category switch
    {
        DoctorCategory.Java => "Java",
        DoctorCategory.Files => "Файлы",
        DoctorCategory.Mods => "Моды",
        DoctorCategory.Network => "Сеть",
        DoctorCategory.Disk => "Диск",
        DoctorCategory.Config => "Настройки",
        DoctorCategory.Logs => "Логи",
        _ => Check.Category.ToString()
    };

    public string Message => Check.Message;

    public string DetailsText => Check.Details;

    public Visibility DetailsVisibility { get; }

    public string BadgeText { get; }

    public Brush BadgeBrush { get; }

    public string FixText => "АВТОИСПРАВИМО";

    public Visibility FixVisibility { get; }

    public string Tooltip
    {
        get
        {
            var parts = new List<string> { $"{CategoryText} / {Title}", Message };
            if (!string.IsNullOrWhiteSpace(Check.Details))
            {
                parts.Add(Check.Details);
            }

            if (!string.IsNullOrWhiteSpace(Check.FixHint))
            {
                parts.Add("Что делать: " + Check.FixHint);
            }

            return string.Join(Environment.NewLine, parts);
        }
    }

    public string AutomationName => $"{BadgeText} · {CategoryText} · {Title}";
}
