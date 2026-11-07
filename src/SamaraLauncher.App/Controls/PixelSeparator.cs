using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace SamaraLauncher.App.Controls;

/// <summary>
/// Тонкий разделитель с пиксельными зазубринами. Может быть горизонтальным или вертикальным.
/// </summary>
public class PixelSeparator : FrameworkElement
{
    private static readonly SolidColorBrush DefaultNotch =
        PixelPalette.CreateBrush(Color.FromArgb(80, 0x94, 0xA0, 0xAC));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation),
        typeof(Orientation),
        typeof(PixelSeparator),
        new FrameworkPropertyMetadata(
            Orientation.Horizontal,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush),
        typeof(MediaBrush),
        typeof(PixelSeparator),
        new FrameworkPropertyMetadata(PixelPalette.Line, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NotchBrushProperty = DependencyProperty.Register(
        nameof(NotchBrush),
        typeof(MediaBrush),
        typeof(PixelSeparator),
        new FrameworkPropertyMetadata(DefaultNotch, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty NotchSpacingProperty = DependencyProperty.Register(
        nameof(NotchSpacing),
        typeof(double),
        typeof(PixelSeparator),
        new FrameworkPropertyMetadata(
            32d,
            FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double spacing && spacing > 0d && !double.IsNaN(spacing) && !double.IsInfinity(spacing));

    public PixelSeparator()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public Orientation Orientation
    {
        get => (Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public MediaBrush LineBrush
    {
        get => (MediaBrush)GetValue(LineBrushProperty);
        set => SetValue(LineBrushProperty, value);
    }

    public MediaBrush NotchBrush
    {
        get => (MediaBrush)GetValue(NotchBrushProperty);
        set => SetValue(NotchBrushProperty, value);
    }

    public double NotchSpacing
    {
        get => (double)GetValue(NotchSpacingProperty);
        set => SetValue(NotchSpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Orientation == Orientation.Horizontal)
        {
            double width = double.IsInfinity(availableSize.Width) ? 120d : availableSize.Width;
            return new Size(Math.Max(0d, width), 3d);
        }

        double height = double.IsInfinity(availableSize.Height) ? 60d : availableSize.Height;
        return new Size(3d, Math.Max(0d, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0d || height <= 0d)
        {
            return;
        }

        if (Orientation == Orientation.Horizontal)
        {
            double y = Math.Max(0d, Math.Floor((height - 1d) / 2d));
            drawingContext.DrawRectangle(LineBrush, null, new Rect(0d, y, width, 1d));

            for (double x = NotchSpacing; x < width - 1d; x += NotchSpacing)
            {
                drawingContext.DrawRectangle(
                    NotchBrush,
                    null,
                    new Rect(Math.Floor(x), Math.Max(0d, y - 1d), 3d, 3d));
            }
        }
        else
        {
            double x = Math.Max(0d, Math.Floor((width - 1d) / 2d));
            drawingContext.DrawRectangle(LineBrush, null, new Rect(x, 0d, 1d, height));

            for (double y = NotchSpacing; y < height - 1d; y += NotchSpacing)
            {
                drawingContext.DrawRectangle(
                    NotchBrush,
                    null,
                    new Rect(Math.Max(0d, x - 1d), Math.Floor(y), 3d, 3d));
            }
        }
    }
}
