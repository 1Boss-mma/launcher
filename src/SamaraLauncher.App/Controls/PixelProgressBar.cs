using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace SamaraLauncher.App.Controls;

/// <summary>
/// Полоса прогресса / XP в пиксельном стиле. Без анимации, значение 0..100.
/// </summary>
public class PixelProgressBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(
            0d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            null,
            (_, baseValue) =>
            {
                double value = (double)baseValue;
                return double.IsNaN(value) || double.IsInfinity(value)
                    ? 0d
                    : Math.Clamp(value, 0d, 100d);
            }));

    public static readonly DependencyProperty ShowTextProperty = DependencyProperty.Register(
        nameof(ShowText),
        typeof(bool),
        typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(
        nameof(BarHeight),
        typeof(double),
        typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(
            16d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double height && height >= 4d && !double.IsNaN(height) && !double.IsInfinity(height));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level),
        typeof(int),
        typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush),
        typeof(MediaBrush),
        typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(PixelPalette.Xp, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty HighlightBrushProperty = DependencyProperty.Register(
        nameof(HighlightBrush),
        typeof(MediaBrush),
        typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(PixelPalette.XpHi, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush),
        typeof(MediaBrush),
        typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(PixelPalette.BgDark, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FrameBrushProperty = DependencyProperty.Register(
        nameof(FrameBrush),
        typeof(MediaBrush),
        typeof(PixelProgressBar),
        new FrameworkPropertyMetadata(PixelPalette.Line, FrameworkPropertyMetadataOptions.AffectsRender));

    public PixelProgressBar()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    /// <summary>Значение от 0 до 100.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public bool ShowText
    {
        get => (bool)GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    public double BarHeight
    {
        get => (double)GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    /// <summary>Необязательный уровень. Если больше нуля, вместо процентов показывается «УР. N».</summary>
    public int Level
    {
        get => (int)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public MediaBrush BarBrush
    {
        get => (MediaBrush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public MediaBrush HighlightBrush
    {
        get => (MediaBrush)GetValue(HighlightBrushProperty);
        set => SetValue(HighlightBrushProperty, value);
    }

    public MediaBrush TrackBrush
    {
        get => (MediaBrush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public MediaBrush FrameBrush
    {
        get => (MediaBrush)GetValue(FrameBrushProperty);
        set => SetValue(FrameBrushProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 200d : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? BarHeight : Math.Min(BarHeight, availableSize.Height);
        return new Size(Math.Max(0d, width), Math.Max(0d, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 4d || height <= 4d)
        {
            return;
        }

        var trackRect = new Rect(1d, 1d, width - 2d, height - 2d);
        double cut = Math.Min(4d, Math.Min(trackRect.Width, trackRect.Height) / 2d);
        PixelChrome.DrawChamfer(drawingContext, trackRect, cut, TrackBrush, FrameBrush, 2d);

        var inner = new Rect(2d, 2d, width - 4d, height - 4d);
        if (inner.Width <= 2d || inner.Height <= 2d)
        {
            return;
        }

        StreamGeometry clip = PixelChrome.Chamfer(inner, Math.Max(0d, cut - 1d));
        drawingContext.PushClip(clip);

        double ratio = Math.Clamp(Value / 100d, 0d, 1d);
        double fillWidth = inner.Width * ratio;

        if (fillWidth > 0.5d)
        {
            drawingContext.DrawRectangle(
                BarBrush,
                null,
                new Rect(inner.Left, inner.Top, fillWidth, inner.Height));

            if (inner.Height > 5d)
            {
                drawingContext.DrawRectangle(
                    HighlightBrush,
                    null,
                    new Rect(inner.Left, inner.Top + 1d, fillWidth, 2d));

                drawingContext.DrawRectangle(
                    PixelPalette.CreateBrush(Color.FromArgb(90, 0, 0, 0)),
                    null,
                    new Rect(inner.Left, inner.Bottom - 2d, fillWidth, 1d));
            }
        }

        // Сегменты примерно через 8%.
        if (inner.Width > 40d)
        {
            MediaBrush segmentBrush = PixelPalette.CreateBrush(Color.FromArgb(120, 0x05, 0x07, 0x0A));
            for (int i = 1; i < 12; i++)
            {
                double x = Math.Round(inner.Left + inner.Width * (i / 12d));
                drawingContext.DrawRectangle(segmentBrush, null, new Rect(x, inner.Top, 1d, inner.Height));
            }
        }

        drawingContext.Pop();

        if (ShowText && inner.Height >= 10d)
        {
            string label = Level > 0
                ? $"УР. {Level}"
                : $"{Math.Round(Value):0}%";

            double fontSize = Math.Clamp(Math.Min(BarHeight, height) - 6d, 8d, 12d);
            var typeface = new Typeface(
                new FontFamily("Bahnschrift"),
                FontStyles.Normal,
                FontWeights.SemiBold,
                FontStretches.Normal);

            var shadow = new FormattedText(
                label,
                CultureInfo.CurrentUICulture,
                FlowDirection,
                typeface,
                fontSize,
                PixelPalette.CreateBrush(Color.FromArgb(180, 0, 0, 0)),
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            var text = new FormattedText(
                label,
                CultureInfo.CurrentUICulture,
                FlowDirection,
                typeface,
                fontSize,
                PixelPalette.Text,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);

            double textLeft = inner.Left + (inner.Width - text.WidthIncludingTrailingWhitespace) / 2d;
            double textTop = inner.Top + (inner.Height - text.Height) / 2d;

            text.MaxTextWidth = Math.Max(0d, inner.Width);
            text.Trimming = TextTrimming.CharacterEllipsis;
            shadow.MaxTextWidth = text.MaxTextWidth;
            shadow.Trimming = TextTrimming.CharacterEllipsis;

            drawingContext.DrawText(shadow, new Point(textLeft + 1d, textTop + 1d));
            drawingContext.DrawText(text, new Point(textLeft, textTop));
        }
    }
}
