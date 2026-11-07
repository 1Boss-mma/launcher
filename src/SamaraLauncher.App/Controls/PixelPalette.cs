using System;
using System.Windows;
using System.Windows.Media;

namespace SamaraLauncher.App.Controls;

/// <summary>
/// Общая палитра лаунчера. Кисти заморожены и безопасны для использования из XAML.
/// </summary>
public static class PixelPalette
{
    public static Color BgColor => (Application.Current?.TryFindResource("BgBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x12, 0x15, 0x1A);
    public static Color BgDarkColor => (Application.Current?.TryFindResource("BgDarkBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x0D, 0x10, 0x14);
    public static Color PanelColor => (Application.Current?.TryFindResource("PanelBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x1B, 0x20, 0x27);
    public static Color Panel2Color => (Application.Current?.TryFindResource("Panel2Brush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x23, 0x2A, 0x33);
    public static Color LineColor => (Application.Current?.TryFindResource("LineBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x05, 0x07, 0x0A);
    public static Color TextColor => (Application.Current?.TryFindResource("TextBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xE9, 0xED, 0xF2);
    public static Color MutedColor => (Application.Current?.TryFindResource("MutedBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x94, 0xA0, 0xAC);
    public static Color GreenColor => (Application.Current?.TryFindResource("GreenBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x5F, 0xBE, 0x4A);
    public static Color GreenHiColor => (Application.Current?.TryFindResource("GreenHiBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x7C, 0xD9, 0x64);
    public static Color GreenLoColor => (Application.Current?.TryFindResource("GreenLoBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x3C, 0x7D, 0x2F);
    public static Color GoldColor => (Application.Current?.TryFindResource("GoldBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xF5, 0xC5, 0x42);
    public static Color RedColor => (Application.Current?.TryFindResource("RedBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xE5, 0x53, 0x3D);
    public static Color AquaColor => (Application.Current?.TryFindResource("AquaBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x58, 0xC7, 0xF0);
    public static Color XpColor => (Application.Current?.TryFindResource("XpBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0x7F, 0xEA, 0x1F);
    public static Color XpHiColor => (Application.Current?.TryFindResource("XpBrush") as SolidColorBrush)?.Color ?? Color.FromRgb(0xC8, 0xFF, 0x86);

    public static SolidColorBrush Bg => CreateBrush(BgColor);
    public static SolidColorBrush BgDark => CreateBrush(BgDarkColor);
    public static SolidColorBrush Panel => CreateBrush(PanelColor);
    public static SolidColorBrush Panel2 => CreateBrush(Panel2Color);
    public static SolidColorBrush Line => CreateBrush(LineColor);
    public static SolidColorBrush Text => CreateBrush(TextColor);
    public static SolidColorBrush Muted => CreateBrush(MutedColor);
    public static SolidColorBrush Green => CreateBrush(GreenColor);
    public static SolidColorBrush GreenHi => CreateBrush(GreenHiColor);
    public static SolidColorBrush GreenLo => CreateBrush(GreenLoColor);
    public static SolidColorBrush Gold => CreateBrush(GoldColor);
    public static SolidColorBrush Red => CreateBrush(RedColor);
    public static SolidColorBrush Aqua => CreateBrush(AquaColor);
    public static SolidColorBrush Xp => CreateBrush(XpColor);
    public static SolidColorBrush XpHi => CreateBrush(XpHiColor);

    /// <summary>Создаёт замороженную кисть указанного цвета.</summary>
    public static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static Color WithAlpha(Color color, byte alpha)
        => Color.FromArgb(alpha, color.R, color.G, color.B);

    public static Color Lerp(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0d, 1d);
        return Color.FromArgb(
            (byte)Math.Round(from.A + (to.A - from.A) * t),
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    public static Color Lighten(Color color, double amount)
        => Lerp(color, Colors.White, amount);

    public static Color Darken(Color color, double amount)
        => Lerp(color, Colors.Black, amount);
}

/// <summary>Внутренние помощники для пиксельной геометрии.</summary>
internal static class PixelChrome
{
    /// <summary>Прямоугольник со срезанными уголками.</summary>
    public static StreamGeometry Chamfer(Rect rect, double cut)
    {
        double maxCut = Math.Min(rect.Width, rect.Height) / 2d;
        double c = Math.Clamp(cut, 0d, Math.Max(0d, maxCut));

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(rect.Left + c, rect.Top), true, true);
            context.LineTo(new Point(rect.Right - c, rect.Top), true, false);
            context.LineTo(new Point(rect.Right, rect.Top + c), true, false);
            context.LineTo(new Point(rect.Right, rect.Bottom - c), true, false);
            context.LineTo(new Point(rect.Right - c, rect.Bottom), true, false);
            context.LineTo(new Point(rect.Left + c, rect.Bottom), true, false);
            context.LineTo(new Point(rect.Left, rect.Bottom - c), true, false);
            context.LineTo(new Point(rect.Left, rect.Top + c), true, false);
        }

        geometry.Freeze();
        return geometry;
    }

    public static void DrawChamfer(
        DrawingContext context,
        Rect rect,
        double cut,
        Brush? fill,
        Brush? stroke,
        double strokeWidth = 0d)
    {
        StreamGeometry geometry = Chamfer(rect, cut);
        Pen? pen = stroke is not null && strokeWidth > 0d ? new Pen(stroke, strokeWidth) : null;
        context.DrawGeometry(fill, pen, geometry);
    }
}
