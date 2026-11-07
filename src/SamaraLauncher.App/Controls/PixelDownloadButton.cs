using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SamaraLauncher.App.Controls;

public sealed class PixelDownloadButton : Button
{
    public static readonly DependencyProperty IsWorkingProperty = DependencyProperty.Register(
        nameof(IsWorking), typeof(bool), typeof(PixelDownloadButton),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsInstalledProperty = DependencyProperty.Register(
        nameof(IsInstalled), typeof(bool), typeof(PixelDownloadButton),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(PixelDownloadButton),
        new FrameworkPropertyMetadata(0d, OnProgressChanged));
    private static readonly DependencyProperty RenderedProgressProperty = DependencyProperty.Register(
        "RenderedProgress", typeof(double), typeof(PixelDownloadButton),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public bool IsWorking { get => (bool)GetValue(IsWorkingProperty); set => SetValue(IsWorkingProperty, value); }
    public bool IsInstalled { get => (bool)GetValue(IsInstalledProperty); set => SetValue(IsInstalledProperty, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    private double RenderedProgress => (double)GetValue(RenderedProgressProperty);

    public PixelDownloadButton()
    {
        Template = null;
        Width = 140;
        Height = 30;
        HorizontalAlignment = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        FontFamily = new FontFamily("Bahnschrift");
        FontSize = 12;
        FontWeight = FontWeights.SemiBold;
        SnapsToDevicePixels = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        MouseEnter += (_, _) => InvalidateVisual();
        MouseLeave += (_, _) => InvalidateVisual();
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (PixelDownloadButton)d;
        var target = Math.Clamp((double)e.NewValue, 0, 1);
        if (target == 0)
        {
            button.BeginAnimation(RenderedProgressProperty, null);
            button.SetValue(RenderedProgressProperty, 0d);
            return;
        }
        var animation = new DoubleAnimation(button.RenderedProgress, target, TimeSpan.FromMilliseconds(180));
        Timeline.SetDesiredFrameRate(animation, 60);
        button.BeginAnimation(RenderedProgressProperty, animation);
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (ActualWidth < 16 || ActualHeight < 12) return;
        var rect = new Rect(1, 1, ActualWidth - 2, ActualHeight - 2);
        var green = Color.FromRgb(0x3C, 0x7D, 0x2F);
        var background = IsInstalled && !IsWorking ? PixelPalette.Panel2Color : green;
        if (!IsEnabled) background = PixelPalette.PanelColor;
        PixelChrome.DrawChamfer(dc, rect, 4, PixelPalette.CreateBrush(background), PixelPalette.CreateBrush(PixelPalette.LineColor), 2);
        if (IsWorking)
        {
            var width = Math.Max(0, rect.Width - 8) * Math.Clamp(RenderedProgress, 0, 1);
            if (width > 0) dc.DrawRectangle(PixelPalette.CreateBrush(Color.FromRgb(0x65, 0xB7, 0x50)), null,
                new Rect(rect.Left + 4, rect.Top + 3, width, Math.Max(1, rect.Height - 6)));
        }
        var label = IsWorking ? $"{(int)(Math.Clamp(RenderedProgress, 0, 1) * 100)}% · ОТМЕНА" : Content?.ToString() ?? "Установить";
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize,
            PixelPalette.CreateBrush(IsEnabled ? PixelPalette.TextColor : PixelPalette.MutedColor),
            VisualTreeHelper.GetDpi(this).PixelsPerDip)
        { MaxTextWidth = Math.Max(1, rect.Width - 8), Trimming = TextTrimming.CharacterEllipsis };
        dc.DrawText(text, new Point(rect.Left + (rect.Width - text.Width) / 2, rect.Top + (rect.Height - text.Height) / 2));
    }
}