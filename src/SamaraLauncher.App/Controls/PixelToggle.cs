using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MediaBrush = System.Windows.Media.Brush;

namespace SamaraLauncher.App.Controls;

/// <summary>
/// Пиксельный переключатель 64x26 с подписью ВКЛ/ВЫКЛ. Клик работает через ToggleButton.
/// </summary>
public class PixelToggle : ToggleButton
{
    public static readonly DependencyProperty BorderlessProperty = DependencyProperty.Register(
        nameof(Borderless), typeof(bool), typeof(PixelToggle), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Borderless { get => (bool)GetValue(BorderlessProperty); set => SetValue(BorderlessProperty, value); }
    public static readonly DependencyProperty ReferenceChromeProperty = DependencyProperty.Register(
        nameof(ReferenceChrome), typeof(bool), typeof(PixelToggle), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ReferenceChrome { get => (bool)GetValue(ReferenceChromeProperty); set => SetValue(ReferenceChromeProperty, value); }
    private static readonly DependencyProperty KnobPositionProperty = DependencyProperty.Register(
        "KnobPosition", typeof(double), typeof(PixelToggle),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private double KnobPosition
    {
        get => (double)GetValue(KnobPositionProperty);
        set => SetValue(KnobPositionProperty, value);
    }

    private void AnimateKnob()
    {
        var target = IsChecked == true ? 1d : 0d;
        var start = KnobPosition;
        BeginAnimation(KnobPositionProperty, null);
        KnobPosition = target;
        if (!IsLoaded || !IsVisible || Math.Abs(start - target) < 0.001d) return;
        var animation = new DoubleAnimation(start, target, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        Timeline.SetDesiredFrameRate(animation, 60);
        BeginAnimation(KnobPositionProperty, animation);
    }

    public PixelToggle()
    {
        Template = null;
        Width = 64d;
        Height = 26d;
        MinWidth = 40d;
        MinHeight = 18d;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        FontFamily = new FontFamily("Bahnschrift");
        FontWeight = FontWeights.SemiBold;
        FontSize = 10d;
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        IsEnabledChanged += (_, _) => InvalidateVisual();
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

        bool enabled = IsEnabled;
        bool isChecked = IsChecked == true;
        bool hover = IsMouseOver && enabled;
        bool pressed = IsPressed && enabled;

        if (ReferenceChrome)
        {
            RenderOptions.SetEdgeMode(this, EdgeMode.Unspecified);
            var track = new Rect(1, 2, Math.Max(0, width - 2), Math.Max(0, height - 4));
            var trackBrush = TryFindResource(isChecked ? "GreenBrush" : "BuildThumbBrush") as MediaBrush ?? PixelPalette.CreateBrush(PixelPalette.Panel2Color);
            drawingContext.PushOpacity(enabled ? (pressed ? 0.8 : 1) : 0.45);
            drawingContext.DrawRoundedRectangle(trackBrush, null, track, track.Height / 2, track.Height / 2);
            var diameter = Math.Max(4, track.Height - 4);
            var x = track.Left + 2 + Math.Clamp(KnobPosition, 0, 1) * Math.Max(0, track.Width - diameter - 4);
            drawingContext.DrawEllipse(Brushes.White, null, new Point(x + diameter / 2, track.Top + track.Height / 2), diameter / 2, diameter / 2);
            if (!Borderless && IsKeyboardFocused) drawingContext.DrawRoundedRectangle(null, new Pen(TryFindResource("SuccessBrush") as MediaBrush ?? Brushes.White, 1), new Rect(0.5, 0.5, width - 1, height - 1), height / 2, height / 2);
            drawingContext.Pop();
            return;
        }

        var rect = new Rect(1d, 1d, width - 2d, height - 2d);
        double cut = Math.Min(4d, Math.Min(rect.Width, rect.Height) / 2d);

        Color fillColor;
        Color borderColor;

        if (!enabled)
        {
            fillColor = PixelPalette.PanelColor;
            borderColor = Color.FromRgb(0x31, 0x3A, 0x44);
        }
        else if (isChecked)
        {
            fillColor = pressed ? PixelPalette.GreenHiColor : PixelPalette.GreenColor;
            borderColor = PixelPalette.LineColor;
        }
        else
        {
            fillColor = pressed
                ? Color.FromRgb(0x17, 0x1C, 0x23)
                : hover
                    ? Color.FromRgb(0x2E, 0x37, 0x42)
                    : PixelPalette.Panel2Color;
            borderColor = PixelPalette.LineColor;
        }

        PixelChrome.DrawChamfer(
            drawingContext,
            rect,
            cut,
            PixelPalette.CreateBrush(fillColor),
            Borderless ? null : PixelPalette.CreateBrush(borderColor),
            2d);

        // Пиксельный блик сверху дорожки.
        double hlLeft = rect.Left + cut + 2d;
        double hlRight = rect.Right - cut - 2d;
        if (hlRight > hlLeft)
        {
            drawingContext.DrawRectangle(
                PixelPalette.CreateBrush(Color.FromArgb(isChecked ? (byte)60 : (byte)22, 255, 255, 255)),
                null,
                new Rect(hlLeft, rect.Top + 2d, hlRight - hlLeft, 1d));
        }

        // Кноб.
        double knobHeight = Math.Max(10d, rect.Height - 8d);
        double knobWidth = Math.Max(14d, knobHeight);
        knobWidth = Math.Min(knobWidth, Math.Max(10d, rect.Width * 0.4d));
        double knobTop = rect.Top + (rect.Height - knobHeight) / 2d;
        double knobLeft = rect.Left + 4d + Math.Clamp(KnobPosition, 0d, 1d) *
            Math.Max(0d, rect.Width - 8d - knobWidth);

        var knobRect = new Rect(knobLeft, knobTop, knobWidth, knobHeight);
        double knobCut = Math.Min(3d, knobHeight / 4d);
        PixelChrome.DrawChamfer(
            drawingContext,
            knobRect,
            knobCut,
            PixelPalette.CreateBrush(enabled ? PixelPalette.TextColor : PixelPalette.MutedColor),
            Borderless ? null : PixelPalette.CreateBrush(PixelPalette.LineColor),
            2d);

        if (knobHeight > 8d)
        {
            drawingContext.DrawRectangle(
                PixelPalette.CreateBrush(Color.FromArgb(90, 255, 255, 255)),
                null,
                new Rect(knobRect.Left + 2d, knobRect.Top + 2d, Math.Max(0d, knobRect.Width - 4d), 1d));
        }

        // Подпись.
        if (rect.Width >= 44d)
        {
            double labelLeft = isChecked ? rect.Left + 4d : knobRect.Right + 2d;
            double labelRight = isChecked ? knobRect.Left - 2d : rect.Right - 4d;
            double labelWidth = labelRight - labelLeft;
            if (labelWidth > 12d)
            {
                string label = isChecked ? "ВКЛ" : "ВЫКЛ";
                MediaBrush labelBrush = PixelPalette.CreateBrush(
                    enabled ? PixelPalette.TextColor : PixelPalette.MutedColor);

                var formatted = new FormattedText(
                    label,
                    CultureInfo.CurrentUICulture,
                    FlowDirection,
                    new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
                    Math.Min(FontSize, Math.Max(7.5d, rect.Height - 13d)),
                    labelBrush,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip)
                {
                    MaxTextWidth = labelWidth,
                    Trimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center
                };

                double labelTop = rect.Top + (rect.Height - formatted.Height) / 2d;
                drawingContext.DrawText(formatted, new Point(labelLeft, labelTop));
            }
        }

        if (!Borderless && IsKeyboardFocused && enabled)
        {
            var focusRect = new Rect(
                rect.Left + 3d,
                rect.Top + 3d,
                Math.Max(0d, rect.Width - 6d),
                Math.Max(0d, rect.Height - 6d));

            if (focusRect.Width > 4d && focusRect.Height > 4d)
            {
                PixelChrome.DrawChamfer(
                    drawingContext,
                    focusRect,
                    Math.Max(0d, cut - 1d),
                    null,
                    PixelPalette.CreateBrush(Color.FromArgb(150, 0x58, 0xC7, 0xF0)),
                    1d);
            }
        }
    }

    protected override void OnChecked(RoutedEventArgs e)
    {
        base.OnChecked(e);
        AnimateKnob();
        InvalidateVisual();
    }

    protected override void OnUnchecked(RoutedEventArgs e)
    {
        base.OnUnchecked(e);
        AnimateKnob();
        InvalidateVisual();
    }

    protected override void OnIndeterminate(RoutedEventArgs e)
    {
        base.OnIndeterminate(e);
        InvalidateVisual();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        InvalidateVisual();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }
}
