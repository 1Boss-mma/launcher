using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace SamaraLauncher.App.Controls;

/// <summary>
/// Пиксельная панель с рамкой 2 px и срезанными уголками. Контент размещается с отступом 2 px.
/// </summary>
public class PixelFrame : ContentControl
{
    private const double ContentPadding = 2d;

    public static readonly DependencyProperty CutProperty = DependencyProperty.Register(
        nameof(Cut),
        typeof(double),
        typeof(PixelFrame),
        new FrameworkPropertyMetadata(
            6d,
            FrameworkPropertyMetadataOptions.AffectsRender,
            (dependencyObject, _) => ((PixelFrame)dependencyObject).UpdateClip()),
        value => value is double cut && cut >= 0d && !double.IsNaN(cut) && !double.IsInfinity(cut));

    public static readonly DependencyProperty FrameBrushProperty = DependencyProperty.Register(
        nameof(FrameBrush),
        typeof(MediaBrush),
        typeof(PixelFrame),
        new FrameworkPropertyMetadata(PixelPalette.Line, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PanelBrushProperty = DependencyProperty.Register(
        nameof(PanelBrush),
        typeof(MediaBrush),
        typeof(PixelFrame),
        new FrameworkPropertyMetadata(PixelPalette.Panel, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty InsetBrushProperty = DependencyProperty.Register(
        nameof(InsetBrush),
        typeof(MediaBrush),
        typeof(PixelFrame),
        new FrameworkPropertyMetadata(PixelPalette.Panel2, FrameworkPropertyMetadataOptions.AffectsRender));

    public PixelFrame()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.MarginProperty, new Thickness(ContentPadding));
        Template = new ControlTemplate(typeof(PixelFrame)) { VisualTree = presenter };
    }

    public double Cut
    {
        get => (double)GetValue(CutProperty);
        set => SetValue(CutProperty, value);
    }

    public MediaBrush FrameBrush
    {
        get => (MediaBrush)GetValue(FrameBrushProperty);
        set => SetValue(FrameBrushProperty, value);
    }

    public MediaBrush PanelBrush
    {
        get => (MediaBrush)GetValue(PanelBrushProperty);
        set => SetValue(PanelBrushProperty, value);
    }

    public MediaBrush InsetBrush
    {
        get => (MediaBrush)GetValue(InsetBrushProperty);
        set => SetValue(InsetBrushProperty, value);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateClip();
    }

    private void UpdateClip()
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0d || height <= 0d)
        {
            Clip = null;
            return;
        }

        double cut = Math.Min(Cut, Math.Min(width, height) / 2d);
        Clip = PixelChrome.Chamfer(new Rect(0d, 0d, width, height), cut);
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

        double cut = Math.Min(Cut, Math.Min(width, height) / 2d);
        var rect = new Rect(1d, 1d, width - 2d, height - 2d);

        PixelChrome.DrawChamfer(drawingContext, rect, cut, PanelBrush, FrameBrush, 2d);

        var insetRect = new Rect(3d, 3d, width - 6d, height - 6d);
        if (insetRect.Width > 2d && insetRect.Height > 2d)
        {
            PixelChrome.DrawChamfer(
                drawingContext,
                insetRect,
                Math.Max(0d, cut - 2d),
                null,
                InsetBrush,
                1d);
        }

        // Тонкий пиксельный блик по верхней кромке.
        double highlightLeft = rect.Left + cut + 3d;
        double highlightRight = rect.Right - cut - 3d;
        if (highlightRight > highlightLeft)
        {
            drawingContext.DrawRectangle(
                PixelPalette.CreateBrush(Color.FromArgb(28, 255, 255, 255)),
                null,
                new Rect(highlightLeft, rect.Top + 3d, highlightRight - highlightLeft, 1d));
        }
    }
}
