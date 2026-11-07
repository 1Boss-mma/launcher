using System;
using System.Windows;
using System.Windows.Media;

namespace SamaraLauncher.App.Controls;

/// <summary>
/// Пиксельный аватар 8x8. Цвета кожи, волос и глаз детерминированно зависят от имени.
/// </summary>
public class PixelAvatar : FrameworkElement
{
    private static readonly string[] Face =
    {
        "HHHHHHHH",
        "HhhHHhhH",
        "HSSSSSSH",
        "HSWSSWSH",
        "HSISSISH",
        "HSSSNSSH",
        "HSSMMSSH",
        "HHSSSSHH"
    };

    private static readonly Color[] SkinColors =
    {
        Color.FromRgb(0xF2, 0xC9, 0xA0),
        Color.FromRgb(0xE6, 0xB1, 0x7E),
        Color.FromRgb(0xD7, 0x9A, 0x63),
        Color.FromRgb(0xB9, 0x7A, 0x4E),
        Color.FromRgb(0x8D, 0x5A, 0x3B),
        Color.FromRgb(0x6B, 0x41, 0x28)
    };

    private static readonly Color[] HairColors =
    {
        Color.FromRgb(0x2B, 0x1B, 0x12),
        Color.FromRgb(0x3E, 0x2A, 0x1A),
        Color.FromRgb(0x5A, 0x3A, 0x22),
        Color.FromRgb(0x7A, 0x4B, 0x2A),
        Color.FromRgb(0x14, 0x14, 0x14),
        Color.FromRgb(0xC9, 0xA1, 0x5C)
    };

    private static readonly Color[] EyeColors =
    {
        Color.FromRgb(0x58, 0xC7, 0xF0),
        Color.FromRgb(0x4E, 0x7E, 0xDB),
        Color.FromRgb(0x5F, 0xBE, 0x4A),
        Color.FromRgb(0x6B, 0x4A, 0x2F),
        Color.FromRgb(0x8F, 0x5B, 0xB8)
    };

    public static readonly DependencyProperty UsernameProperty = DependencyProperty.Register(
        nameof(Username),
        typeof(string),
        typeof(PixelAvatar),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public PixelAvatar()
    {
        Width = 40d;
        Height = 40d;
        MinWidth = 16d;
        MinHeight = 16d;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public string? Username
    {
        get => (string?)GetValue(UsernameProperty);
        set => SetValue(UsernameProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = 40d;
        if (!double.IsInfinity(availableSize.Width))
        {
            side = Math.Min(side, availableSize.Width);
        }

        if (!double.IsInfinity(availableSize.Height))
        {
            side = Math.Min(side, availableSize.Height);
        }

        side = Math.Max(16d, side);
        return new Size(side, side);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        double side = Math.Min(width, height);
        if (side < 8d)
        {
            return;
        }

        double left = Math.Floor((width - side) / 2d);
        double top = Math.Floor((height - side) / 2d);
        var frameRect = new Rect(left + 1d, top + 1d, side - 2d, side - 2d);

        PixelChrome.DrawChamfer(drawingContext, frameRect, 3d, PixelPalette.BgDark, PixelPalette.Line, 2d);

        uint hash = Hash(Username);
        Color skin = SkinColors[hash % (uint)SkinColors.Length];
        Color hair = HairColors[(hash >> 5) % (uint)HairColors.Length];
        Color hairHi = PixelPalette.Lighten(hair, 0.16d);
        Color eye = EyeColors[(hash >> 11) % (uint)EyeColors.Length];
        Color mouth = PixelPalette.Darken(skin, 0.48d);
        Color nose = PixelPalette.Darken(skin, 0.2d);

        SolidColorBrush skinBrush = PixelPalette.CreateBrush(skin);
        SolidColorBrush hairBrush = PixelPalette.CreateBrush(hair);
        SolidColorBrush hairHiBrush = PixelPalette.CreateBrush(hairHi);
        SolidColorBrush eyeBrush = PixelPalette.CreateBrush(eye);
        SolidColorBrush whiteBrush = PixelPalette.CreateBrush(Color.FromRgb(0xF4, 0xF7, 0xFA));
        SolidColorBrush mouthBrush = PixelPalette.CreateBrush(mouth);
        SolidColorBrush noseBrush = PixelPalette.CreateBrush(nose);

        double inner = side - 8d;
        int cell = (int)Math.Floor(inner / 8d);
        if (cell < 1)
        {
            cell = 1;
        }

        double faceSize = cell * 8d;
        double faceLeft = Math.Round(left + (side - faceSize) / 2d);
        double faceTop = Math.Round(top + (side - faceSize) / 2d);

        for (int row = 0; row < 8; row++)
        {
            string line = Face[row];
            for (int column = 0; column < 8; column++)
            {
                SolidColorBrush brush;
                switch (line[column])
                {
                    case 'H':
                        brush = hairBrush;
                        break;
                    case 'h':
                        brush = hairHiBrush;
                        break;
                    case 'W':
                        brush = whiteBrush;
                        break;
                    case 'I':
                        brush = eyeBrush;
                        break;
                    case 'M':
                        brush = mouthBrush;
                        break;
                    case 'N':
                        brush = noseBrush;
                        break;
                    default:
                        brush = skinBrush;
                        break;
                }

                drawingContext.DrawRectangle(
                    brush,
                    null,
                    new Rect(faceLeft + column * cell, faceTop + row * cell, cell, cell));
            }
        }
    }

    private static uint Hash(string? value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;

        uint hash = offset;
        if (string.IsNullOrEmpty(value))
        {
            return offset;
        }

        foreach (char c in value)
        {
            hash ^= c;
            hash *= prime;
        }

        return hash;
    }
}
