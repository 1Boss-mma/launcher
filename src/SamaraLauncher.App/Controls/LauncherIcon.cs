using System.Windows;
using System.Windows.Media;

namespace SamaraLauncher.App.Controls;

public enum LauncherGlyph { Home, Builds, Worlds, Servers, Workshop, Settings, ChevronDown, ChevronUp, Minimize, Close, Mods, Image, Sun, Play, Download, Sort, Trash, Refresh }

/// <summary>Consistent 24-unit vector icons for launcher navigation and window chrome.</summary>
public sealed class LauncherIcon : FrameworkElement
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(LauncherGlyph), typeof(LauncherIcon), new FrameworkPropertyMetadata(LauncherGlyph.Home, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(LauncherIcon), new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));
    public LauncherGlyph Kind { get => (LauncherGlyph)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    private static readonly IReadOnlyDictionary<LauncherGlyph, Geometry> Shapes = new Dictionary<LauncherGlyph, Geometry>
    {
        [LauncherGlyph.Mods] = Geometry.Parse("M3,4 L9,4 9,3 A3,3 0 1 1 15,3 L15,4 21,4 21,10 22,10 A3,3 0 1 1 22,16 L21,16 21,22 15,22 15,20 A3,3 0 1 0 9,20 L9,22 3,22 3,16 5,16 A3,3 0 1 0 5,10 L3,10 Z"),
        [LauncherGlyph.Image] = Geometry.Parse("M2,3 L22,3 22,21 2,21 Z M3,19 L9,11 13,15 17,9 22,16 M7,7 A1.5,1.5 0 1 0 7.01,7"),
        [LauncherGlyph.Sun] = Geometry.Parse("M12,6 A6,6 0 1 0 12.01,6 M12,0 L12,3 M12,21 L12,24 M0,12 L3,12 M21,12 L24,12 M3,3 L5,5 M19,19 L21,21 M3,21 L5,19 M19,5 L21,3"),
        [LauncherGlyph.Play] = Geometry.Parse("M6,3 L21,12 6,21 Z"),
        [LauncherGlyph.Download] = Geometry.Parse("M12,2 L12,16 M7,11 L12,16 17,11 M3,19 L3,22 21,22 21,19"),
        [LauncherGlyph.Sort] = Geometry.Parse("M6,2 L6,22 M2,6 L6,2 10,6 M18,2 L18,22 M14,18 L18,22 22,18"),
        [LauncherGlyph.Trash] = Geometry.Parse("M3,5 L21,5 M9,5 L9,2 15,2 15,5 M6,5 L7,22 17,22 18,5 M10,9 L10,18 M14,9 L14,18"),
        [LauncherGlyph.Refresh] = Geometry.Parse("M21,9 A9,9 0 0 0 5,5 L2,8 M2,2 L2,8 8,8 M3,15 A9,9 0 0 0 19,19 L22,16 M16,16 L22,16 22,22"),
        [LauncherGlyph.Home] = Geometry.Parse("M2,11 L12,2 22,11 M5,9 L5,22 10,22 10,15 14,15 14,22 19,22 19,9"),
        [LauncherGlyph.Builds] = Geometry.Parse("M12,1 L17,4 12,7 7,4 Z M7,4 L7,10 12,13 17,10 17,4 M12,7 L12,13 M6,11 L11,14 6,17 1,14 Z M1,14 L1,20 6,23 11,20 11,14 M6,17 L6,23 M18,11 L23,14 18,17 13,14 Z M13,14 L13,20 18,23 23,20 23,14 M18,17 L18,23"),
        [LauncherGlyph.Worlds] = Geometry.Parse("M12,2 A10,10 0 1 1 11.99,2 M14,7 L13,13 9,17 10,11 Z M12,2 L12,4 M12,20 L12,22 M2,12 L4,12 M20,12 L22,12"),
        [LauncherGlyph.Servers] = Geometry.Parse("M2,3 L22,3 22,21 2,21 Z M2,12 L22,12 M6,7 L8,7 M6,16 L8,16 M12,7 L18,7 M12,16 L18,16"),
        [LauncherGlyph.Workshop] = Geometry.Parse("M4,3 L10,3 17,10 14,13 7,6 Z M16,9 L20,13 18,15 14,11 M14,12 L4,22 2,20 12,10"),
        [LauncherGlyph.Settings] = CreateSettingsShape(),
        [LauncherGlyph.ChevronDown] = Geometry.Parse("M6,9 L12,15 18,9"),
        [LauncherGlyph.ChevronUp] = Geometry.Parse("M6,15 L12,9 18,15"),
        [LauncherGlyph.Minimize] = Geometry.Parse("M4,12 L20,12"),
        [LauncherGlyph.Close] = Geometry.Parse("M6,6 L18,18 M18,6 L6,18")
    };

    protected override void OnRender(DrawingContext dc)
    {
        var side = Math.Min(ActualWidth, ActualHeight);
        if (side <= 0) return;
        dc.PushTransform(new TranslateTransform((ActualWidth - side) / 2, (ActualHeight - side) / 2));
        dc.PushTransform(new ScaleTransform(side / 24, side / 24));
        dc.DrawGeometry(Kind is LauncherGlyph.Play or LauncherGlyph.Mods or LauncherGlyph.Settings ? Stroke : null, Kind == LauncherGlyph.Settings ? null : new Pen(Stroke, 1.8) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Miter }, Shapes[Kind]);
        dc.Pop();
        dc.Pop();
    }

    private static Geometry CreateSettingsShape()
    {
        var figure = new PathFigure { IsClosed = true };
        for (var i = 0; i < 64; i++)
        {
            var angle = (i - 0.5) * Math.PI / 32;
            var radius = i % 8 is 1 or 2 or 3 or 4 ? 11d : 8.5d;
            var point = new Point(12 + Math.Cos(angle) * radius, 12 + Math.Sin(angle) * radius);
            if (i == 0) figure.StartPoint = point; else figure.Segments.Add(new LineSegment(point, true));
        }
        var gear = new CombinedGeometry(GeometryCombineMode.Exclude, new PathGeometry(new[] { figure }), new EllipseGeometry(new Point(12,12), 3.5, 3.5));
        gear.Freeze(); return gear;
    }
}
