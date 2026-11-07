using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SamaraLauncher.App.Controls;

namespace SamaraLauncher.App;

public partial class MainWindow
{
    private void ApplyWorkspaceTheme(bool night)
    {
        var roles = new (string Key, string Night, string Day)[] {
            ("BgBrush", "#101820", "#F1F5F6"), ("BgDarkBrush", "#101820", "#F1F5F6"),
            ("PanelBrush", "#1B2933", "#FFFFFF"), ("Panel2Brush", "#263640", "#E3EBEE"),
            ("LineBrush", "#40515D", "#BBC9CF"), ("TextBrush", "#EDF4F5", "#17272E"),
            ("MutedBrush", "#A9BAC3", "#465D68"), ("GreenBrush", "#3C7D2F", "#36702B"),
            ("GreenHiBrush", "#477F38", "#427B35"), ("GreenLoBrush", "#336B29", "#2F6426"),
            ("SceneTintBrush", "#80101820", "#99F1F5F6"), ("AccentTextBrush", "#FFFFFF", "#FFFFFF"), ("SuccessBrush", "#81CB89", "#246735"),
            ("BuildControlBrush", "#BF1B242B", "#F2FFFFFF"), ("BuildControlLineBrush", "#78848B", "#8C9BA3"), ("BuildTrackBrush", "#50313C43", "#50BBC9CF"), ("BuildThumbBrush", "#828C92", "#6C7C85"),
            ("BuildDetailsBrush", "#E51B252B", "#F2FFFFFF"), ("BuildSurfaceBrush", "#D91A2228", "#EFFFFFFF"), ("BuildSelectedBrush", "#A63C7D2F", "#4036702B"),
            ("GoldBrush", "#F1BF68", "#795000"), ("RedBrush", "#F07D73", "#A4322B"),
            ("AquaBrush", "#3C7D2F", "#36702B"), ("XpBrush", "#81CB89", "#246735") };
        foreach (var role in roles) Application.Current.Resources[role.Key] = Brush(night ? role.Night : role.Day);
        Application.Current.Resources["TransparentBrush"] = Brushes.Transparent;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        foreach (Window window in Application.Current.Windows) RefreshThemeVisuals(window);
        foreach (var card in _settingsCards.Values) RefreshThemeVisuals(card);
    }

    private static void RefreshThemeVisuals(DependencyObject root)
    {
        if (root is PixelFrame frame)
        {
            frame.SetResourceReference(PixelFrame.PanelBrushProperty, "PanelBrush");
            frame.SetResourceReference(PixelFrame.FrameBrushProperty, "LineBrush");
            frame.SetResourceReference(PixelFrame.InsetBrushProperty, "Panel2Brush");
            frame.Cut = 0;
        }
        if (root is UIElement element) element.InvalidateVisual();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) RefreshThemeVisuals(VisualTreeHelper.GetChild(root, i));
    }
}



