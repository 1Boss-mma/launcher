using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace SamaraLauncher.App.Controls;

public enum PixelButtonVariant
{
    Primary,
    Secondary,
    Ghost,
    Danger
}

/// <summary>
/// Кнопка в стиле пиксельного GUI. Полностью рисуется в OnRender, шаблон темы не используется.
/// </summary>
public class PixelButton : Button
{
    public static readonly DependencyProperty ReferenceChromeProperty = DependencyProperty.Register(nameof(ReferenceChrome), typeof(bool), typeof(PixelButton), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool ReferenceChrome { get => (bool)GetValue(ReferenceChromeProperty); set => SetValue(ReferenceChromeProperty, value); }
    public static readonly DependencyProperty FlatProperty = DependencyProperty.Register(nameof(Flat), typeof(bool), typeof(PixelButton), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Flat { get => (bool)GetValue(FlatProperty); set => SetValue(FlatProperty, value); }

    private const double IconTextGap = 8d;

    public static readonly DependencyProperty VariantProperty = DependencyProperty.Register(
        nameof(Variant),
        typeof(PixelButtonVariant),
        typeof(PixelButton),
        new FrameworkPropertyMetadata(PixelButtonVariant.Primary, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconKindProperty = DependencyProperty.Register(
        nameof(IconKind),
        typeof(PixelIconKind?),
        typeof(PixelButton),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize),
        typeof(double),
        typeof(PixelButton),
        new FrameworkPropertyMetadata(
            18d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double size && size > 0d && !double.IsNaN(size) && !double.IsInfinity(size));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(PixelButton),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public PixelButton()
    {
        Template = null;
        Background = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        Padding = new Thickness(14d, 8d, 14d, 8d);
        MinHeight = 36d;
        Height = 40d;
        FontFamily = new FontFamily("Bahnschrift");
        FontWeight = FontWeights.SemiBold;
        FontSize = 14d;
        Foreground = PixelPalette.Text;
        Cursor = Cursors.Hand;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        Focusable = true;
        SetResourceReference(FocusVisualStyleProperty, "WorkspaceFocus");
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        IsEnabledChanged += (_, _) => InvalidateVisual();
    }

    public PixelButtonVariant Variant
    {
        get => (PixelButtonVariant)GetValue(VariantProperty);
        set => SetValue(VariantProperty, value);
    }

    public PixelIconKind? IconKind
    {
        get => (PixelIconKind?)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>Текст кнопки. Если пуст, используется Content, приведённый к строке.</summary>
    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        string? label = ResolveText();
        double iconWidth = IconKind.HasValue ? IconSize : 0d;
        double textWidth = 0d;
        double textHeight = 0d;

        if (!string.IsNullOrEmpty(label))
        {
            FormattedText formatted = CreateText(label, PixelPalette.Text);
            textWidth = formatted.WidthIncludingTrailingWhitespace;
            textHeight = formatted.Height;
        }

        double gap = iconWidth > 0d && textWidth > 0d ? IconTextGap : 0d;
        double width = Padding.Left + iconWidth + gap + textWidth + Padding.Right;
        double height = Math.Max(MinHeight, textHeight + Padding.Top + Padding.Bottom);

        return new Size(
            double.IsInfinity(width) ? 0d : width,
            double.IsInfinity(height) ? MinHeight : height);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);
        InvalidateMeasure();
        InvalidateVisual();
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
        bool pressed = IsPressed && enabled;

        ResolveColors(out Color fillColor, out Color borderColor, out Color textColor);

        double pressOffset = pressed ? 1d : 0d;
        var face = new Rect(1d, 1d + pressOffset, width - 2d, height - 3d - pressOffset);
        if (face.Width <= 2d || face.Height <= 2d)
        {
            return;
        }

        double cut = Math.Min(7d, Math.Min(face.Width, face.Height) / 2d);

        if (ReferenceChrome)
        {
            var top = Variant == PixelButtonVariant.Primary ? Color.FromRgb(51, 111, 34) : fillColor;
            var bottom = Variant == PixelButtonVariant.Primary ? Color.FromRgb(42, 91, 29) : fillColor;
            if (!enabled) { top = fillColor; bottom = fillColor; }
            if (IsMouseOver && enabled && Variant == PixelButtonVariant.Primary) { top = Color.FromRgb(65, 130, 43); }
            drawingContext.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), null, new Rect(face.X, face.Y + 2, face.Width, face.Height), 10, 10);
            drawingContext.DrawRoundedRectangle(new LinearGradientBrush(top, bottom, 90), new Pen(PixelPalette.CreateBrush(Color.FromRgb(37, 111, 46)), 2), face, 10, 10);
        }
        else if (Flat)
        {
            cut = 0;
            drawingContext.DrawRectangle(PixelPalette.CreateBrush(fillColor), new Pen(PixelPalette.CreateBrush(borderColor), 1d), face);
        }
        else
        {
        // Пиксельная тень под кнопкой.
        var shadowRect = new Rect(face.X, face.Y + 2d, face.Width, face.Height);
        PixelChrome.DrawChamfer(
            drawingContext,
            shadowRect,
            cut,
            PixelPalette.CreateBrush(Color.FromArgb(90, 0, 0, 0)),
            null);

        MediaBrush fill = Variant == PixelButtonVariant.Ghost && !enabled
            ? Brushes.Transparent
            : PixelPalette.CreateBrush(fillColor);

        PixelChrome.DrawChamfer(
            drawingContext,
            face,
            cut,
            fill,
            PixelPalette.CreateBrush(borderColor),
            2d);

        // Верхний inset-блик.
        double highlightLeft = face.Left + cut + 2d;
        double highlightRight = face.Right - cut - 2d;
        if (highlightRight > highlightLeft && face.Height > 8d)
        {
            Color highlight = enabled
                ? (Variant == PixelButtonVariant.Ghost
                    ? Color.FromArgb(24, 255, 255, 255)
                    : Color.FromArgb(70, 255, 255, 255))
                : Color.FromArgb(12, 255, 255, 255);

            drawingContext.DrawRectangle(
                PixelPalette.CreateBrush(highlight),
                null,
                new Rect(highlightLeft, face.Top + 2d, highlightRight - highlightLeft, 1d));
        }

        // Тень снизу внутри рамки.
        if (highlightRight > highlightLeft && face.Height > 8d)
        {
            drawingContext.DrawRectangle(
                PixelPalette.CreateBrush(Color.FromArgb(70, 0, 0, 0)),
                null,
                new Rect(highlightLeft, face.Bottom - 2d, highlightRight - highlightLeft, 1d));
        }

        }

        MediaBrush textBrush = PixelPalette.CreateBrush(textColor);
        string? label = ResolveText();
        bool hasText = !string.IsNullOrEmpty(label);
        double iconSide = IconKind.HasValue
            ? Math.Max(8d, Math.Min(IconSize, face.Height - 10d))
            : 0d;

        FormattedText? formatted = hasText ? CreateText(label!, textBrush) : null;
        double textWidth = formatted?.WidthIncludingTrailingWhitespace ?? 0d;
        double gap = iconSide > 0d && textWidth > 0d ? IconTextGap : 0d;
        double totalWidth = iconSide + gap + textWidth;
        double startX = face.Left + (face.Width - totalWidth) / 2d;

        if (iconSide > 0d && IconKind is PixelIconKind kind)
        {
            double iconTop = face.Top + (face.Height - iconSide) / 2d;
            PixelIcon.Draw(drawingContext, kind, textBrush, iconSide / 10d, new Point(startX, iconTop));
        }

        if (formatted is not null)
        {
            double textLeft = startX + iconSide + gap;
            double textTop = face.Top + (face.Height - formatted.Height) / 2d;
            double maxWidth = face.Right - 8d - textLeft;
            if (maxWidth > 0d)
            {
                formatted.MaxTextWidth = maxWidth;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
                drawingContext.DrawText(formatted, new Point(textLeft, textTop));
            }
        }

        if (IsKeyboardFocused && enabled)
        {
            var focusRect = new Rect(
                face.Left + 4d,
                face.Top + 4d,
                Math.Max(0d, face.Width - 8d),
                Math.Max(0d, face.Height - 8d));

            if (focusRect.Width > 4d && focusRect.Height > 4d)
            {
                PixelChrome.DrawChamfer(
                    drawingContext,
                    focusRect,
                    Math.Max(0d, cut - 2d),
                    null,
                    PixelPalette.CreateBrush(Color.FromArgb(150, 0x58, 0xC7, 0xF0)),
                    1d);
            }
        }
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

    private string? ResolveText()
    {
        if (!string.IsNullOrEmpty(Text))
        {
            return Text;
        }

        return Content as string;
    }

    private FormattedText CreateText(string text, MediaBrush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private void ResolveColors(out Color fill, out Color border, out Color text)
    {
        bool enabled = IsEnabled;
        bool hover = IsMouseOver && enabled;
        bool pressed = IsPressed && enabled;

        if (!enabled)
        {
            fill = Variant == PixelButtonVariant.Ghost ? Colors.Transparent : PixelPalette.Panel2Color;
            border = PixelPalette.LineColor;
            text = PixelPalette.MutedColor;
            return;
        }

        switch (Variant)
        {
            case PixelButtonVariant.Secondary:
                fill = pressed
                    ? PixelPalette.PanelColor
                    : hover
                        ? PixelPalette.Panel2Color
                        : PixelPalette.Panel2Color;
                border = PixelPalette.LineColor;
                text = Variant == PixelButtonVariant.Secondary ? PixelPalette.TextColor : ((SolidColorBrush)Application.Current.FindResource("AccentTextBrush")).Color;
                break;

            case PixelButtonVariant.Ghost:
                fill = pressed
                    ? Color.FromArgb(36, 255, 255, 255)
                    : hover
                        ? Color.FromArgb(18, 255, 255, 255)
                        : Colors.Transparent;
                border = hover
                    ? Color.FromArgb(120, 0x39, 0x41, 0x4B)
                    : Color.FromArgb(70, 0x39, 0x41, 0x4B);
                text = PixelPalette.MutedColor;
                break;

            case PixelButtonVariant.Danger:
                fill = pressed
                    ? PixelPalette.Darken(PixelPalette.RedColor, 0.16)
                    : hover
                        ? PixelPalette.Lighten(PixelPalette.RedColor, 0.12)
                        : PixelPalette.RedColor;
                border = PixelPalette.LineColor;
                text = Variant == PixelButtonVariant.Secondary ? PixelPalette.TextColor : ((SolidColorBrush)Application.Current.FindResource("AccentTextBrush")).Color;
                break;

            default:
                fill = pressed
                    ? PixelPalette.GreenHiColor
                    : hover
                        ? PixelPalette.GreenColor
                        : PixelPalette.GreenLoColor;
                border = PixelPalette.LineColor;
                text = Variant == PixelButtonVariant.Secondary ? PixelPalette.TextColor : ((SolidColorBrush)Application.Current.FindResource("AccentTextBrush")).Color;
                break;
        }
    }
}
