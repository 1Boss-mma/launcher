using System;
using System.Windows;
using System.Windows.Media;

namespace SamaraLauncher.App.Controls;

/// <summary>
/// Пиксельный блок травы в стиле Minecraft: зелёная верхняя грань, земляные бока
/// и тёмная обводка. Рисуется прямоугольниками без сглаживания и без картинок.
/// </summary>
public class PixelBlock : FrameworkElement
{
    private const int Grid = 16;

    // Карта 16x16: o — обводка, g/h/d — трава, e/f — кромка травы на боках,
    // b/l/k/K — земля от светлой к самой тёмной. Точка — прозрачный пиксель.
    private static readonly string[] Map =
    {
        "................",
        ".......oo.......",
        "......ohgo......",
        "....ohggdggo....",
        "..oghggdggdhgo..",
        ".ogghgdggdhgggo.",
        ".oeghgdggdhggfo.",
        ".oeefgdgghgfeeo.",
        ".obkbefghfebkbo.",
        ".oblbkbKKkbbkko.",
        ".obkblkKKkbkbko.",
        ".oblbkbKKkbbkko.",
        "...oblkKKkbko...",
        ".....okKKko.....",
        "......oooo......",
        "................"
    };

    private static readonly SolidColorBrush OutlineBrush =
        PixelPalette.CreateBrush(Color.FromRgb(0x0F, 0x13, 0x0E));

    private static readonly SolidColorBrush GrassBrush = PixelPalette.Green;
    private static readonly SolidColorBrush GrassHiBrush = PixelPalette.GreenHi;

    private static readonly SolidColorBrush GrassDarkBrush =
        PixelPalette.CreateBrush(Color.FromRgb(0x47, 0x9B, 0x38));

    private static readonly SolidColorBrush FringeBrush =
        PixelPalette.CreateBrush(Color.FromRgb(0x4F, 0xA2, 0x3C));

    private static readonly SolidColorBrush FringeDarkBrush =
        PixelPalette.CreateBrush(Color.FromRgb(0x38, 0x7B, 0x2B));

    private static readonly SolidColorBrush DirtBrush =
        PixelPalette.CreateBrush(Color.FromRgb(0x8A, 0x5A, 0x36));

    private static readonly SolidColorBrush DirtLightBrush =
        PixelPalette.CreateBrush(Color.FromRgb(0xA9, 0x74, 0x49));

    private static readonly SolidColorBrush DirtDarkBrush =
        PixelPalette.CreateBrush(Color.FromRgb(0x6A, 0x42, 0x26));

    private static readonly SolidColorBrush DirtDeepBrush =
        PixelPalette.CreateBrush(Color.FromRgb(0x4E, 0x2F, 0x1A));

    public static readonly DependencyProperty BlockSizeProperty = DependencyProperty.Register(
        nameof(BlockSize),
        typeof(double),
        typeof(PixelBlock),
        new FrameworkPropertyMetadata(
            44d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double size && size > 0d && !double.IsNaN(size) && !double.IsInfinity(size));

    public PixelBlock()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public double BlockSize
    {
        get => (double)GetValue(BlockSizeProperty);
        set => SetValue(BlockSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = BlockSize;

        if (!double.IsInfinity(availableSize.Width))
        {
            side = Math.Min(side, Math.Max(0d, availableSize.Width));
        }

        if (!double.IsInfinity(availableSize.Height))
        {
            side = Math.Min(side, Math.Max(0d, availableSize.Height));
        }

        return new Size(side, side);
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

        double side = Math.Min(width, height);
        double art = side * 0.92d;
        double cell = art / Grid;
        double originX = (width - art) / 2d;
        double originY = (height - art) / 2d;
        if (cell <= 0d)
        {
            return;
        }

        for (int y = 0; y < Grid; y++)
        {
            string row = Map[y];
            for (int x = 0; x < Grid; x++)
            {
                SolidColorBrush? brush = BrushFor(row[x]);
                if (brush is null)
                {
                    continue;
                }

                double left = Snap(originX + x * cell);
                double top = Snap(originY + y * cell);
                double right = Snap(originX + (x + 1) * cell);
                double bottom = Snap(originY + (y + 1) * cell);
                if (right <= left || bottom <= top)
                {
                    continue;
                }

                drawingContext.DrawRectangle(brush, null, new Rect(left, top, right - left, bottom - top));
            }
        }
    }

    private static double Snap(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private static SolidColorBrush? BrushFor(char symbol) => symbol switch
    {
        'o' => OutlineBrush,
        'g' => GrassBrush,
        'h' => GrassHiBrush,
        'd' => GrassDarkBrush,
        'e' => FringeBrush,
        'f' => FringeDarkBrush,
        'b' => DirtBrush,
        'l' => DirtLightBrush,
        'k' => DirtDarkBrush,
        'K' => DirtDeepBrush,
        _ => null
    };
}
