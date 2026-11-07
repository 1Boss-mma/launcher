using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Media;

namespace SamaraLauncher.App.Controls;

/// <summary>
/// Пиксельный панорамный пейзаж: небо, солнце/луна, облака, горы, холмы, луг и частицы.
/// Ночная сцена целиком строится из геометрии; статичные слои кэшируются один раз.
/// Палитры и кисти подготовлены заранее, поэтому кадр не создаёт новых объектов.
/// </summary>
public partial class PixelPanorama : FrameworkElement
{
    private const double Pixel = 4d;

    // 30 кадров в секунду: пиксель-арту достаточно, а нагрузка вдвое ниже.
    private const double FrameInterval = 1d / 30d;
    private const double NightTransitionSeconds = 1.5d;
    private const double MaxFrameDelta = 0.1d;

    // Количество заранее просчитанных ступеней палитры.
    // Небо большое, поэтому для него шагов больше — переход выглядит непрерывным.
    private const int TintSteps = 64;
    private const int SkySteps = 96;
    private const int StarLevels = 32;
    private const int ParticleLevels = 16;

    private static readonly double[] SkyOffsets =
    {
        0d, 0.18d, 0.36d, 0.54d, 0.68d, 0.80d, 0.90d, 1d
    };

    // День: глубокий синий верх и тёплый горизонт.
    private static readonly Color[] SkyDayColors =
    {
        Color.FromRgb(0x17, 0x45, 0x8F),
        Color.FromRgb(0x27, 0x61, 0xB4),
        Color.FromRgb(0x46, 0x85, 0xCF),
        Color.FromRgb(0x76, 0xB0, 0xE5),
        Color.FromRgb(0xA9, 0xD2, 0xF0),
        Color.FromRgb(0xE8, 0xE4, 0xCE),
        Color.FromRgb(0xF6, 0xCD, 0x92),
        Color.FromRgb(0xEF, 0xA7, 0x6B)
    };

    // Сумерки: тёплый закатный коридор между днём и ночью.
    private static readonly Color[] SkyDuskColors =
    {
        Color.FromRgb(0x17, 0x1B, 0x44),
        Color.FromRgb(0x2A, 0x2A, 0x61),
        Color.FromRgb(0x55, 0x3A, 0x72),
        Color.FromRgb(0x8B, 0x50, 0x79),
        Color.FromRgb(0xC3, 0x70, 0x77),
        Color.FromRgb(0xE7, 0x9A, 0x6C),
        Color.FromRgb(0xF4, 0xBA, 0x7A),
        Color.FromRgb(0xE9, 0xAE, 0x7A)
    };

    // Ночь: тёмно-синий градиент с чуть более светлым горизонтом.
    private static readonly Color[] SkyNightColors =
    {
        Color.FromRgb(0x05, 0x07, 0x0F),
        Color.FromRgb(0x08, 0x0D, 0x20),
        Color.FromRgb(0x0D, 0x16, 0x30),
        Color.FromRgb(0x14, 0x21, 0x42),
        Color.FromRgb(0x1D, 0x2F, 0x53),
        Color.FromRgb(0x26, 0x3C, 0x63),
        Color.FromRgb(0x2F, 0x48, 0x70),
        Color.FromRgb(0x22, 0x36, 0x50)
    };

    // Формы облаков заданы в «пиксельных» единицах, а не в экранных.
    // Tone: 0 — тело, 1 — тень, 2 — блик.
    private static readonly CloudPart[] CloudShapeWide =
    {
        new CloudPart(0d, 3d, 13d, 3d, 0),
        new CloudPart(2d, 1d, 3d, 3d, 0),
        new CloudPart(4d, 0d, 4d, 4d, 0),
        new CloudPart(8d, 1d, 3d, 3d, 0),
        new CloudPart(11d, 2d, 2d, 2d, 0),
        new CloudPart(5d, 0d, 3d, 1d, 2),
        new CloudPart(2d, 1d, 2d, 1d, 2),
        new CloudPart(9d, 1d, 2d, 1d, 2),
        new CloudPart(0d, 6d, 13d, 1d, 1),
        new CloudPart(3d, 3d, 4d, 1d, 1),
        new CloudPart(10d, 4d, 3d, 1d, 1)
    };

    private static readonly CloudPart[] CloudShapeLong =
    {
        new CloudPart(0d, 3d, 16d, 3d, 0),
        new CloudPart(2d, 1d, 4d, 3d, 0),
        new CloudPart(6d, 0d, 6d, 4d, 0),
        new CloudPart(11d, 2d, 3d, 2d, 0),
        new CloudPart(6d, 0d, 5d, 1d, 2),
        new CloudPart(3d, 1d, 3d, 1d, 2),
        new CloudPart(0d, 6d, 16d, 1d, 1),
        new CloudPart(12d, 4d, 4d, 1d, 1),
        new CloudPart(6d, 5d, 5d, 1d, 1)
    };

    private static readonly CloudPart[] CloudShapeSmall =
    {
        new CloudPart(0d, 2d, 9d, 3d, 0),
        new CloudPart(2d, 0d, 4d, 3d, 0),
        new CloudPart(5d, 1d, 3d, 2d, 0),
        new CloudPart(3d, 0d, 2d, 1d, 2),
        new CloudPart(6d, 1d, 1d, 1d, 2),
        new CloudPart(0d, 5d, 9d, 1d, 1),
        new CloudPart(7d, 3d, 2d, 1d, 1)
    };

    // Кратеры луны в локальных координатах [-1..1], размер — в пикселях.
    private static readonly MoonCrater[] MoonCraters =
    {
        new MoonCrater(-0.26d, -0.20d, 2),
        new MoonCrater(0.18d, -0.30d, 1),
        new MoonCrater(0.34d, 0.04d, 1),
        new MoonCrater(-0.05d, 0.28d, 2),
        new MoonCrater(-0.36d, 0.10d, 1),
        new MoonCrater(0.06d, -0.02d, 1)
    };

    private readonly Random _random;
    private readonly Star[] _stars;
    private readonly Particle[] _pollen;
    private readonly Firefly[] _fireflies;
    private readonly Cloud[] _clouds;
    private readonly ShootingStar[] _shootingStars;

    private readonly LinearGradientBrush[] _skyBrushes;
    private readonly LinearGradientBrush _horizonGlow;

    private readonly SolidColorBrush[] _starCool;
    private readonly SolidColorBrush[] _starWarm;
    private readonly SolidColorBrush[] _pollenCore;
    private readonly SolidColorBrush[] _pollenGlow;
    private readonly SolidColorBrush[] _fireflyCore;
    private readonly SolidColorBrush[] _fireflyGlow;

    private readonly SolidColorBrush _sunRay = PixelPalette.CreateBrush(Color.FromArgb(58, 0xFF, 0xD9, 0x7E));
    private readonly SolidColorBrush _sunHaloOuter = PixelPalette.CreateBrush(Color.FromArgb(10, 0xFF, 0xD4, 0x74));
    private readonly SolidColorBrush _sunHaloMid = PixelPalette.CreateBrush(Color.FromArgb(18, 0xFF, 0xD8, 0x7C));
    private readonly SolidColorBrush _sunHaloInner = PixelPalette.CreateBrush(Color.FromArgb(30, 0xFF, 0xDF, 0x8E));
    private readonly SolidColorBrush _sunOuter = PixelPalette.CreateBrush(Color.FromRgb(0xFF, 0xCB, 0x58));
    private readonly SolidColorBrush _sunCore = PixelPalette.CreateBrush(Color.FromRgb(0xFF, 0xE4, 0x8C));
    private readonly SolidColorBrush _sunHighlight = PixelPalette.CreateBrush(Color.FromRgb(0xFF, 0xF6, 0xCF));

    private readonly SolidColorBrush _moonHaloOuter = PixelPalette.CreateBrush(Color.FromArgb(8, 0xA9, 0xC4, 0xFF));
    private readonly SolidColorBrush _moonHaloMid = PixelPalette.CreateBrush(Color.FromArgb(15, 0xAF, 0xC9, 0xFF));
    private readonly SolidColorBrush _moonHaloInner = PixelPalette.CreateBrush(Color.FromArgb(25, 0xBF, 0xD5, 0xFF));
    private readonly SolidColorBrush _moonBase = PixelPalette.CreateBrush(Color.FromRgb(0xE8, 0xEE, 0xF8));
    private readonly SolidColorBrush _moonShade = PixelPalette.CreateBrush(Color.FromRgb(0xC7, 0xD2, 0xE6));
    private readonly SolidColorBrush _moonLight = PixelPalette.CreateBrush(Color.FromRgb(0xF7, 0xFA, 0xFF));
    private readonly SolidColorBrush _moonCrater = PixelPalette.CreateBrush(Color.FromRgb(0xB2, 0xBF, 0xD6));
    private readonly SolidColorBrush _moonCraterLight = PixelPalette.CreateBrush(Color.FromRgb(0xDD, 0xE6, 0xF4));
    private readonly SolidColorBrush _dew = PixelPalette.CreateBrush(Color.FromArgb(210, 0xD8, 0xEC, 0xFF));

    private readonly CloudStyle _cloudFar;
    private readonly CloudStyle _cloudMid;
    private readonly CloudStyle _cloudNear;

    private readonly RidgeStyle _styleBackRidge;
    private readonly RidgeStyle _styleFarRidge;
    private readonly RidgeStyle _styleBackHills;
    private readonly RidgeStyle _styleFrontHills;
    private readonly RidgeStyle _styleMeadow;

    private readonly Tint _treeBody;
    private readonly Tint _treeRim;
    private readonly Tint _treeTrunk;
    private readonly Tint _grassLight;
    private readonly Tint _grassDark;
    private readonly Tint _flowerWhite;
    private readonly Tint _flowerGold;
    private readonly Tint _flowerRose;
    private readonly Tint _flowerCenter;
    private readonly Tint _haze;

    private Ridge _backRidge;
    private Ridge _farRidge;
    private Ridge _backHills;
    private Ridge _frontHills;
    private Ridge _meadowRidge;

    private Tree[] _trees = Array.Empty<Tree>();
    private Grass[] _grass = Array.Empty<Grass>();
    private Flower[] _flowers = Array.Empty<Flower>();
    private Dew[] _dewDrops = Array.Empty<Dew>();

    private double _sceneWidth = -1d;
    private double _sceneHeight = -1d;

    private double _time;
    private double _frameAccumulator;
    private double _nightAmount;
    private double _nightFrom;
    private double _nightTarget;
    private double _nightProgress = 1d;
    private TimeSpan _lastRenderingTime;
    private readonly Stopwatch _animationClock = new();
    private DispatcherTimer? _animationTimer;
    private bool _hasRenderingTime;
    private bool _renderingHooked;

    public static readonly DependencyProperty IsNightProperty = DependencyProperty.Register(
        nameof(IsNight),
        typeof(bool),
        typeof(PixelPanorama),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsRender,
            (dependencyObject, _) => ((PixelPanorama)dependencyObject).OnNightChanged()));

    public static readonly DependencyProperty ParticlesEnabledProperty = DependencyProperty.Register(
        nameof(ParticlesEnabled),
        typeof(bool),
        typeof(PixelPanorama),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AnimationEnabledProperty = DependencyProperty.Register(
        nameof(AnimationEnabled),
        typeof(bool),
        typeof(PixelPanorama),
        new FrameworkPropertyMetadata(
            true,
            FrameworkPropertyMetadataOptions.AffectsRender,
            (dependencyObject, _) =>
            {
                var panorama = (PixelPanorama)dependencyObject;
                panorama.OnAnimationEnabledChanged();
            }));

    /// <summary>
    /// Временная пауза без изменения пользовательской настройки анимации:
    /// окно свёрнуто/неактивно или открыт оверлей.
    /// </summary>
    public static readonly DependencyProperty IsPausedProperty = DependencyProperty.Register(
        nameof(IsPaused),
        typeof(bool),
        typeof(PixelPanorama),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.AffectsRender,
            (dependencyObject, _) => ((PixelPanorama)dependencyObject).UpdateAnimationHook()));

    public PixelPanorama()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        ClipToBounds = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        _random = new Random(0x5A4D);
        _skyBrushes = BuildSkyBrushes();
        _horizonGlow = BuildHorizonGlow();

        _starCool = BuildStarBrushes(Color.FromRgb(0xDF, 0xEC, 0xFF));
        _starWarm = BuildStarBrushes(Color.FromRgb(0xFF, 0xE8, 0xBE));
        _pollenCore = BuildCoreBrushes(Color.FromRgb(0xFF, 0xE8, 0xA8));
        _pollenGlow = BuildGlowBrushes(Color.FromRgb(0xFF, 0xD9, 0x7A), 44);
        _fireflyCore = BuildCoreBrushes(Color.FromRgb(0xD5, 0xFF, 0x96));
        _fireflyGlow = BuildGlowBrushes(Color.FromRgb(0x9C, 0xE8, 0x70), 58);

        _cloudFar = new CloudStyle(
            new Tint(
                Color.FromRgb(0xFF, 0xF8, 0xEC),
                Color.FromRgb(0xFF, 0xDF, 0xBB),
                Color.FromRgb(0x6C, 0x7D, 0x9D)),
            new Tint(
                Color.FromRgb(0xDD, 0xEA, 0xF6),
                Color.FromRgb(0xD1, 0xA7, 0x9A),
                Color.FromRgb(0x4E, 0x5E, 0x7C)),
            new Tint(
                Color.FromRgb(0xFF, 0xFF, 0xFF),
                Color.FromRgb(0xFF, 0xF1, 0xD5),
                Color.FromRgb(0x8D, 0x9E, 0xBC)),
            0.72d);

        _cloudMid = new CloudStyle(
            new Tint(
                Color.FromRgb(0xFF, 0xFF, 0xFF),
                Color.FromRgb(0xFF, 0xE6, 0xC5),
                Color.FromRgb(0x5A, 0x6A, 0x8B)),
            new Tint(
                Color.FromRgb(0xD3, 0xE3, 0xF2),
                Color.FromRgb(0xD6, 0xA9, 0x96),
                Color.FromRgb(0x3B, 0x4A, 0x68)),
            new Tint(
                Color.FromRgb(0xFF, 0xFF, 0xFF),
                Color.FromRgb(0xFF, 0xF3, 0xDD),
                Color.FromRgb(0x7A, 0x8C, 0xAB)),
            0.85d);

        _cloudNear = new CloudStyle(
            new Tint(
                Color.FromRgb(0xFF, 0xFF, 0xFF),
                Color.FromRgb(0xFF, 0xD9, 0xAD),
                Color.FromRgb(0x45, 0x52, 0x73)),
            new Tint(
                Color.FromRgb(0xC2, 0xD8, 0xEA),
                Color.FromRgb(0xC1, 0x8D, 0x80),
                Color.FromRgb(0x2B, 0x35, 0x4E)),
            new Tint(
                Color.FromRgb(0xFF, 0xFF, 0xFF),
                Color.FromRgb(0xFF, 0xED, 0xCF),
                Color.FromRgb(0x65, 0x76, 0x99)),
            0.95d);

        _styleBackRidge = new RidgeStyle(
            new Tint(
                Color.FromRgb(0x7F, 0xA4, 0xB9),
                Color.FromRgb(0x7C, 0x6D, 0x88),
                Color.FromRgb(0x26, 0x3A, 0x52)),
            new Tint(
                Color.FromRgb(0xEA, 0xF4, 0xFB),
                Color.FromRgb(0xFF, 0xD9, 0xBC),
                Color.FromRgb(0x90, 0xA9, 0xC6)),
            new Tint(
                Color.FromRgb(0x6C, 0x90, 0xA6),
                Color.FromRgb(0x6A, 0x5E, 0x7A),
                Color.FromRgb(0x1C, 0x2D, 0x42)));

        _styleFarRidge = new RidgeStyle(
            new Tint(
                Color.FromRgb(0x5E, 0x87, 0x9F),
                Color.FromRgb(0x5C, 0x58, 0x78),
                Color.FromRgb(0x1C, 0x2D, 0x45)),
            new Tint(
                Color.FromRgb(0xDC, 0xEB, 0xF5),
                Color.FromRgb(0xF6, 0xC9, 0xA6),
                Color.FromRgb(0x7C, 0x97, 0xB5)),
            new Tint(
                Color.FromRgb(0x4B, 0x74, 0x8C),
                Color.FromRgb(0x4A, 0x47, 0x67),
                Color.FromRgb(0x13, 0x23, 0x38)));

        _styleBackHills = new RidgeStyle(
            new Tint(
                Color.FromRgb(0x5C, 0x99, 0x55),
                Color.FromRgb(0x6E, 0x6A, 0x55),
                Color.FromRgb(0x1B, 0x2D, 0x2C)),
            new Tint(
                Color.FromRgb(0x93, 0xD0, 0x71),
                Color.FromRgb(0xE2, 0xB1, 0x76),
                Color.FromRgb(0x4C, 0x72, 0x80)),
            new Tint(
                Color.FromRgb(0x4A, 0x7E, 0x45),
                Color.FromRgb(0x51, 0x4C, 0x43),
                Color.FromRgb(0x12, 0x20, 0x20)));

        _styleFrontHills = new RidgeStyle(
            new Tint(
                Color.FromRgb(0x48, 0x7F, 0x44),
                Color.FromRgb(0x56, 0x52, 0x46),
                Color.FromRgb(0x14, 0x25, 0x21)),
            new Tint(
                Color.FromRgb(0x7C, 0xC6, 0x5C),
                Color.FromRgb(0xD4, 0xA4, 0x69),
                Color.FromRgb(0x43, 0x66, 0x6F)),
            new Tint(
                Color.FromRgb(0x35, 0x61, 0x32),
                Color.FromRgb(0x40, 0x3C, 0x34),
                Color.FromRgb(0x0C, 0x15, 0x14)));

        _styleMeadow = new RidgeStyle(
            new Tint(
                Color.FromRgb(0x3F, 0x76, 0x39),
                Color.FromRgb(0x45, 0x45, 0x36),
                Color.FromRgb(0x0E, 0x1C, 0x17)),
            new Tint(
                Color.FromRgb(0x74, 0xB8, 0x4E),
                Color.FromRgb(0xC9, 0x9A, 0x5C),
                Color.FromRgb(0x3A, 0x5C, 0x5E)),
            new Tint(
                Color.FromRgb(0x2B, 0x54, 0x28),
                Color.FromRgb(0x34, 0x34, 0x2A),
                Color.FromRgb(0x07, 0x10, 0x0C)));

        _treeBody = new Tint(
            Color.FromRgb(0x2E, 0x5B, 0x3B),
            Color.FromRgb(0x3D, 0x37, 0x38),
            Color.FromRgb(0x09, 0x13, 0x15));
        _treeRim = new Tint(
            Color.FromRgb(0x78, 0xB0, 0x4E),
            Color.FromRgb(0xCE, 0x9B, 0x62),
            Color.FromRgb(0x50, 0x78, 0x88));
        _treeTrunk = new Tint(
            Color.FromRgb(0x4A, 0x35, 0x28),
            Color.FromRgb(0x3F, 0x32, 0x31),
            Color.FromRgb(0x08, 0x0D, 0x10));

        _grassLight = new Tint(
            Color.FromRgb(0x8F, 0xD3, 0x5F),
            Color.FromRgb(0xD6, 0xB0, 0x6A),
            Color.FromRgb(0x3C, 0x65, 0x59));
        _grassDark = new Tint(
            Color.FromRgb(0x2E, 0x5A, 0x2B),
            Color.FromRgb(0x39, 0x35, 0x2B),
            Color.FromRgb(0x08, 0x12, 0x0F));

        _flowerWhite = new Tint(
            Color.FromRgb(0xFF, 0xF6, 0xD8),
            Color.FromRgb(0xFF, 0xE2, 0xBC),
            Color.FromRgb(0xB4, 0xCD, 0xE8));
        _flowerGold = new Tint(
            Color.FromRgb(0xFF, 0xD4, 0x5E),
            Color.FromRgb(0xFF, 0xB6, 0x5C),
            Color.FromRgb(0xC9, 0xA9, 0x6A));
        _flowerRose = new Tint(
            Color.FromRgb(0xF5, 0x8A, 0xA0),
            Color.FromRgb(0xE7, 0x7C, 0x92),
            Color.FromRgb(0x96, 0x78, 0xA8));
        _flowerCenter = new Tint(
            Color.FromRgb(0xFF, 0xE9, 0x9A),
            Color.FromRgb(0xFF, 0xC5, 0x6A),
            Color.FromRgb(0xD8, 0xC2, 0x7C));
        _haze = new Tint(
            Color.FromArgb(66, 0xD9, 0xEC, 0xF8),
            Color.FromArgb(60, 0xF0, 0xB8, 0x94),
            Color.FromArgb(50, 0x6F, 0x8A, 0xB2));

        _stars = new Star[200];
        for (int i = 0; i < _stars.Length; i++)
        {
            _stars[i] = new Star(
                _random.NextDouble(),
                Math.Pow(_random.NextDouble(), 1.35d) * 0.64d,
                0.35d + _random.NextDouble() * 1.4d,
                _random.NextDouble() * Math.PI * 2d,
                0.35d + _random.NextDouble() * 0.65d,
                _random.NextDouble() < 0.12d ? 2 : 1,
                _random.NextDouble() < 0.28d);
        }

        _pollen = new Particle[34];
        for (int i = 0; i < _pollen.Length; i++)
        {
            _pollen[i] = new Particle(
                _random.NextDouble(),
                0.14d + _random.NextDouble() * 0.70d,
                _random.NextDouble() * Math.PI * 2d,
                6d + _random.NextDouble() * 14d,
                _random.NextDouble() < 0.28d ? 2 : 1);
        }

        _fireflies = new Firefly[22];
        for (int i = 0; i < _fireflies.Length; i++)
        {
            _fireflies[i] = new Firefly(
                _random.NextDouble(),
                0.58d + _random.NextDouble() * 0.38d,
                _random.NextDouble() * Math.PI * 2d,
                0.55d + _random.NextDouble() * 0.65d,
                _random.NextDouble() < 0.30d ? 2 : 1);
        }

        _clouds = new[]
        {
            new Cloud(0.02d, 0.13d, 3.0d, 0.62d, 0, 0, 0.3d),
            new Cloud(0.28d, 0.22d, 4.0d, 0.74d, 0, 2, 1.7d),
            new Cloud(0.55d, 0.16d, 3.4d, 0.58d, 0, 1, 3.1d),
            new Cloud(0.80d, 0.27d, 4.6d, 0.80d, 0, 0, 4.2d),
            new Cloud(0.10d, 0.30d, 7.0d, 1.05d, 1, 1, 0.8d),
            new Cloud(0.42d, 0.35d, 8.5d, 1.20d, 1, 0, 2.9d),
            new Cloud(0.72d, 0.31d, 7.6d, 0.98d, 1, 2, 5.0d),
            new Cloud(0.93d, 0.38d, 9.0d, 1.15d, 1, 1, 1.2d),
            new Cloud(0.16d, 0.40d, 12.0d, 1.70d, 2, 0, 0.5d),
            new Cloud(0.48d, 0.45d, 14.0d, 2.10d, 2, 2, 2.2d),
            new Cloud(0.74d, 0.42d, 11.0d, 1.55d, 2, 1, 4.0d),
            new Cloud(0.90d, 0.48d, 13.0d, 1.85d, 2, 0, 5.5d)
        };

        _shootingStars = new[]
        {
            new ShootingStar(13d, 2.5d, 0.85d, 0.16d, 0.08d, 0.22d, 0.09d),
            new ShootingStar(21d, 11.0d, 1.05d, 0.58d, 0.05d, 0.17d, 0.12d),
            new ShootingStar(29d, 6.0d, 0.75d, 0.82d, 0.12d, -0.13d, 0.08d)
        };

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public bool IsNight
    {
        get => (bool)GetValue(IsNightProperty);
        set => SetValue(IsNightProperty, value);
    }

    public bool ParticlesEnabled
    {
        get => (bool)GetValue(ParticlesEnabledProperty);
        set => SetValue(ParticlesEnabledProperty, value);
    }

    public bool AnimationEnabled
    {
        get => (bool)GetValue(AnimationEnabledProperty);
        set => SetValue(AnimationEnabledProperty, value);
    }

    public bool IsPaused
    {
        get => (bool)GetValue(IsPausedProperty);
        set => SetValue(IsPausedProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 800d : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 450d : availableSize.Height;
        return new Size(Math.Max(0d, width), Math.Max(0d, height));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        double width = ActualWidth;
        double height = ActualHeight;
        if (width < 8d || height < 8d)
        {
            return;
        }

        DrawNightValley(drawingContext, width, height);
    }

    private void DrawSky(DrawingContext drawingContext, double width, double height, double night)
    {
        drawingContext.DrawRectangle(
            _skyBrushes[PaletteIndex(night, SkySteps)],
            null,
            new Rect(0d, 0d, width, height));
    }

    private void DrawCelestial(DrawingContext drawingContext, double width, double height, double night)
    {
        double minSide = Math.Min(width, height);
        double sunRadius = Math.Max(Pixel * 6d, minSide * 0.070d);
        double moonRadius = Math.Max(Pixel * 5d, minSide * 0.058d);

        // У светил разные траектории: солнце к ночи опускается, луна поднимается.
        // Получается мягкий параллакс и ощущение смены времени суток.
        double sunX = width * 0.78d + Math.Sin(_time * 0.05d) * width * 0.004d;
        double sunY = height * 0.22d + night * height * 0.14d + Math.Sin(_time * 0.11d) * Pixel;
        double moonX = width * 0.64d + Math.Cos(_time * 0.04d) * width * 0.006d;
        double moonY = height * 0.18d + (1d - night) * height * 0.09d + Math.Sin(_time * 0.09d + 1.3d) * Pixel;

        double sunOpacity = 1d - night;
        double moonOpacity = night;

        if (sunOpacity > 0.002d)
        {
            drawingContext.PushOpacity(sunOpacity);
            // Центр не привязываем к пиксельной сетке: движение светила
            // остаётся плавным, а пиксельными остаются его форма и размер.
            DrawSun(drawingContext, sunX, sunY, sunRadius);
            drawingContext.Pop();
        }

        if (moonOpacity > 0.002d)
        {
            drawingContext.PushOpacity(moonOpacity);
            DrawMoon(drawingContext, moonX, moonY, moonRadius);
            drawingContext.Pop();
        }
    }

    private void DrawSun(DrawingContext drawingContext, double centerX, double centerY, double radius)
    {
        DrawPixelDisc(drawingContext, _sunHaloOuter, centerX, centerY, radius * 2.15d);
        DrawPixelDisc(drawingContext, _sunHaloMid, centerX, centerY, radius * 1.55d);
        DrawPixelDisc(drawingContext, _sunHaloInner, centerX, centerY, radius * 1.18d);

        // Восемь коротких пиксельных лучей, которые едва заметно пульсируют.
        double pulse = 0.70d + 0.30d * Math.Sin(_time * 0.9d);
        drawingContext.PushOpacity(pulse);
        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4d;
            double cos = Math.Cos(angle);
            double sin = Math.Sin(angle);
            double distance = radius * 1.42d;
            double rayWidth = Math.Abs(cos) > 0.7d ? Pixel * 2d : Pixel;
            double rayHeight = Math.Abs(sin) > 0.7d ? Pixel * 2d : Pixel;

            drawingContext.DrawRectangle(
                _sunRay,
                null,
                new Rect(
                    centerX + cos * distance - rayWidth * 0.5d,
                    centerY + sin * distance - rayHeight * 0.5d,
                    rayWidth,
                    rayHeight));
        }

        drawingContext.Pop();

        DrawPixelDisc(drawingContext, _sunOuter, centerX, centerY, radius);
        DrawPixelDisc(drawingContext, _sunCore, centerX - Pixel * 0.75d, centerY - Pixel * 0.75d, radius * 0.78d);
        DrawPixelDisc(drawingContext, _sunHighlight, centerX - Pixel * 1.5d, centerY - Pixel * 1.5d, radius * 0.42d);
    }

    private void DrawMoon(DrawingContext drawingContext, double centerX, double centerY, double radius)
    {
        DrawPixelDisc(drawingContext, _moonHaloOuter, centerX, centerY, radius * 2.20d);
        DrawPixelDisc(drawingContext, _moonHaloMid, centerX, centerY, radius * 1.60d);
        DrawPixelDisc(drawingContext, _moonHaloInner, centerX, centerY, radius * 1.20d);

        DrawPixelDisc(drawingContext, _moonBase, centerX, centerY, radius);
        DrawPixelDisc(drawingContext, _moonShade, centerX + Pixel * 0.45d, centerY + Pixel * 0.45d, radius * 0.88d);
        DrawPixelDisc(drawingContext, _moonLight, centerX - radius * 0.18d, centerY - radius * 0.18d, radius * 0.60d);

        for (int i = 0; i < MoonCraters.Length; i++)
        {
            MoonCrater crater = MoonCraters[i];
            double x = centerX + crater.X * radius;
            double y = centerY + crater.Y * radius;
            double size = crater.Size * Pixel;

            drawingContext.DrawRectangle(_moonCrater, null, new Rect(x, y, size, size));
            if (crater.Size >= 2)
            {
                drawingContext.DrawRectangle(
                    _moonCraterLight,
                    null,
                    new Rect(x, y, size, Pixel * 0.5d));
            }
        }
    }

    private void DrawStars(DrawingContext drawingContext, double width, double height, double night)
    {
        double visibility = night * night;

        for (int i = 0; i < _stars.Length; i++)
        {
            Star star = _stars[i];
            double twinkle = 0.55d + 0.45d * Math.Sin(_time * star.Speed + star.Phase);
            double strength = star.Brightness * twinkle * visibility;
            int level = (int)(Math.Clamp(strength, 0d, 1d) * (StarLevels - 1) + 0.5d);
            if (level <= 0)
            {
                continue;
            }

            SolidColorBrush brush = star.Warm ? _starWarm[level] : _starCool[level];
            double size = star.Size * 2d;
            double x = Math.Floor(star.X * width / 2d) * 2d;
            double y = Math.Floor(star.Y * height / 2d) * 2d;

            drawingContext.DrawRectangle(brush, null, new Rect(x, y, size, size));

            // Самые яркие звёзды получают пиксельный «крестик».
            if (star.Size == 2 && level >= StarLevels * 3 / 4)
            {
                drawingContext.DrawRectangle(
                    brush,
                    null,
                    new Rect(x - Pixel, y + size * 0.5d - 1d, size + Pixel * 2d, 2d));
                drawingContext.DrawRectangle(
                    brush,
                    null,
                    new Rect(x + size * 0.5d - 1d, y - Pixel, 2d, size + Pixel * 2d));
            }
        }
    }

    private void DrawShootingStars(DrawingContext drawingContext, double width, double height, double night)
    {
        double visibility = SmoothStep((night - 0.55d) / 0.45d);
        if (visibility <= 0.002d)
        {
            return;
        }

        for (int i = 0; i < _shootingStars.Length; i++)
        {
            ShootingStar star = _shootingStars[i];
            double local = (_time + star.Offset) % star.Period;
            if (local > star.Duration)
            {
                continue;
            }

            double progress = local / star.Duration;
            double fade = Math.Sin(progress * Math.PI);
            if (fade <= 0.002d)
            {
                continue;
            }

            double headX = width * (star.StartX + star.DeltaX * progress);
            double headY = height * (star.StartY + star.DeltaY * progress);
            double travelX = star.DeltaX * width;
            double travelY = star.DeltaY * height;

            drawingContext.PushOpacity(visibility * fade);

            for (int trail = 0; trail < 8; trail++)
            {
                double q = trail / 7d;
                int level = Math.Clamp((int)((1d - q) * (StarLevels - 1)), 0, StarLevels - 1);
                double x = headX - travelX * q * 0.18d;
                double y = headY - travelY * q * 0.18d;

                drawingContext.DrawRectangle(
                    _starWarm[level],
                    null,
                    new Rect(x, y, Pixel, Pixel));
            }

            drawingContext.DrawRectangle(
                _starWarm[StarLevels - 1],
                null,
                new Rect(headX - Pixel * 0.5d, headY - Pixel * 0.5d, Pixel * 2d, Pixel * 2d));

            drawingContext.Pop();
        }
    }

    private void DrawCloudLayer(
        DrawingContext drawingContext,
        double width,
        double height,
        int layer,
        CloudStyle style,
        double night)
    {
        SolidColorBrush main = style.Main.At(night);
        SolidColorBrush shade = style.Shade.At(night);
        SolidColorBrush light = style.Light.At(night);

        drawingContext.PushOpacity(style.Opacity);

        for (int i = 0; i < _clouds.Length; i++)
        {
            Cloud cloud = _clouds[i];
            if (cloud.Layer != layer)
            {
                continue;
            }

            double u = Pixel * cloud.Scale;
            double span = width + u * 18d;
            // Отступ больше самой широкой фигуры (16u): облако не «выпрыгивает»
            // на краю, а полностью уходит за границу и возвращается с другой стороны.
            double x = ((cloud.X * span + _time * cloud.Speed) % span) - u * 18d;
            double y = cloud.Y * height + Math.Sin(_time * 0.16d + cloud.Phase) * Pixel;

            CloudPart[] shape = cloud.Variant == 1
                ? CloudShapeLong
                : cloud.Variant == 2
                    ? CloudShapeSmall
                    : CloudShapeWide;

            for (int partIndex = 0; partIndex < shape.Length; partIndex++)
            {
                CloudPart part = shape[partIndex];
                SolidColorBrush brush = part.Tone == 1
                    ? shade
                    : part.Tone == 2
                        ? light
                        : main;

                drawingContext.DrawRectangle(
                    brush,
                    null,
                    new Rect(
                        x + part.X * u,
                        y + part.Y * u,
                        part.Width * u,
                        part.Height * u));
            }
        }

        drawingContext.Pop();
    }

    private void DrawHaze(DrawingContext drawingContext, double width, double height, double night)
    {
        SolidColorBrush haze = _haze.At(night);

        for (int band = 0; band < 4; band++)
        {
            drawingContext.PushOpacity(0.10d + band * 0.06d);
            drawingContext.DrawRectangle(
                haze,
                null,
                new Rect(0d, height * (0.595d + band * 0.022d), width, height * 0.045d));
            drawingContext.Pop();
        }
    }

    private void DrawRidge(DrawingContext drawingContext, Ridge ridge, RidgeStyle style, double night)
    {
        // Каждая гряда — одна замороженная геометрия из прямоугольников.
        // Так на кадр приходится три команды вместо сотен DrawRectangle.
        drawingContext.DrawGeometry(style.Body.At(night), null, ridge.Body);
        drawingContext.DrawGeometry(style.Shade.At(night), null, ridge.Shade);
        drawingContext.DrawGeometry(style.Rim.At(night), null, ridge.Rim);
    }

    private static Geometry BuildRectGeometry(Rect[] rects)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (StreamGeometryContext context = geometry.Open())
        {
            for (int i = 0; i < rects.Length; i++)
            {
                Rect rect = rects[i];
                if (rect.Width <= 0d || rect.Height <= 0d)
                {
                    continue;
                }

                context.BeginFigure(rect.TopLeft, true, true);
                context.LineTo(rect.TopRight, true, false);
                context.LineTo(rect.BottomRight, true, false);
                context.LineTo(rect.BottomLeft, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private void DrawTrees(DrawingContext drawingContext, double night)
    {
        SolidColorBrush body = _treeBody.At(night);
        SolidColorBrush rim = _treeRim.At(night);
        SolidColorBrush trunk = _treeTrunk.At(night);

        for (int i = 0; i < _trees.Length; i++)
        {
            Tree tree = _trees[i];
            double u = Pixel * tree.Scale;
            double x = tree.X;
            double y = tree.BaseY;

            drawingContext.DrawRectangle(trunk, null, new Rect(x - u * 0.5d, y - u * 2d, u, u * 2d));
            drawingContext.DrawRectangle(body, null, new Rect(x - u * 3.5d, y - u * 4d, u * 7d, u));
            drawingContext.DrawRectangle(body, null, new Rect(x - u * 2.5d, y - u * 5d, u * 5d, u));
            drawingContext.DrawRectangle(body, null, new Rect(x - u * 1.5d, y - u * 6d, u * 3d, u));
            drawingContext.DrawRectangle(body, null, new Rect(x - u * 0.5d, y - u * 7d, u, u));

            // Лунный блик по левой кромке кроны.
            drawingContext.DrawRectangle(rim, null, new Rect(x - u * 3.5d, y - u * 4d, u * 0.75d, u * 3d));
        }
    }

    private void DrawGrass(DrawingContext drawingContext, double night)
    {
        SolidColorBrush light = _grassLight.At(night);
        SolidColorBrush dark = _grassDark.At(night);

        for (int i = 0; i < _grass.Length; i++)
        {
            Grass blade = _grass[i];
            double x = Snap(blade.X);
            double y = Snap(blade.Y);
            double sway = Math.Sin(_time * 0.8d + blade.Phase);
            double offset = sway > 0.45d ? Pixel : sway < -0.45d ? -Pixel : 0d;
            double stemHeight = Pixel * (1d + blade.Scale);
            SolidColorBrush brush = blade.Light ? light : dark;

            drawingContext.DrawRectangle(brush, null, new Rect(x, y - stemHeight, Pixel, stemHeight));
            drawingContext.DrawRectangle(brush, null, new Rect(x + offset, y - stemHeight - Pixel, Pixel, Pixel));
        }
    }

    private void DrawFlowers(DrawingContext drawingContext, double night)
    {
        SolidColorBrush stem = _grassDark.At(night);
        SolidColorBrush center = _flowerCenter.At(night);

        for (int i = 0; i < _flowers.Length; i++)
        {
            Flower flower = _flowers[i];
            SolidColorBrush petal = flower.ColorIndex == 0
                ? _flowerWhite.At(night)
                : flower.ColorIndex == 1
                    ? _flowerGold.At(night)
                    : _flowerRose.At(night);

            double x = Snap(flower.X);
            double y = Snap(flower.Y);

            drawingContext.DrawRectangle(stem, null, new Rect(x + Pixel * 0.5d, y - Pixel * 1.5d, Pixel * 0.5d, Pixel * 1.5d));
            drawingContext.DrawRectangle(petal, null, new Rect(x - Pixel, y - Pixel * 2d, Pixel, Pixel));
            drawingContext.DrawRectangle(petal, null, new Rect(x + Pixel, y - Pixel * 2d, Pixel, Pixel));
            drawingContext.DrawRectangle(petal, null, new Rect(x, y - Pixel * 3d, Pixel, Pixel));
            drawingContext.DrawRectangle(petal, null, new Rect(x, y - Pixel, Pixel, Pixel));
            drawingContext.DrawRectangle(center, null, new Rect(x, y - Pixel * 2d, Pixel, Pixel));
        }
    }

    private void DrawDew(DrawingContext drawingContext, double night)
    {
        if (night < 0.03d)
        {
            return;
        }

        // Капли росы на лугу ловят холодный лунный свет — мягкий ночной блик.
        drawingContext.PushOpacity(night * 0.85d);
        for (int i = 0; i < _dewDrops.Length; i++)
        {
            Dew drop = _dewDrops[i];
            drawingContext.DrawRectangle(
                _dew,
                null,
                new Rect(Snap(drop.X), Snap(drop.Y), Pixel * 0.5d, Pixel * 0.5d));
        }

        drawingContext.Pop();
    }

    private void DrawPollen(DrawingContext drawingContext, double width, double height, double night)
    {
        double visibility = Math.Pow(1d - night, 1.35d);
        if (visibility < 0.02d)
        {
            return;
        }

        drawingContext.PushOpacity(visibility);
        double span = width + Pixel * 10d;

        for (int i = 0; i < _pollen.Length; i++)
        {
            Particle particle = _pollen[i];
            double x = ((particle.X * span + _time * particle.Speed) % span) - Pixel * 5d;
            double y = particle.Y * height + Math.Sin(_time * 0.6d + particle.Phase) * Pixel * 2d;
            double pulse = 0.55d + 0.45d * Math.Sin(_time * 1.1d + particle.Phase);
            int level = (int)(Math.Clamp(pulse * 0.85d + 0.15d, 0d, 1d) * (ParticleLevels - 1) + 0.5d);
            double size = particle.Size == 2 ? Pixel : Pixel * 0.5d;

            drawingContext.DrawRectangle(
                _pollenGlow[level],
                null,
                new Rect(x - Pixel, y - Pixel, size + Pixel * 2d, size + Pixel * 2d));
            drawingContext.DrawRectangle(
                _pollenCore[level],
                null,
                new Rect(x, y, size, size));
        }

        drawingContext.Pop();
    }

    private void DrawFireflies(DrawingContext drawingContext, double width, double height, double night)
    {
        double visibility = Math.Pow(night, 1.45d);
        if (visibility < 0.02d)
        {
            return;
        }

        drawingContext.PushOpacity(visibility);

        for (int i = 0; i < _fireflies.Length; i++)
        {
            Firefly firefly = _fireflies[i];
            double x = firefly.X * width + Math.Sin(_time * 0.32d * firefly.Speed + firefly.Phase) * width * 0.018d;
            double y = firefly.Y * height + Math.Sin(_time * 0.45d * firefly.Speed + firefly.Phase * 1.7d) * height * 0.012d;
            double pulse = 0.35d + 0.65d * (0.5d + 0.5d * Math.Sin(_time * 1.6d + firefly.Phase * 2.1d));
            int level = (int)(Math.Clamp(pulse, 0d, 1d) * (ParticleLevels - 1) + 0.5d);
            double size = firefly.Size * Pixel * 0.5d;

            drawingContext.DrawRectangle(
                _fireflyGlow[level],
                null,
                new Rect(x - Pixel * 1.5d, y - Pixel * 1.5d, size + Pixel * 3d, size + Pixel * 3d));
            drawingContext.DrawRectangle(
                _fireflyCore[level],
                null,
                new Rect(x, y, size, size));
        }

        drawingContext.Pop();
    }

    private static void DrawPixelDisc(
        DrawingContext drawingContext,
        Brush brush,
        double centerX,
        double centerY,
        double radius)
    {
        int r = Math.Max(1, (int)Math.Round(radius / Pixel));

        for (int gy = -r; gy <= r; gy++)
        {
            int half = (int)Math.Sqrt(Math.Max(0, r * r - gy * gy));
            drawingContext.DrawRectangle(
                brush,
                null,
                new Rect(
                    centerX - half * Pixel,
                    centerY + gy * Pixel,
                    (half * 2 + 1) * Pixel,
                    Pixel));
        }
    }

    private static double Snap(double value) => Math.Floor(value / Pixel) * Pixel;

    private static double SmoothStep(double value)
    {
        double t = Math.Clamp(value, 0d, 1d);
        return t * t * (3d - 2d * t);
    }

    private static int PaletteIndex(double amount, int steps)
    {
        double t = Math.Clamp(amount, 0d, 1d);
        return Math.Clamp((int)(t * (steps - 1) + 0.5d), 0, steps - 1);
    }

    private static Color Interpolate(Color day, Color dusk, Color night, double amount)
    {
        double t = Math.Clamp(amount, 0d, 1d);
        return t < 0.5d
            ? PixelPalette.Lerp(day, dusk, t * 2d)
            : PixelPalette.Lerp(dusk, night, (t - 0.5d) * 2d);
    }

    private static double Hash01(int index, int salt)
    {
        double value = Math.Sin(index * 12.9898d + salt * 78.233d) * 43758.5453d;
        return value - Math.Floor(value);
    }

    private static LinearGradientBrush[] BuildSkyBrushes()
    {
        var brushes = new LinearGradientBrush[SkySteps];
        var colors = new Color[SkyOffsets.Length];

        for (int step = 0; step < SkySteps; step++)
        {
            double amount = step / (double)(SkySteps - 1);
            for (int stop = 0; stop < colors.Length; stop++)
            {
                colors[stop] = Interpolate(
                    SkyDayColors[stop],
                    SkyDuskColors[stop],
                    SkyNightColors[stop],
                    amount);
            }

            brushes[step] = CreateSkyBrush(colors);
        }

        return brushes;
    }

    private static LinearGradientBrush CreateSkyBrush(Color[] colors)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0d, 0d),
            EndPoint = new Point(0d, 1d),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            ColorInterpolationMode = ColorInterpolationMode.SRgbLinearInterpolation,
            SpreadMethod = GradientSpreadMethod.Pad
        };

        for (int i = 0; i < SkyOffsets.Length; i++)
        {
            brush.GradientStops.Add(new GradientStop(colors[i], SkyOffsets[i]));
        }

        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush BuildHorizonGlow()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0d, 0d),
            EndPoint = new Point(0d, 1d),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
            ColorInterpolationMode = ColorInterpolationMode.SRgbLinearInterpolation,
            SpreadMethod = GradientSpreadMethod.Pad
        };

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xFF, 0xCE, 0x8A), 0d));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(10, 0xFF, 0xCE, 0x8A), 0.35d));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(40, 0xFF, 0xC5, 0x78), 0.75d));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0xFF, 0xB8, 0x70), 1d));

        brush.Freeze();
        return brush;
    }

    private static SolidColorBrush[] BuildStarBrushes(Color color)
    {
        var brushes = new SolidColorBrush[StarLevels];
        for (int i = 0; i < StarLevels; i++)
        {
            byte alpha = (byte)(255 * i / (StarLevels - 1));
            brushes[i] = PixelPalette.CreateBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        return brushes;
    }

    private static SolidColorBrush[] BuildCoreBrushes(Color color)
    {
        var brushes = new SolidColorBrush[ParticleLevels];
        for (int i = 0; i < ParticleLevels; i++)
        {
            byte alpha = (byte)(70 + 185 * i / (ParticleLevels - 1));
            brushes[i] = PixelPalette.CreateBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        return brushes;
    }

    private static SolidColorBrush[] BuildGlowBrushes(Color color, byte maxAlpha)
    {
        var brushes = new SolidColorBrush[ParticleLevels];
        int range = maxAlpha - 8;
        for (int i = 0; i < ParticleLevels; i++)
        {
            byte alpha = (byte)(8 + range * i / (ParticleLevels - 1));
            brushes[i] = PixelPalette.CreateBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        }

        return brushes;
    }

    private void EnsureScene(double width, double height)
    {
        // Геометрия гряд строится только при изменении размера контрола,
        // а не на каждом кадре. Во время анимации массивы уже готовы.
        if (Math.Abs(width - _sceneWidth) < 0.5d && Math.Abs(height - _sceneHeight) < 0.5d)
        {
            return;
        }

        _sceneWidth = width;
        _sceneHeight = height;

        _backRidge = BuildRidge(width, height, 0.600d, 0.140d, 1.15d, 0.4d, 0.72d, 2, 6);
        _farRidge = BuildRidge(width, height, 0.675d, 0.160d, 1.65d, 2.3d, 0.88d, 2, 8);
        _backHills = BuildRidge(width, height, 0.765d, 0.075d, 1.10d, 4.1d, 0d, 1, 7);
        _frontHills = BuildRidge(width, height, 0.825d, 0.060d, 1.55d, 1.2d, 0d, 1, 8);
        _meadowRidge = BuildRidge(width, height, 0.885d, 0.024d, 1.90d, 3.4d, 0d, 2, 10);

        BuildDetails(width, height);
    }

    private static Ridge BuildRidge(
        double width,
        double height,
        double baseFraction,
        double amplitudeFraction,
        double frequency,
        double phase,
        double jagged,
        int rimPixels,
        int shadeOffsetPixels)
    {
        int columns = Math.Max(1, (int)Math.Ceiling(width / Pixel));
        var tops = new double[columns];

        for (int i = 0; i < columns; i++)
        {
            double p = columns <= 1 ? 0d : i / (double)(columns - 1);
            double wave =
                0.58d * Math.Sin(p * Math.PI * 2d * frequency + phase) +
                0.28d * Math.Sin(p * Math.PI * 2d * frequency * 2.17d + phase * 1.7d) +
                0.14d * Math.Sin(p * Math.PI * 2d * frequency * 4.31d + phase * 0.6d);
            double ridge = 0.5d + 0.5d * wave;

            if (jagged > 0d)
            {
                double peaks = Math.Abs(Math.Sin(p * Math.PI * 2d * frequency * 1.73d + phase * 0.8d));
                ridge = ridge * (1d - jagged) + peaks * jagged;
            }

            double top = height * (baseFraction - amplitudeFraction * ridge);
            top = Math.Floor(top / Pixel) * Pixel;
            tops[i] = Math.Clamp(top, 0d, height - Pixel);
        }

        var body = new List<Rect>(columns);
        var rim = new List<Rect>(columns);
        var shade = new List<Rect>(columns);

        int runStart = 0;
        double runTop = tops[0];

        for (int i = 1; i <= columns; i++)
        {
            bool last = i == columns;
            if (!last && Math.Abs(tops[i] - runTop) < 0.01d)
            {
                continue;
            }

            double x = runStart * Pixel;
            double runWidth = Math.Min(width - x, (i - runStart) * Pixel);
            if (runWidth > 0d)
            {
                body.Add(new Rect(x, runTop, runWidth + 0.5d, Math.Max(Pixel, height - runTop)));
                rim.Add(new Rect(
                    x,
                    runTop,
                    runWidth + 0.5d,
                    Math.Min(rimPixels * Pixel, Math.Max(Pixel, height - runTop))));

                double shadeY = runTop + shadeOffsetPixels * Pixel;
                if (shadeY + Pixel <= height)
                {
                    shade.Add(new Rect(x, shadeY, runWidth + 0.5d, Pixel));
                }
            }

            if (last)
            {
                break;
            }

            runStart = i;
            runTop = tops[i];
        }

        return new Ridge(
            BuildRectGeometry(body.ToArray()),
            BuildRectGeometry(rim.ToArray()),
            BuildRectGeometry(shade.ToArray()),
            tops);
    }

    private void BuildDetails(double width, double height)
    {
        double[] tops = _frontHills.Tops;

        // Плотность деталей зависит от ширины сцены, чтобы на широких окнах
        // луг не выглядел пустым, а на компактных — не был перегружен.
        int treeCount = Math.Clamp((int)(width / 130d), 7, 14);
        var trees = new Tree[treeCount];

        for (int i = 0; i < trees.Length; i++)
        {
            double x = width * (0.055d + (i + 0.5d) / treeCount * 0.890d +
                                (Hash01(i, 3) - 0.5d) * 0.035d);
            x = Math.Clamp(x, 0d, width - 1d);

            int column = Math.Clamp((int)(x / Pixel), 0, tops.Length - 1);
            double scale = 1.15d + Hash01(i, 4) * 0.70d;
            trees[i] = new Tree(x, tops[column] + Pixel * 1.5d, scale);
        }

        _trees = trees;

        int grassCount = Math.Clamp((int)(width / 16d), 50, 160);
        var grass = new Grass[grassCount];
        for (int i = 0; i < grass.Length; i++)
        {
            grass[i] = new Grass(
                Hash01(i, 11) * width,
                height * (0.895d + Hash01(i, 12) * 0.095d),
                0.70d + Hash01(i, 13) * 1.30d,
                Hash01(i, 14) * Math.PI * 2d,
                (i & 1) == 0);
        }

        _grass = grass;

        int flowerCount = Math.Clamp((int)(width / 48d), 18, 52);
        var flowers = new Flower[flowerCount];
        for (int i = 0; i < flowers.Length; i++)
        {
            flowers[i] = new Flower(
                Hash01(i, 21) * width,
                height * (0.905d + Hash01(i, 22) * 0.070d),
                i % 3);
        }

        _flowers = flowers;

        int dewCount = Math.Clamp((int)(width / 70d), 12, 36);
        var dew = new Dew[dewCount];
        for (int i = 0; i < dew.Length; i++)
        {
            dew[i] = new Dew(
                Hash01(i, 31) * width,
                height * (0.900d + Hash01(i, 32) * 0.080d));
        }

        _dewDrops = dew;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _nightTarget = IsNight ? 1d : 0d;
        if (!AnimationEnabled)
        {
            _nightAmount = _nightTarget;
            _nightProgress = 1d;
        }

        UpdateAnimationHook();
        InvalidateVisual();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookRendering();
        _frameAccumulator = 0d;
        _hasRenderingTime = false;

        // Фазы _time, _nightAmount и _nightProgress не сбрасываем:
        // после повторной загрузки сцена продолжится с того же места.
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        UpdateAnimationHook();
        if (IsVisible)
        {
            InvalidateVisual();
        }
    }

    private void OnNightChanged()
    {
        double target = IsNight ? 1d : 0d;
        _nightFrom = _nightAmount;
        _nightTarget = target;
        _nightProgress = 0d;

        if (Math.Abs(_nightAmount - target) < 0.0001d)
        {
            _nightAmount = target;
            _nightProgress = 1d;
            InvalidateVisual();
            return;
        }

        // Без анимации или вне окна переключаем палитру сразу.
        if (!AnimationEnabled || !IsLoaded || !IsVisible)
        {
            _nightAmount = target;
            _nightProgress = 1d;
            InvalidateVisual();
            return;
        }

        UpdateAnimationHook();
        InvalidateVisual();
    }

    private void OnAnimationEnabledChanged()
    {
        _nightTarget = IsNight ? 1d : 0d;

        if (!AnimationEnabled)
        {
            // Пауза (например, на время перетаскивания или изменения размера окна):
            // сохраняем _time и текущую фазу перехода, сбрасываем только тайминг кадра.
            _frameAccumulator = 0d;
            _hasRenderingTime = false;

            if (Math.Abs(_nightAmount - _nightTarget) < 0.0001d)
            {
                _nightAmount = _nightTarget;
                _nightProgress = 1d;
            }
        }

        UpdateAnimationHook();
        InvalidateVisual();
    }

    private void UpdateAnimationHook()
    {
        bool shouldAnimate = AnimationEnabled && !IsPaused && IsLoaded && IsVisible;

        if (shouldAnimate)
        {
            if (!_renderingHooked)
            {
                _animationTimer ??= new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
                {
                    Interval = TimeSpan.FromSeconds(FrameInterval)
                };
                _animationTimer.Tick -= OnAnimationTick;
                _animationTimer.Tick += OnAnimationTick;
                _animationClock.Restart();
                _animationTimer.Start();
                _renderingHooked = true;
                _hasRenderingTime = false;
            }
        }
        else
        {
            UnhookRendering();
        }
    }

    private void UnhookRendering()
    {
        if (_renderingHooked)
        {
            _animationTimer?.Stop();
            _animationClock.Stop();
            _renderingHooked = false;
        }

        _hasRenderingTime = false;
        _frameAccumulator = 0d;
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!AnimationEnabled || IsPaused || !IsVisible || !IsLoaded)
        {
            UnhookRendering();
            return;
        }

        var now = _animationClock.Elapsed;

        if (!_hasRenderingTime)
        {
            _lastRenderingTime = now;
            _hasRenderingTime = true;
            return;
        }

        double delta = (now - _lastRenderingTime).TotalSeconds;
        _lastRenderingTime = now;

        if (delta <= 0d)
        {
            return;
        }

        // Ограничиваем дельту, чтобы после подвисания сцена не прыгнула вперёд.
        delta = Math.Min(delta, MaxFrameDelta);
        _time += delta;
        UpdateNightAmount(delta);

        // Накапливаем время и инвалидируем не чаще 30 раз в секунду,
        // даже если монитор работает на 120/144 Гц.
        _frameAccumulator += delta;
        if (_frameAccumulator + 0.0001d < FrameInterval)
        {
            return;
        }

        _frameAccumulator %= FrameInterval;
        InvalidateVisual();
    }

    private void UpdateNightAmount(double delta)
    {
        if (_nightProgress >= 1d)
        {
            _nightAmount = _nightTarget;
            return;
        }

        _nightProgress = Math.Min(1d, _nightProgress + delta / NightTransitionSeconds);

        // Плавный старт и финиш перехода — без рывка в начале и в конце.
        double eased = SmoothStep(_nightProgress);
        _nightAmount = _nightFrom + (_nightTarget - _nightFrom) * eased;

        if (_nightProgress >= 1d)
        {
            _nightAmount = _nightTarget;
        }
    }

    /// <summary>Палитра из трёх опорных точек: день, сумерки и ночь.</summary>
    private sealed class Tint
    {
        public const int Steps = TintSteps;

        private readonly SolidColorBrush[] _brushes;

        public Tint(Color day, Color dusk, Color night)
        {
            _brushes = new SolidColorBrush[Steps];
            for (int i = 0; i < Steps; i++)
            {
                double amount = i / (double)(Steps - 1);
                _brushes[i] = PixelPalette.CreateBrush(Interpolate(day, dusk, night, amount));
            }
        }

        public SolidColorBrush At(double nightAmount)
            => _brushes[PaletteIndex(nightAmount, Steps)];
    }

    private readonly struct CloudStyle
    {
        public CloudStyle(Tint main, Tint shade, Tint light, double opacity)
        {
            Main = main;
            Shade = shade;
            Light = light;
            Opacity = opacity;
        }

        public Tint Main { get; }
        public Tint Shade { get; }
        public Tint Light { get; }
        public double Opacity { get; }
    }

    private readonly struct RidgeStyle
    {
        public RidgeStyle(Tint body, Tint rim, Tint shade)
        {
            Body = body;
            Rim = rim;
            Shade = shade;
        }

        public Tint Body { get; }
        public Tint Rim { get; }
        public Tint Shade { get; }
    }

    private readonly struct CloudPart
    {
        public CloudPart(double x, double y, double width, double height, int tone)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
            Tone = tone;
        }

        public double X { get; }
        public double Y { get; }
        public double Width { get; }
        public double Height { get; }
        public int Tone { get; }
    }

    private readonly struct MoonCrater
    {
        public MoonCrater(double x, double y, int size)
        {
            X = x;
            Y = y;
            Size = size;
        }

        public double X { get; }
        public double Y { get; }
        public int Size { get; }
    }

    private readonly struct Ridge
    {
        public Ridge(Geometry body, Geometry rim, Geometry shade, double[] tops)
        {
            Body = body;
            Rim = rim;
            Shade = shade;
            Tops = tops;
        }

        public Geometry Body { get; }
        public Geometry Rim { get; }
        public Geometry Shade { get; }
        public double[] Tops { get; }
    }

    private readonly struct Star
    {
        public Star(double x, double y, double speed, double phase, double brightness, int size, bool warm)
        {
            X = x;
            Y = y;
            Speed = speed;
            Phase = phase;
            Brightness = brightness;
            Size = size;
            Warm = warm;
        }

        public double X { get; }
        public double Y { get; }
        public double Speed { get; }
        public double Phase { get; }
        public double Brightness { get; }
        public int Size { get; }
        public bool Warm { get; }
    }

    private readonly struct Particle
    {
        public Particle(double x, double y, double phase, double speed, int size)
        {
            X = x;
            Y = y;
            Phase = phase;
            Speed = speed;
            Size = size;
        }

        public double X { get; }
        public double Y { get; }
        public double Phase { get; }
        public double Speed { get; }
        public int Size { get; }
    }

    private readonly struct Firefly
    {
        public Firefly(double x, double y, double phase, double speed, int size)
        {
            X = x;
            Y = y;
            Phase = phase;
            Speed = speed;
            Size = size;
        }

        public double X { get; }
        public double Y { get; }
        public double Phase { get; }
        public double Speed { get; }
        public int Size { get; }
    }

    private readonly struct Cloud
    {
        public Cloud(double x, double y, double speed, double scale, int layer, int variant, double phase)
        {
            X = x;
            Y = y;
            Speed = speed;
            Scale = scale;
            Layer = layer;
            Variant = variant;
            Phase = phase;
        }

        public double X { get; }
        public double Y { get; }
        public double Speed { get; }
        public double Scale { get; }
        public int Layer { get; }
        public int Variant { get; }
        public double Phase { get; }
    }

    private readonly struct ShootingStar
    {
        public ShootingStar(
            double period,
            double offset,
            double duration,
            double startX,
            double startY,
            double deltaX,
            double deltaY)
        {
            Period = period;
            Offset = offset;
            Duration = duration;
            StartX = startX;
            StartY = startY;
            DeltaX = deltaX;
            DeltaY = deltaY;
        }

        public double Period { get; }
        public double Offset { get; }
        public double Duration { get; }
        public double StartX { get; }
        public double StartY { get; }
        public double DeltaX { get; }
        public double DeltaY { get; }
    }

    private readonly struct Tree
    {
        public Tree(double x, double baseY, double scale)
        {
            X = x;
            BaseY = baseY;
            Scale = scale;
        }

        public double X { get; }
        public double BaseY { get; }
        public double Scale { get; }
    }

    private readonly struct Grass
    {
        public Grass(double x, double y, double scale, double phase, bool light)
        {
            X = x;
            Y = y;
            Scale = scale;
            Phase = phase;
            Light = light;
        }

        public double X { get; }
        public double Y { get; }
        public double Scale { get; }
        public double Phase { get; }
        public bool Light { get; }
    }

    private readonly struct Flower
    {
        public Flower(double x, double y, int colorIndex)
        {
            X = x;
            Y = y;
            ColorIndex = colorIndex;
        }

        public double X { get; }
        public double Y { get; }
        public int ColorIndex { get; }
    }

    private readonly struct Dew
    {
        public Dew(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }
}




