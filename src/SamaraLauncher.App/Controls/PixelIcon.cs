using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using MediaBrush = System.Windows.Media.Brush;

namespace SamaraLauncher.App.Controls;

public enum PixelIconKind
{
    Home,
    Mods,
    Worlds,
    Logs,
    Settings,
    Play,
    Refresh,
    Shield,
    Crash,
    Folder,
    User,
    Plus,
    Check,
    Warning,
    Download,
    Clock,
    Memory,
    Java,
    Server,
    Close,
    Minimize,
    Maximize,
    Back,
    Search,
    Trash,
    Power,
    News,
    Backup,
    Info
}

/// <summary>
/// Пиксельная иконка 10x10, нарисованная прямоугольниками. Шрифты и картинки не используются.
/// </summary>
public sealed class PixelIcon : FrameworkElement
{
    private const int Grid = 10;

    private static readonly IReadOnlyDictionary<PixelIconKind, string[]> Maps = BuildMaps();

    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind),
        typeof(PixelIconKind),
        typeof(PixelIcon),
        new FrameworkPropertyMetadata(
            PixelIconKind.Home,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush),
        typeof(MediaBrush),
        typeof(PixelIcon),
        new FrameworkPropertyMetadata(
            PixelPalette.Text,
            FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty PixelSizeProperty = DependencyProperty.Register(
        nameof(PixelSize),
        typeof(double),
        typeof(PixelIcon),
        new FrameworkPropertyMetadata(
            2d,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender),
        value => value is double size && size > 0d && !double.IsNaN(size) && !double.IsInfinity(size));

    public PixelIcon()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public PixelIconKind Kind
    {
        get => (PixelIconKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public MediaBrush Brush
    {
        get => (MediaBrush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public double PixelSize
    {
        get => (double)GetValue(PixelSizeProperty);
        set => SetValue(PixelSizeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double side = Grid * PixelSize;
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

        double cell = Math.Min(PixelSize, Math.Min(width, height) / Grid);
        if (cell <= 0d)
        {
            return;
        }

        double iconSize = cell * Grid;
        double left = Math.Round((width - iconSize) / 2d);
        double top = Math.Round((height - iconSize) / 2d);
        Draw(drawingContext, Kind, Brush, cell, new Point(left, top));
    }

    /// <summary>Рисует иконку в указанной точке. Используется кнопками и другими контролами.</summary>
    internal static void Draw(
        DrawingContext drawingContext,
        PixelIconKind kind,
        MediaBrush brush,
        double pixelSize,
        Point origin)
    {
        if (pixelSize <= 0d || brush is null)
        {
            return;
        }

        string[] map = Maps[kind];
        for (int row = 0; row < map.Length; row++)
        {
            string line = map[row];
            for (int column = 0; column < line.Length; column++)
            {
                if (line[column] != '#')
                {
                    continue;
                }

                drawingContext.DrawRectangle(
                    brush,
                    null,
                    new Rect(
                        origin.X + column * pixelSize,
                        origin.Y + row * pixelSize,
                        pixelSize,
                        pixelSize));
            }
        }
    }

    private static IReadOnlyDictionary<PixelIconKind, string[]> BuildMaps()
    {
        return new Dictionary<PixelIconKind, string[]>
        {
            [PixelIconKind.Home] = new[]
            {
                "....##....",
                "...####...",
                "..######..",
                ".########.",
                "##########",
                ".#......#.",
                ".#..##..#.",
                ".#..##..#.",
                ".#..##..#.",
                ".########."
            },
            [PixelIconKind.Mods] = new[]
            {
                ".########.",
                ".########.",
                ".########.",
                "....##....",
                "....##....",
                "....##....",
                "....##....",
                "....##....",
                "....##....",
                ".........."
            },
            [PixelIconKind.Worlds] = new[]
            {
                "...####...",
                "..#....#..",
                ".#..##..#.",
                "#..####..#",
                "#..###...#",
                "#...##...#",
                "#..####..#",
                ".#..#...#.",
                "..#....#..",
                "...####..."
            },
            [PixelIconKind.Logs] = new[]
            {
                ".########.",
                ".#......#.",
                ".#.####.#.",
                ".#......#.",
                ".#.####.#.",
                ".#......#.",
                ".#.###..#.",
                ".#......#.",
                ".#......#.",
                ".########."
            },
            [PixelIconKind.Settings] = new[]
            {
                "..##..##..",
                ".########.",
                ".##.##.##.",
                "###.##.###",
                ".#..##..#.",
                ".#..##..#.",
                "###.##.###",
                ".##.##.##.",
                ".########.",
                "..##..##.."
            },
            [PixelIconKind.Play] = new[]
            {
                "..##......",
                "..####....",
                "..######..",
                "..########",
                "..########",
                "..######..",
                "..####....",
                "..##......",
                "..........",
                ".........."
            },
            [PixelIconKind.Refresh] = new[]
            {
                "........##",
                "...##...##",
                "..#..#....",
                ".#....#...",
                ".#....#...",
                "#......#..",
                "#..##..#..",
                ".#..##.#..",
                "..#..#....",
                "...##....."
            },
            [PixelIconKind.Shield] = new[]
            {
                ".########.",
                ".########.",
                ".##....##.",
                ".##....##.",
                ".##....##.",
                "..######..",
                "..######..",
                "...####...",
                "....##....",
                "....##...."
            },
            [PixelIconKind.Crash] = new[]
            {
                "....##....",
                ".#..##..#.",
                "..#.##.#..",
                "...####...",
                "###.##.###",
                "###.##.###",
                "...####...",
                "..#.##.#..",
                ".#..##..#.",
                "....##...."
            },
            [PixelIconKind.Folder] = new[]
            {
                "..####....",
                ".#....#...",
                "##########",
                "#........#",
                "#........#",
                "#........#",
                "#........#",
                "#........#",
                "##########",
                ".........."
            },
            [PixelIconKind.User] = new[]
            {
                "...####...",
                "..#....#..",
                ".#......#.",
                ".#......#.",
                "..#....#..",
                "...####...",
                "..........",
                "..######..",
                ".########.",
                "##########"
            },
            [PixelIconKind.Plus] = new[]
            {
                "....##....",
                "....##....",
                "....##....",
                "....##....",
                "##########",
                "##########",
                "....##....",
                "....##....",
                "....##....",
                "....##...."
            },
            [PixelIconKind.Check] = new[]
            {
                "........##",
                ".......###",
                "......###.",
                "##...###..",
                "###.###...",
                ".#####....",
                "..###.....",
                "...#......",
                "..........",
                ".........."
            },
            [PixelIconKind.Warning] = new[]
            {
                "....##....",
                "...#..#...",
                "..#....#..",
                "..#.##.#..",
                ".#..##..#.",
                ".#..##..#.",
                "#...##...#",
                "#........#",
                "#...##...#",
                "##########"
            },
            [PixelIconKind.Download] = new[]
            {
                "....##....",
                "....##....",
                "....##....",
                ".##.##.##.",
                "..######..",
                "...####...",
                "....##....",
                "..........",
                "##########",
                ".........."
            },
            [PixelIconKind.Clock] = new[]
            {
                "...####...",
                "..#....#..",
                ".#......#.",
                "#........#",
                "#....#...#",
                "#....####.",
                "#........#",
                ".#......#.",
                "..#....#..",
                "...####..."
            },
            [PixelIconKind.Memory] = new[]
            {
                "##########",
                "#.#.#.#..#",
                "#.#.#.#..#",
                "#.#.#.#..#",
                "#.#.#.#..#",
                "#.#.#.#..#",
                "#.#.#.#..#",
                "##########",
                "..##..##..",
                "..##..##.."
            },
            [PixelIconKind.Java] = new[]
            {
                "..#..#....",
                ".#..#.....",
                "..#..#....",
                "..........",
                ".########.",
                ".#......#.",
                ".#......##",
                ".#......#.",
                "..######..",
                ".........."
            },
            [PixelIconKind.Server] = new[]
            {
                "##########",
                "#........#",
                "#..####..#",
                "#........#",
                "##########",
                "#........#",
                "#..####..#",
                "#........#",
                "##########",
                ".........."
            },
            [PixelIconKind.Close] = new[]
            {
                "##......##",
                "###....###",
                ".###..###.",
                "..######..",
                "...####...",
                "...####...",
                "..######..",
                ".###..###.",
                "###....###",
                "##......##"
            },
            [PixelIconKind.Minimize] = new[]
            {
                "..........",
                "..........",
                "..........",
                "..........",
                "..........",
                "..........",
                "##########",
                "##########",
                "..........",
                ".........."
            },
            [PixelIconKind.Maximize] = new[]
            {
                "..........",
                ".########.",
                ".#......#.",
                ".#......#.",
                ".#......#.",
                ".#......#.",
                ".#......#.",
                ".########.",
                "..........",
                ".........."
            },
            [PixelIconKind.Back] = new[]
            {
                "....##....",
                "...###....",
                "..####....",
                ".#####....",
                "##########",
                ".#####....",
                "..####....",
                "...###....",
                "....##....",
                ".........."
            },
            [PixelIconKind.Search] = new[]
            {
                "..#####...",
                ".#.....#..",
                "#.......#.",
                "#.......#.",
                "#.......#.",
                ".#.....#..",
                "..#####...",
                ".....##...",
                "......##..",
                ".......##."
            },
            [PixelIconKind.Trash] = new[]
            {
                "...####...",
                "..######..",
                "..........",
                "##########",
                "#..#..#..#",
                "#..#..#..#",
                "#..#..#..#",
                "#..#..#..#",
                "##########",
                ".........."
            },
            [PixelIconKind.Power] = new[]
            {
                "....##....",
                "....##....",
                "..#.##.#..",
                ".#..##..#.",
                "#...##...#",
                "#........#",
                "#........#",
                ".#......#.",
                "..#....#..",
                "...####..."
            },
            [PixelIconKind.News] = new[]
            {
                "..........",
                "##########",
                "#........#",
                "#.######.#",
                "#.#....#.#",
                "#.#....#.#",
                "#.######.#",
                "#........#",
                "##########",
                ".........."
            },
            [PixelIconKind.Backup] = new[]
            {
                "....##....",
                "...####...",
                "..##..##..",
                ".##....##.",
                "..........",
                "##########",
                "#........#",
                "#.######.#",
                "#........#",
                "##########"
            },
            [PixelIconKind.Info] = new[]
            {
                "...####...",
                "..#....#..",
                ".#..##..#.",
                "#...##...#",
                "#........#",
                "#...##...#",
                "#...##...#",
                ".#..##..#.",
                "..#....#..",
                "...####..."
            }
        };
    }
}
