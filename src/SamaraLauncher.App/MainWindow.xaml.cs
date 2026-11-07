using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using SamaraLauncher.App.Controls;
using SamaraLauncher.App.ViewModels;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Backups;
using SamaraLauncher.Core.Diagnostics;
using SamaraLauncher.Core.Instances;
using SamaraLauncher.Core.Integrity;
using SamaraLauncher.Core.Install;
using SamaraLauncher.Core.Java;
using SamaraLauncher.Core.Launch;
using SamaraLauncher.Core.Logs;
using SamaraLauncher.Core.Mods;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Packwiz;
using SamaraLauncher.Core.Resources;
using SamaraLauncher.Core.ServerHost;
using SamaraLauncher.Core.Servers;
using SamaraLauncher.Core.Tools;
using SamaraLauncher.Core.Update;
using SamaraLauncher.Core.Worlds;
using JavaLocatorImpl = SamaraLauncher.Core.Java.JavaLocator;

namespace SamaraLauncher.App;

public partial class MainWindow : Window
{
    private static readonly string AppVersion = LauncherVersion.Current;

    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmSysCommand = 0x0112;
    private const int ScMaximize = 0xF030;
    private const int HtCaption = 2;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;

    private const double DefaultWindowWidth = 1100d;
    private const double DefaultWindowHeight = 680d;
    private const double WindowScreenMargin = 72d;
    private const double MinimumWindowWidth = 960d;
    private const double MinimumWindowHeight = 600d;

    private static readonly string[] WorldColors =
    {
        "#5FBE4A", "#58C7F0", "#F5C542", "#E5533D", "#7CD964", "#B07CFF"
    };

    private static readonly string[] BootTips =
    {
        "Совет: первый запуск дольше обычного — скачиваются библиотеки.",
        "Совет: 6–8 ГБ памяти достаточно для 87 модов.",
        "Совет: перед обновлением сборки лаунчер делает бэкап.",
        "Совет: вылетел мод? Открой вкладку «Краши» — там будет виновник.",
        "Совет: ник можно сменить прямо на главной — кликни по нему."
    };

    private readonly LauncherPaths _paths = new();
    private readonly InstanceStore _store = new();
    private readonly JavaLocatorImpl _javaLocator = new();
    private readonly CrashReportParser _crashParser = new();
    private readonly LogService _logService = new();
    private readonly ServerListService _serverList = new();
    private readonly ServerPinger _serverPinger = new();
    private readonly ResourcePackService _resourcePacks = new();
    private readonly ShaderPackService _shaderPacks = new();
    private readonly DoctorService _doctor = new();
    private readonly DoctorFixService _doctorFix = new();

    private LauncherConfig _config = new();
    private InstanceProfile _instance = new();
    private BackupService _backups = null!;
    private IntegrityService _integrity = null!;
    private ModManager _mods = null!;
    private WorldService _worlds = null!;
    private WorldBackupScheduler _backupScheduler = null!;
    private InstanceInstaller _installer = null!;
    private PackwizService _packwiz = null!;
    private GameLauncher _launcher = null!;
    private CrashHelper _crashHelper = null!;
    private LauncherUpdateService _updates = null!;

    private readonly ObservableCollection<ModRow> _modRows = new();
    private readonly List<ModRow> _allMods = new();
    private readonly ObservableCollection<ModUpdateRow> _modUpdateRows = new();
    private readonly ObservableCollection<ModIssueRow> _modIssueRows = new();
    private readonly List<ModUpdateInfo> _modUpdates = new();
    private readonly ObservableCollection<WorldRow> _worldRows = new();
    private readonly ObservableCollection<WorldBackupRow> _restoreBackupRows = new();
    private readonly ObservableCollection<InstanceRow> _instanceRows = new();
    private readonly ObservableCollection<ServerRow> _serverRows = new();
    private readonly ObservableCollection<DoctorCheckRow> _doctorRows = new();
    private readonly ObservableCollection<PackRow> _packRows = new();
    private readonly ObservableCollection<ServerWorldRow> _serverWorldRows = new();
    private readonly ObservableCollection<ServerWorldBackupRow> _serverWorldBackupRows = new();
    private ModAnalysisReport? _modAnalysis;
    private WorldRow? _restoreWorld;
    private WorldBackupOptions _worldBackupOptions = new();

    private readonly Dictionary<string, JavaInfo?> _javaInfoCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CachedServerPing> _serverPingCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _serverPingCacheLock = new();
    private Task<JavaInfo?>? _javaProbeTask;
    private string? _javaProbeKey;
    private int _instanceJavaScanVersion;
    private string? _modsFingerprint;
    private bool _modsDirty = true;
    private bool _modsLoading;
    private int? _modsCountHint;
    private int _modsScanVersion;
    private DispatcherTimer? _modFilterTimer;
    private int _worldsScanVersion;
    private bool _worldsDirty = true;
    private int _logsScanVersion;
    private CancellationTokenSource? _logsCts;
    private string _currentPage = "home";
    private bool _suppressNavigation;
    private bool _configLoadFailed;
    private bool _startupBlocked;

    private static readonly TimeSpan ServerPingCacheDuration = TimeSpan.FromSeconds(45);

    private GameProcess? _game;
    private JavaInfo? _javaInfo;
    private LauncherUpdateInfo? _pendingUpdate;
    private Button? _homeUpdateButton;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _javaCts;
    private CancellationTokenSource? _serversCts;
    private CancellationTokenSource? _doctorCts;
    private HwndSource? _hwndSource;
    private InstanceManager _instances = null!;
    private JavaManager _java = null!;
    private string? _selectedInstanceId;
    private InstanceFormMode _instanceFormMode;
    private bool _instancesBusy;
    private bool _busy;
    private bool _settingsLoading;
    private bool _applyingTheme;
    private bool _inSizeMove;
    private bool _panoramaAnimationBeforeSizeMove;
    private bool _nickEditActive;
    private bool _serversBusy;
    private bool _doctorBusy;
    private bool _resourcesBusy;
    private int _resourcesVersion;
    private string? _pendingServerAddress;
    private ServerRow? _selectedServer;
    private DoctorReport? _doctorReport;
    private string? _doctorReportInstanceId;

    private ServerHostService _serverHost = null!;
    private ServerWorldService _serverWorlds = null!;
    private HostedServerProfile _hostProfile = new();
    private ServerProcess? _hostProcess;
    private CancellationTokenSource? _hostCts;
    private DispatcherTimer? _hostUptimeTimer;
    private DispatcherTimer? _hostPingTimer;
    private readonly System.Text.StringBuilder _hostConsole = new();
    private DateTime _hostStartedAt;
    private bool _hostBusy;
    private bool _hostReady;
    private bool _hostPingBusy;
    private bool _hostSuppressSettings;
    private bool _hostStopping;
    private bool _hostPortInUse;
    private bool _closingAfterServerStop;
    private int _hostWorldsVersion;
    private string? _hostLanAddress;
    private string? _hostPublicAddress;

    internal bool StartupBlocked => _startupBlocked;

    public MainWindow()
    {
        App.StartupTrace("MainWindow ctor begin");
        InitializeComponent();
        ApplyOptimalWindowSize();
        App.StartupTrace("InitializeComponent done");

        _paths.EnsureCreated();
        App.StartupTrace("paths done");
        LoadConfiguration();
        App.StartupTrace("config done");
        WireServices();
        App.StartupTrace("services done");

        TitleVersionText.Text = $"v{AppVersion}";
        AboutText.Text = $"Launcher {AppVersion} · Лаунчер\nMinecraft Java Edition · Vanilla / Forge";

        ModsList.ItemsSource = _installedContent;

        ModUpdatesList.ItemsSource = _modUpdateRows;
        ModDetailsList.ItemsSource = _modIssueRows;
        WorldGrid.ItemsSource = _worldRows;
        WorldRestoreList.ItemsSource = _restoreBackupRows;
        HostWorldsList.ItemsSource = _serverWorldRows;
        HostBackupsList.ItemsSource = _serverWorldBackupRows;
        InstancesItems.ItemsSource = _instanceRows;
        ServersList.ItemsSource = _serverRows;
        DoctorCheckList.ItemsSource = _doctorRows;
        PacksList.ItemsSource = _packRows;

        SourceInitialized += MainWindow_SourceInitialized;
        StateChanged += MainWindow_StateChanged;
        PreviewMouseDown += HomeWindow_PreviewMouseDown;
        Activated += MainWindow_Activated;
        Deactivated += MainWindow_Deactivated;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        Closed += MainWindow_Closed;
        App.StartupTrace("MainWindow ctor end");
    }

    private void LoadConfiguration()
    {
        try
        {
            _config = _store.LoadOrDefault();
            _config.LauncherUpdateUrl ??= LauncherConfig.DefaultUpdateUrl;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or LauncherException or System.Text.Json.JsonException)
        {
            // Не подменяем занятый/недоступный конфиг дефолтным: иначе
            // TrySaveConfig затрёт реальные сборки. Показываем ошибку и
            // завершаем запуск, не записывая ничего на диск.
            _configLoadFailed = true;
            _startupBlocked = true;
            _config = new LauncherConfig();
            _instance = new InstanceProfile
            {
                Id = "",
                Name = "Настройки недоступны",
                GameDir = "",
                Username = "Player"
            };

            MessageBox.Show(
                "Не удалось прочитать config.json:\n\n" + ex.Message +
                "\n\nЛаунчер закроется, чтобы не перезаписать настройки. " +
                "Проверьте, что файл не занят другой программой, и запустите снова.",
                "Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        if (_config.Instances.Count == 0)
        {
            var instance = new InstanceProfile
            {
                Id = "samara",
                Name = "Сборка 1.20.1",
                GameDir = LauncherPaths.DefaultInstanceGameDir("samara"),
                PackUrl = ""
            };

            _config.Instances.Add(instance);
            _config.LastInstanceId = instance.Id;
            _instance = instance;
            TrySaveConfig();
        }

        _instance = _config.Find(_config.LastInstanceId ?? "") ?? _config.Instances[0];

        if (string.IsNullOrWhiteSpace(_instance.PackUrl))
        {
            var localPack = TryFindLocalPack(_instance.GameDir);
            if (localPack is not null)
            {
                _instance.PackUrl = localPack;
            }
        }

        if (!string.Equals(_config.LastInstanceId, _instance.Id, StringComparison.Ordinal))
        {
            _config.LastInstanceId = _instance.Id;
            TrySaveConfig();
        }

        LoadHostedServerProfile();
    }

    private void LoadHostedServerProfile()
    {
        _config.HostedServers ??= new List<HostedServerProfile>();

        _hostProfile = _config.FindHostedServer(_config.LastHostedServerId ?? "")
                       ?? _config.HostedServers.FirstOrDefault()
                       ?? CreateDefaultHostedServer();

        if (string.IsNullOrWhiteSpace(_hostProfile.ServerDir))
        {
            _hostProfile.ServerDir = ServerHostService.FindExistingServer(_instance)
                                     ?? DefaultHostServerDir();
            TrySaveConfig();
        }

        _config.LastHostedServerId = _hostProfile.Id;
    }

    private HostedServerProfile CreateDefaultHostedServer()
    {
        var server = new HostedServerProfile
        {
            Id = "server",
            Name = "Сервер",
            InstanceId = _instance.Id,
            ServerDir = ServerHostService.FindExistingServer(_instance) ?? DefaultHostServerDir(),
            PackUrl = _instance.PackUrl,
            MemoryMb = Math.Max(2048, _instance.MemoryMb),
            MinMemoryMb = Math.Max(512, _instance.MinMemoryMb),
            Port = 25565,
            Motd = "Сервер 1.20.1",
            MaxPlayers = 6,
            OnlineMode = false,
            Whitelist = false,
            JavaPath = "auto"
        };

        TryLoadExistingServerProperties(server);

        _config.HostedServers.Add(server);
        _config.LastHostedServerId = server.Id;
        TrySaveConfig();
        return server;
    }

    private static void TryLoadExistingServerProperties(HostedServerProfile server)
    {
        if (server is null || string.IsNullOrWhiteSpace(server.ServerDir))
        {
            return;
        }

        try
        {
            var serverDir = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(server.ServerDir.Trim().Trim('"')));
            var path = Path.Combine(serverDir, "server.properties");

            if (!File.Exists(path))
            {
                return;
            }

            var properties = ServerProperties.Load(path);

            server.Port = properties.GetInt("server-port", server.Port);
            server.MaxPlayers = properties.GetInt("max-players", server.MaxPlayers);
            server.OnlineMode = properties.GetBool("online-mode", server.OnlineMode);
            server.Whitelist = properties.GetBool("white-list", server.Whitelist);

            var motd = properties.Get("motd", server.Motd);
            if (!string.IsNullOrWhiteSpace(motd))
            {
                server.Motd = motd;
            }
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or LauncherException
                or ArgumentException
                or NotSupportedException
                or PathTooLongException
                or System.Security.SecurityException)
        {
            // Битый server.properties не должен мешать созданию профиля сервера.
        }
    }

    private string DefaultHostServerDir()
    {
        if (string.IsNullOrWhiteSpace(_instance.GameDir))
        {
            return "server";
        }

        try
        {
            return Path.Combine(_instance.GameDir, "server");
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "server";
        }
    }

    private void WireServices()
    {
        _backups = new BackupService(_paths);
        _integrity = new IntegrityService();
        _mods = new ModManager();
        _worlds = new WorldService(_paths);
        _backupScheduler = new WorldBackupScheduler(_paths, _worlds);
        _installer = new InstanceInstaller(_javaLocator);
        _launcher = new GameLauncher(_javaLocator);
        _crashHelper = new CrashHelper(_crashParser);
        _updates = new LauncherUpdateService(_paths);
        _instances = new InstanceManager(_store, _paths);
        _java = new JavaManager(_paths);
        _serverHost = new ServerHostService(_paths, _java, new ToolLocator(AppContext.BaseDirectory));
        _serverWorlds = new ServerWorldService(
            _paths,
            () => _hostProcess is { IsRunning: true },
            _worlds);
        CreatePackwizService();
    }

    private void CreatePackwizService()
    {
        _packwiz = new PackwizService(
            _javaLocator,
            new ToolLocator(AppContext.BaseDirectory),
            _backups,
            _integrity,
            Math.Clamp(_config.BackupRetention, 1, 50));
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.StartupTrace("Loaded begin");
            InitializeWorkspace();
            ApplyTheme();
            ApplyHomeCover();
            PopulateSettingsFields();
            UpdatePanoramaPause();

            // Даём окну отрисоваться до фоновой проверки Java.
            await Dispatcher.Yield(DispatcherPriority.Background);

            _ = EnsureJavaInfoAsync();
            UpdateDashboardText();
            _ = UpdateModsCountHintAsync();
            App.StartupTrace("home/dashboard done");

            if (_config.CheckLauncherUpdates && !string.IsNullOrWhiteSpace(_config.LauncherUpdateUrl))
            {
                _ = CheckUpdatesSilentlyAsync();
            }

            _ = RunStartupAutoBackupAsync();
            App.StartupTrace("Loaded end");
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[СТАРТ] " + ex);
            App.StartupTrace("Loaded failed: " + ex.Message);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup-error.log"), ex.ToString());
        }
    }

    // ================= ТЕМА И ОКНО =================

    private void ApplyTheme()
    {
        var night = _config.Theme.Equals("night", StringComparison.OrdinalIgnoreCase) ||
                    (_config.Theme.Equals("auto", StringComparison.OrdinalIgnoreCase) &&
                     (DateTime.Now.Hour >= 18 || DateTime.Now.Hour < 6));

        Panorama.IsNight = night;
        Panorama.ParticlesEnabled = _config.ParticlesEnabled;
        ApplyWorkspaceTheme(night);

        _applyingTheme = true;
        try
        {
            ThemeNight.IsChecked = night && !_config.Theme.Equals("auto", StringComparison.OrdinalIgnoreCase);
            ThemeDay.IsChecked = !night && !_config.Theme.Equals("auto", StringComparison.OrdinalIgnoreCase);
            ThemeAuto.IsChecked = _config.Theme.Equals("auto", StringComparison.OrdinalIgnoreCase);
            ParticlesToggle.IsChecked = _config.ParticlesEnabled;
            CloseOnLaunchToggle.IsChecked = _config.CloseOnLaunch;
        }
        finally
        {
            _applyingTheme = false;
        }
    }

    private void ApplyOptimalWindowSize()
    {
        var workArea = SystemParameters.WorkArea;
        var maxWidth = Math.Max(1d, workArea.Width - WindowScreenMargin);
        var maxHeight = Math.Max(1d, workArea.Height - WindowScreenMargin);

        // На маленьких экранах ужимаем окно под рабочую область, но не ниже
        // разумного минимума. Если сама область меньше минимума — помещаемся в неё.
        var minWidth = Math.Min(MinimumWindowWidth, maxWidth);
        var minHeight = Math.Min(MinimumWindowHeight, maxHeight);

        Width = Math.Clamp(DefaultWindowWidth, minWidth, maxWidth);
        Height = Math.Clamp(DefaultWindowHeight, minHeight, maxHeight);
        Left = workArea.Left + (workArea.Width - Width) / 2d;
        Top = workArea.Top + (workArea.Height - Height) / 2d;
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Normal;
            ApplyOptimalWindowSize();
        }

        UpdatePanoramaPause();
    }

    private void MainWindow_Activated(object? sender, EventArgs e) => UpdatePanoramaPause();

    private void MainWindow_Deactivated(object? sender, EventArgs e)
    {
        SetQuickBuildOpen(false);
        UpdatePanoramaPause();
    }

    private void UpdatePanoramaPause()
    {
        if (Panorama is null)
        {
            return;
        }

        var overlayVisible =
            BootOverlay?.Visibility == Visibility.Visible ||
            InstancesOverlay?.Visibility == Visibility.Visible ||
            ModDetailsOverlay?.Visibility == Visibility.Visible ||
            ModUpdatesOverlay?.Visibility == Visibility.Visible ||
            WorldRestoreOverlay?.Visibility == Visibility.Visible;

        Panorama.IsPaused =
            WindowState == WindowState.Minimized ||
            overlayVisible;
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _hwndSource = HwndSource.FromHwnd(handle);
        _hwndSource?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmSysCommand:
                if ((wParam.ToInt32() & 0xFFF0) == ScMaximize)
                {
                    handled = true;
                }

                break;

            case WmNcLButtonDblClk:
                if (wParam.ToInt32() == HtCaption)
                {
                    handled = true;
                }

                break;

            case WmEnterSizeMove:
                if (!_inSizeMove)
                {
                    _inSizeMove = true;
                    _panoramaAnimationBeforeSizeMove = Panorama.AnimationEnabled;
                    Panorama.AnimationEnabled = false;
                }

                break;

            case WmExitSizeMove:
                if (_inSizeMove)
                {
                    _inSizeMove = false;
                    Panorama.AnimationEnabled = _panoramaAnimationBeforeSizeMove;
                }

                break;
        }

        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject))
        {
            return;
        }

        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        // Нативное перетаскивание: Windows двигает окно в своём цикле,
        // без DragMove и лишних перерисовок WPF на каждый пиксель.
        ReleaseCapture();
        SendMessage(handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
        e.Handled = true;
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBoxBase or ScrollBar or Thumb or Slider or ComboBox or PasswordBox)
            {
                return true;
            }

            source = source switch
            {
                Visual => VisualTreeHelper.GetParent(source),
                FrameworkContentElement content => content.Parent,
                _ => null
            };
        }

        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        var night = !Panorama.IsNight;
        _config.Theme = night ? "night" : "day";
        ApplyTheme();
        TrySaveConfig();
    }

    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _settingsLoading || _applyingTheme)
        {
            return;
        }

        if (sender is RadioButton { Tag: string theme })
        {
            _config.Theme = theme;
            ApplyTheme();
            TrySaveConfig();
        }
    }

    private void ParticlesToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _settingsLoading || _applyingTheme)
        {
            return;
        }

        _config.ParticlesEnabled = ParticlesToggle.IsChecked == true;
        Panorama.ParticlesEnabled = _config.ParticlesEnabled;
        TrySaveConfig();
    }

    private void CloseOnLaunch_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _settingsLoading || _applyingTheme)
        {
            return;
        }

        _config.CloseOnLaunch = CloseOnLaunchToggle.IsChecked == true;
        TrySaveConfig();
    }

    // ================= НАВИГАЦИЯ =================

    /// <summary>
    /// Выбор пункта меню клавиатурой или через UI Automation тоже переключает страницу.
    /// </summary>
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressNavigation || !IsLoaded || HomeView is null)
        {
            return;
        }

        if (sender is RadioButton { Tag: string page })
        {
            ShowPage(page);
        }
    }

    private void ShowLegacyPage(string page)
    {
        if (string.Equals(_currentPage, page, StringComparison.Ordinal))
        {
            return;
        }

        _currentPage = page;
        HomeView.Visibility = page == "home" ? Visibility.Visible : Visibility.Collapsed;
        ModsView.Visibility = page == "mods" ? Visibility.Visible : Visibility.Collapsed;
        WorldsView.Visibility = page == "worlds" ? Visibility.Visible : Visibility.Collapsed;
        LogsView.Visibility = page == "logs" ? Visibility.Visible : Visibility.Collapsed;
        ServersView.Visibility = page == "servers" ? Visibility.Visible : Visibility.Collapsed;
        HostView.Visibility = page == "host" ? Visibility.Visible : Visibility.Collapsed;
        DoctorView.Visibility = page == "doctor" ? Visibility.Visible : Visibility.Collapsed;
        ResourcesView.Visibility = page == "resources" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;
        FadeContentIn(page switch
        {
            "mods" => ModsView, "worlds" => WorldsView, "logs" => LogsView,
            "servers" => ServersView, "host" => HostView, "doctor" => DoctorView,
            "resources" => ResourcesView, "settings" => SettingsView, _ => HomeView
        });

        _suppressNavigation = true;
        try
        {
            switch (page)
            {
                case "mods":
                    NavMods.IsChecked = true;
                    break;
                case "worlds":
                    NavWorlds.IsChecked = true;
                    break;
                case "logs":
                    NavLogs.IsChecked = true;
                    break;
                case "servers":
                    NavServers.IsChecked = true;
                    break;
                case "host":
                    NavHost.IsChecked = true;
                    break;
                case "doctor":
                    NavDoctor.IsChecked = true;
                    break;
                case "resources":
                    NavResources.IsChecked = true;
                    break;
                case "settings":
                    NavSettings.IsChecked = true;
                    break;
                default:
                    NavHome.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _suppressNavigation = false;
        }

        PageScroll.ScrollToTop();

        switch (page)
        {
            case "mods":
                _ = EnsureModsLoadedAsync();
                _ = RefreshResourcesAsync(force: true);
                CatalogTab_Checked(CatalogTab, new RoutedEventArgs());
                break;
            case "worlds":
                _ = RefreshWorldsAsync();
                break;
            case "logs":
                _ = RefreshLogsAsync();
                break;
            case "servers":
                _ = RefreshServersAsync();
                break;
            case "host":
                _ = RefreshHostPageAsync();
                break;
            case "doctor":
                PrepareDoctorPage();
                break;
            case "resources":
                _ = RefreshResourcesAsync();
                break;
            case "settings":
                PopulateSettingsFields();
                break;
            default:
                _ = EnsureJavaInfoAsync();
                UpdateDashboardText();
                break;
        }
    }

    private void OpenMods_Click(object sender, RoutedEventArgs e)
    {
        NavMods.IsChecked = true;
        ShowPage("mods");
    }

    // ================= ПРОФИЛЬ =================

    private void NickEditStart_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_nickEditActive)
        {
            return;
        }

        e.Handled = true;
        _nickEditActive = true;
        NavNickEdit.Text = _instance.Username;
        NavNickText.Visibility = Visibility.Collapsed;
        NavNickEdit.Visibility = Visibility.Visible;
        NavNickEdit.Focus();
        NavNickEdit.SelectAll();
    }

    private void NavNickEdit_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            CommitNickEdit();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelNickEdit();
        }
    }

    private void NavNickEdit_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        CommitNickEdit();
    }

    private void CommitNickEdit()
    {
        if (!_nickEditActive)
        {
            return;
        }

        var nick = (NavNickEdit.Text ?? "").Trim();
        EndNickEdit();

        if (string.Equals(nick, _instance.Username, StringComparison.Ordinal))
        {
            return;
        }

        if (nick.Length is < 3 or > 16)
        {
            ShowError("Ник", "Ник должен содержать от 3 до 16 символов.");
            return;
        }

        _instance.Username = nick;
        _instance.Uuid = OfflineUuid.FromUsername(nick);
        TrySaveConfig();

        NavNickText.Text = nick;
        NavAvatar.Username = nick;
        SettingsAvatar.Username = nick;
        NicknameBox.Text = nick;
        ShowToast("Ник обновлён", nick, "#7CD964");
    }

    private void CancelNickEdit()
    {
        if (_nickEditActive)
        {
            EndNickEdit();
        }
    }

    private void EndNickEdit()
    {
        _nickEditActive = false;
        NavNickEdit.Visibility = Visibility.Collapsed;
        NavNickText.Text = _instance.Username;
        NavNickText.Visibility = Visibility.Visible;
    }

    // ================= МЕНЕДЖЕР СБОРОК =================

    private enum InstanceFormMode
    {
        None,
        Create,
        Rename,
        Duplicate,
        Delete,
        Java
    }

    private sealed class TemplateOption
    {
        public TemplateOption(string? id, string label)
        {
            Id = id;
            Label = label;
        }

        public string? Id { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }

    private void AddInstance_Click(object sender, RoutedEventArgs e) => OpenInstancesOverlay();

    private void OpenInstancesOverlay()
    {
        if (InstancesOverlay.Visibility == Visibility.Visible)
        {
            return;
        }

        _selectedInstanceId = _instance.Id;
        HideInstanceForm();
        RefreshInstanceList();
        InstancesStatusText.Text = "";
        InstancesOverlay.Visibility = Visibility.Visible;
        InstancesScroll.ScrollToTop();
        UpdatePanoramaPause();
        _ = WarmInstanceJavaAsync();
    }

    private void InstancesClose_Click(object sender, RoutedEventArgs e) => CloseInstancesOverlay();

    private void InstancesOverlay_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        e.Handled = true;

        if (_instanceFormMode != InstanceFormMode.None)
        {
            InstanceFormCancel_Click(sender, e);
            return;
        }

        CloseInstancesOverlay();
    }

    private void CloseInstancesOverlay()
    {
        _javaCts?.Cancel();
        InstancesOverlay.Visibility = Visibility.Collapsed;
        UpdatePanoramaPause();
    }

    private void RefreshInstanceList()
    {
        var instances = _instances.List(_config);

        if (string.IsNullOrWhiteSpace(_selectedInstanceId) ||
            _config.Find(_selectedInstanceId) is null)
        {
            _selectedInstanceId = _config.Find(_instance.Id)?.Id
                                  ?? instances.FirstOrDefault()?.Id;
        }

        _instanceRows.Clear();
        foreach (var instance in instances)
        {
            _instanceRows.Add(CreateInstanceRow(instance));
        }

        InstancesCountText.Text =
            $"{instances.Count} {Plural(instances.Count, "СБОРКА", "СБОРКИ", "СБОРОК")}";

        UpdateInstanceActions();
        UpdateInstanceDetails();
    }

    private InstanceRow CreateInstanceRow(InstanceProfile instance)
    {
        var java = GetCachedJavaFor(instance);
        var pending = IsJavaCachePending(instance);
        var javaText = java is not null
            ? $"Java {java.MajorVersion}"
            : pending ? "проверка Java..." : "Java не найдена";
        var javaPath = java?.Path;

        if (string.IsNullOrWhiteSpace(javaPath) &&
            !string.IsNullOrWhiteSpace(instance.JavaPath) &&
            !instance.JavaPath.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            javaPath = instance.JavaPath;
        }

        return new InstanceRow(
            instance,
            javaText,
            string.IsNullOrWhiteSpace(javaPath)
                ? pending ? "проверка..." : "не найдена"
                : javaPath,
            string.Equals(instance.Id, _instance.Id, StringComparison.OrdinalIgnoreCase));
    }

    private async Task WarmInstanceJavaAsync()
    {
        try
        {
            await WarmInstanceJavaCoreAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[JAVA] прогрев списка сборок: " + ex.Message);
        }
    }

    private async Task WarmInstanceJavaCoreAsync()
    {
        var version = ++_instanceJavaScanVersion;
        var instances = _instances.List(_config);
        var changed = false;

        foreach (var instance in instances)
        {
            var key = JavaCacheKey(instance);
            if (_javaInfoCache.ContainsKey(key))
            {
                continue;
            }

            var info = await Task.Run(() => ProbeJavaCore(instance));
            if (version != _instanceJavaScanVersion)
            {
                return;
            }

            var current = _config.Find(instance.Id);
            if (current is null ||
                !string.Equals(JavaCacheKey(current), key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (_javaInfoCache.ContainsKey(key))
            {
                continue;
            }

            _javaInfoCache[key] = info;
            changed = true;

            if (string.Equals(current.Id, _instance.Id, StringComparison.OrdinalIgnoreCase))
            {
                _javaInfo = info;
            }
        }

        if (!changed)
        {
            return;
        }

        UpdateDashboardText();

        if (InstancesOverlay.Visibility == Visibility.Visible)
        {
            var selected = _selectedInstanceId;
            RefreshInstanceList();
            _selectedInstanceId = selected;
            UpdateInstanceActions();
            UpdateInstanceDetails();
        }
    }

    private void UpdateInstanceActions()
    {
        var selected = GetSelectedInstance();
        var hasSelection = selected is not null;
        var isActive = selected is not null &&
                       string.Equals(selected.Id, _instance.Id, StringComparison.OrdinalIgnoreCase);

        InstanceCreateButton.IsEnabled = !_instancesBusy;
        InstanceImportButton.IsEnabled = !_instancesBusy;
        InstanceRenameButton.IsEnabled = hasSelection && !_instancesBusy;
        InstanceDuplicateButton.IsEnabled = hasSelection && !_instancesBusy;
        InstanceDeleteButton.IsEnabled = hasSelection && !_instancesBusy;
        InstanceExportButton.IsEnabled = hasSelection && !_instancesBusy;
        InstanceJavaButton.IsEnabled = hasSelection && !_instancesBusy;
        InstanceSelectButton.IsEnabled = hasSelection && !_instancesBusy && !isActive;
        InstanceSelectButton.Content = isActive ? "АКТИВНА" : "ВЫБРАТЬ";
    }

    private void UpdateInstanceDetails()
    {
        var selected = GetSelectedInstance();

        if (selected is null)
        {
            InstanceDetailName.Text = "—";
            InstanceDetailActive.Text = "";
            InstanceDetailFavorite.Visibility = Visibility.Collapsed;
            InstanceDetailVersion.Text = "—";
            InstanceDetailJava.Text = "—";
            InstanceDetailJavaPath.Text = "—";
            InstanceDetailMemory.Text = "—";
            InstanceDetailInstalled.Text = "—";
            InstanceDetailPack.Text = "—";
            InstanceDetailPath.Text = "—";
            InstanceDetailNotes.Visibility = Visibility.Collapsed;
            return;
        }

        var active = string.Equals(selected.Id, _instance.Id, StringComparison.OrdinalIgnoreCase);
        var java = GetCachedJavaFor(selected);
        var javaPending = IsJavaCachePending(selected);

        InstanceDetailName.Text = selected.Name;
        InstanceDetailActive.Text = active ? "● АКТИВНА" : "○ НЕ АКТИВНА";
        InstanceDetailActive.Foreground = (Brush)FindResource(active ? "GreenHiBrush" : "MutedBrush");
        InstanceDetailFavorite.Visibility = selected.IsFavorite ? Visibility.Visible : Visibility.Collapsed;
        InstanceDetailVersion.Text = FormatInstanceVersion(selected);
        InstanceDetailJava.Text = java is not null
            ? $"Java {java.MajorVersion}"
            : javaPending ? "проверка..." : "не найдена";
        InstanceDetailJava.Foreground = (Brush)FindResource(
            java is not null ? "TextBrush" : javaPending ? "GoldBrush" : "RedBrush");
        InstanceDetailJava.ToolTip = java?.Version;
        InstanceDetailJavaPath.Text = !string.IsNullOrWhiteSpace(java?.Path)
            ? java.Path
            : !string.IsNullOrWhiteSpace(selected.JavaPath) &&
              !selected.JavaPath.Equals("auto", StringComparison.OrdinalIgnoreCase)
                ? selected.JavaPath
                : javaPending ? "проверка..." : "не найдена";
        InstanceDetailMemory.Text = $"{selected.MemoryMb / 1024.0:0.#} ГБ";
        InstanceDetailInstalled.Text = selected.IsInstalled ? "установлена" : "не установлена";
        InstanceDetailInstalled.Foreground = (Brush)FindResource(
            selected.IsInstalled ? "GreenHiBrush" : "GoldBrush");

        var pack = selected.PackUrl ?? "";
        InstanceDetailPack.Text = IsRemotePack(pack)
            ? "URL"
            : string.IsNullOrWhiteSpace(pack)
                ? "не задан"
                : File.Exists(pack) ? "локальный" : "путь не найден";
        InstanceDetailPack.ToolTip = pack;
        InstanceDetailPath.Text = string.IsNullOrWhiteSpace(selected.GameDir) ? "—" : selected.GameDir;
        InstanceDetailNotes.Text = selected.Notes;
        InstanceDetailNotes.Visibility = string.IsNullOrWhiteSpace(selected.Notes)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private InstanceProfile? GetSelectedInstance() =>
        string.IsNullOrWhiteSpace(_selectedInstanceId)
            ? null
            : _config.Find(_selectedInstanceId);

    private JavaInfo? GetCachedJavaFor(InstanceProfile instance) =>
        _javaInfoCache.TryGetValue(JavaCacheKey(instance), out var info) ? info : null;

    private bool IsJavaCachePending(InstanceProfile instance) =>
        !_javaInfoCache.ContainsKey(JavaCacheKey(instance));

    private static string JavaCacheKey(InstanceProfile instance) =>
        (instance.Id ?? "") + "\n" + (instance.JavaPath ?? "") + "\n" + (instance.GameDir ?? "");

    private void SetInstanceStatus(string text, bool error)
    {
        InstancesStatusText.Text = text;
        InstancesStatusText.Foreground = (Brush)FindResource(error ? "RedBrush" : "MutedBrush");
    }

    private void SetInstancesBusy(bool busy)
    {
        _instancesBusy = busy;
        InstancesItems.IsEnabled = !busy;
        InstanceFormOkButton.IsEnabled = !busy;
        UpdateInstanceActions();
    }

    private void InstanceItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: InstanceRow row })
        {
            return;
        }

        if (string.Equals(_selectedInstanceId, row.Instance.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _selectedInstanceId = row.Instance.Id;
        HideInstanceForm();
        UpdateInstanceActions();
        UpdateInstanceDetails();
        SetInstanceStatus("", false);
    }

    private void InstanceItem_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: InstanceRow row })
        {
            _selectedInstanceId = row.Instance.Id;
            ActivateSelectedInstance();
            e.Handled = true;
        }
    }

    private void InstanceCreate_Click(object sender, RoutedEventArgs e) => ShowCreateForm();

    private void InstanceRename_Click(object sender, RoutedEventArgs e) => ShowRenameForm();

    private void InstanceDuplicate_Click(object sender, RoutedEventArgs e) => ShowDuplicateForm();

    private void InstanceDelete_Click(object sender, RoutedEventArgs e) => ShowDeleteForm();

    private void InstanceJava_Click(object sender, RoutedEventArgs e) => ShowJavaForm();

    private void ShowCreateForm()
    {
        PrepareForm(
            InstanceFormMode.Create,
            "НОВАЯ СБОРКА",
            "Создастся новая папка игры. Моды, миры и конфиги не копируются.",
            "СОЗДАТЬ",
            danger: false);

        InstanceFormNameBlock.Visibility = Visibility.Visible;
        InstanceFormNameBox.Text = "";
        InstanceFormTemplateBlock.Visibility = Visibility.Visible;

        var options = new List<TemplateOption> { new(null, "Без шаблона") };
        options.AddRange(_instances.List(_config)
            .Select(instance => new TemplateOption(instance.Id, instance.Name)));

        InstanceFormTemplateCombo.ItemsSource = options;
        InstanceFormTemplateCombo.SelectedIndex = 0;
        ShowFormPanel();
        InstanceFormNameBox.Focus();
    }

    private void ShowRenameForm()
    {
        var selected = GetSelectedInstance();
        if (selected is null)
        {
            SetInstanceStatus("Сначала выбери сборку в списке.", true);
            return;
        }

        PrepareForm(
            InstanceFormMode.Rename,
            "ПЕРЕИМЕНОВАТЬ",
            $"Новое имя для «{selected.Name}». Имя должно быть уникальным.",
            "СОХРАНИТЬ",
            danger: false);

        InstanceFormNameBlock.Visibility = Visibility.Visible;
        InstanceFormNameBox.Text = selected.Name;
        ShowFormPanel();
        InstanceFormNameBox.Focus();
        InstanceFormNameBox.SelectAll();
    }

    private void ShowDuplicateForm()
    {
        var selected = GetSelectedInstance();
        if (selected is null)
        {
            SetInstanceStatus("Сначала выбери сборку в списке.", true);
            return;
        }

        PrepareForm(
            InstanceFormMode.Duplicate,
            "ДУБЛИРОВАТЬ",
            $"Создастся копия «{selected.Name}» с отдельной папкой игры.",
            "ДУБЛИРОВАТЬ",
            danger: false);

        InstanceFormNameBlock.Visibility = Visibility.Visible;
        InstanceFormNameBox.Text = selected.Name + " (копия)";
        InstanceFormCheckBlock.Visibility = Visibility.Visible;
        InstanceFormCheck.Content = "копировать mods/config";
        InstanceFormCheck.IsChecked = false;
        ShowFormPanel();
        InstanceFormNameBox.Focus();
        InstanceFormNameBox.SelectAll();
    }

    private void ShowDeleteForm()
    {
        var selected = GetSelectedInstance();
        if (selected is null)
        {
            SetInstanceStatus("Сначала выбери сборку в списке.", true);
            return;
        }

        PrepareForm(
            InstanceFormMode.Delete,
            "УДАЛИТЬ СБОРКУ",
            $"Сборка «{selected.Name}» будет удалена из лаунчера. Это действие нельзя отменить.\n{selected.GameDir}",
            "УДАЛИТЬ",
            danger: true);

        if (IsGameDirInsideInstances(selected))
        {
            InstanceFormCheckBlock.Visibility = Visibility.Visible;
            InstanceFormCheck.Content = "удалить папку игры";
            InstanceFormCheck.IsChecked = false;
        }

        ShowFormPanel();
    }

    private void ShowJavaForm()
    {
        var selected = GetSelectedInstance();
        if (selected is null)
        {
            SetInstanceStatus("Сначала выбери сборку в списке.", true);
            return;
        }

        PrepareForm(
            InstanceFormMode.Java,
            "JAVA ДЛЯ СБОРКИ",
            $"Выбери версию Java для «{selected.Name}». Файлы скачаются в папку runtime инстанса.",
            "УСТАНОВИТЬ",
            danger: false);

        InstanceJavaBlock.Visibility = Visibility.Visible;
        InstanceJavaProgress.Value = 0;
        InstanceJavaStatusText.Text = "Готово к установке.";

        var found = GetCachedJavaFor(selected);
        if (found is null)
        {
            var pending = IsJavaCachePending(selected);
            InstanceJavaFound.Text = pending
                ? "Найденная Java: проверка..."
                : "Найденная Java: не найдена";
            InstanceJavaFound.Foreground = (Brush)FindResource(pending ? "GoldBrush" : "RedBrush");
        }
        else
        {
            InstanceJavaFound.Text = $"Java {found.MajorVersion} · {found.Version}\n{found.Path}";
            InstanceJavaFound.Foreground = (Brush)FindResource("TextBrush");
        }

        InstanceJavaMajorCombo.SelectedIndex = found?.MajorVersion switch
        {
            8 => 0,
            21 => 2,
            _ => 1
        };

        ShowFormPanel();
    }

    private void PrepareForm(
        InstanceFormMode mode,
        string title,
        string description,
        string okText,
        bool danger)
    {
        _instanceFormMode = mode;
        InstanceFormTitle.Text = title;
        InstanceFormDescription.Text = description;
        InstanceFormOkButton.Content = okText;
        InstanceFormOkButton.Variant = danger ? PixelButtonVariant.Danger : PixelButtonVariant.Primary;
        InstanceFormError.Text = "";
        InstanceFormNameBlock.Visibility = Visibility.Collapsed;
        InstanceFormTemplateBlock.Visibility = Visibility.Collapsed;
        InstanceFormCheckBlock.Visibility = Visibility.Collapsed;
        InstanceJavaBlock.Visibility = Visibility.Collapsed;
        InstanceJavaStatusText.Text = "";
        InstanceFormCheck.IsChecked = false;
    }

    private void ShowFormPanel()
    {
        InstanceDetailsPanel.Visibility = Visibility.Collapsed;
        InstanceFormPanel.Visibility = Visibility.Visible;
        InstanceFormPanel.ScrollToTop();
    }

    private void HideInstanceForm()
    {
        _selectCreatedFromMods = false;
        _instanceFormMode = InstanceFormMode.None;
        InstanceFormPanel.Visibility = Visibility.Collapsed;
        InstanceDetailsPanel.Visibility = Visibility.Visible;
        InstanceFormError.Text = "";
    }

    private void InstanceFormCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_javaCts is not null)
        {
            _javaCts.Cancel();
            InstanceJavaStatusText.Text = "Отмена...";
            return;
        }

        HideInstanceForm();
    }

    private static int? GetSelectedJavaMajor(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem { Tag: string tag } &&
            int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major))
        {
            return major;
        }

        return null;
    }

    private async void InstanceFormOk_Click(object sender, RoutedEventArgs e)
    {
        if (_instancesBusy)
        {
            return;
        }

        var selected = GetSelectedInstance();

        switch (_instanceFormMode)
        {
            case InstanceFormMode.Create:
            {
                var name = InstanceFormNameBox.Text.Trim();
                if (name.Length == 0)
                {
                    InstanceFormError.Text = "Укажи имя сборки.";
                    return;
                }

                var templateId = (InstanceFormTemplateCombo.SelectedItem as TemplateOption)?.Id;

                try
                {
                    var created = _instances.Create(_config, name, null, templateId);
                    var selectCreated = _selectCreatedFromMods;
                    _selectedInstanceId = created.Id;
                    HideInstanceForm();
                    RefreshInstanceList();
                    SetInstanceStatus($"Сборка «{created.Name}» создана.", false);
                    ScrollSelectedIntoView();
                    if (selectCreated && !_busy && _game is not { IsRunning: true } && SwitchActiveInstance(created))
                        CloseInstancesOverlay();
                }
                catch (Exception ex)
                {
                    InstanceFormError.Text = ex.Message;
                }

                return;
            }

            case InstanceFormMode.Rename:
            {
                if (selected is null)
                {
                    InstanceFormError.Text = "Сборка не найдена.";
                    return;
                }

                var name = InstanceFormNameBox.Text.Trim();

                try
                {
                    if (!_instances.Rename(_config, selected.Id, name))
                    {
                        InstanceFormError.Text = "Имя пустое или уже занято другой сборкой.";
                        return;
                    }

                    HideInstanceForm();
                    RefreshInstanceList();

                    if (string.Equals(selected.Id, _instance.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        UpdateDashboardText();
                    }

                    SetInstanceStatus("Сборка переименована.", false);
                }
                catch (Exception ex)
                {
                    InstanceFormError.Text = ex.Message;
                }

                return;
            }

            case InstanceFormMode.Duplicate:
            {
                if (selected is null)
                {
                    InstanceFormError.Text = "Сборка не найдена.";
                    return;
                }

                var name = InstanceFormNameBox.Text.Trim();
                var copyGameData = InstanceFormCheck.IsChecked == true;

                SetInstancesBusy(true);
                SetInstanceStatus("Копирование сборки...", false);

                try
                {
                    var sourceId = selected.Id;
                    var copy = await Task.Run(() =>
                        _instances.Duplicate(_config, sourceId, name, copyGameData));

                    _selectedInstanceId = copy.Id;
                    HideInstanceForm();
                    RefreshInstanceList();
                    SetInstanceStatus($"Создана копия «{copy.Name}».", false);
                    ScrollSelectedIntoView();
                }
                catch (Exception ex)
                {
                    InstanceFormError.Text = ex.Message;
                }
                finally
                {
                    SetInstancesBusy(false);
                }

                return;
            }

            case InstanceFormMode.Delete:
            {
                if (selected is null)
                {
                    InstanceFormError.Text = "Сборка не найдена.";
                    return;
                }

                var deleteGameDir = InstanceFormCheckBlock.Visibility == Visibility.Visible &&
                                    InstanceFormCheck.IsChecked == true;
                if (deleteGameDir && MessageBox.Show(this,
                    $"Удалить также папку игры и все её данные?\n\n{selected.GameDir}",
                    "Удаление данных сборки", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    return;
                var sourceId = selected.Id;
                var wasActive = string.Equals(selected.Id, _instance.Id, StringComparison.OrdinalIgnoreCase);

                if (wasActive && _game is { IsRunning: true })
                {
                    InstanceFormError.Text = "Нельзя удалить активную сборку, пока игра запущена.";
                    return;
                }

                SetInstancesBusy(true);
                SetInstanceStatus("Удаление сборки...", false);

                try
                {
                    await Task.Run(() => _instances.Remove(_config, sourceId, deleteGameDir));

                    if (wasActive)
                    {
                        var next = _config.Find(_config.LastInstanceId ?? "")
                                   ?? _config.Instances.FirstOrDefault();

                        if (next is not null)
                        {
                            SwitchActiveInstance(next);
                        }
                    }

                    _selectedInstanceId = _config.Find(_config.LastInstanceId ?? "")?.Id
                                          ?? _config.Instances.FirstOrDefault()?.Id;
                    HideInstanceForm();
                    RefreshInstanceList();
                    SetInstanceStatus("Сборка удалена из лаунчера.", false);
                }
                catch (Exception ex)
                {
                    InstanceFormError.Text = ex.Message;
                }
                finally
                {
                    SetInstancesBusy(false);
                }

                return;
            }

            case InstanceFormMode.Java:
            {
                if (selected is null)
                {
                    InstanceFormError.Text = "Сборка не найдена.";
                    return;
                }

                var major = GetSelectedJavaMajor(InstanceJavaMajorCombo);
                if (major is null)
                {
                    InstanceFormError.Text = "Выбери версию Java.";
                    return;
                }

                var cts = new CancellationTokenSource();
                _javaCts = cts;
                SetInstancesBusy(true);
                SetInstanceStatus("Установка Java...", false);
                InstanceFormError.Text = "";
                InstanceJavaProgress.Value = 0;
                InstanceJavaStatusText.Text = "Подготовка...";

                try
                {
                    var progress = new Progress<LauncherProgress>(value =>
                    {
                        InstanceJavaProgress.Value = value.Percent ?? 0;
                        InstanceJavaStatusText.Text = value.Message;
                    });

                    var info = await _java.EnsureAsync(selected, major.Value, progress, cts.Token);
                    var previousKey = JavaCacheKey(selected);
                    selected.JavaPath = info.Path;
                    _javaInfoCache.Remove(previousKey);
                    _javaInfoCache[JavaCacheKey(selected)] = info;
                    _instances.Save(_config);

                    if (string.Equals(selected.Id, _instance.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        _javaInfo = info;
                        UpdateDashboardText();
                    }

                    HideInstanceForm();
                    RefreshInstanceList();
                    SetInstanceStatus($"Java {info.MajorVersion} установлена и привязана к сборке.", false);
                }
                catch (OperationCanceledException)
                {
                    InstanceJavaStatusText.Text = "Установка отменена.";
                }
                catch (Exception ex)
                {
                    InstanceFormError.Text = ex.Message;
                }
                finally
                {
                    if (ReferenceEquals(_javaCts, cts))
                    {
                        _javaCts = null;
                    }

                    cts.Dispose();
                    SetInstancesBusy(false);
                }

                return;
            }
        }
    }

    private void InstanceSelect_Click(object sender, RoutedEventArgs e) => ActivateSelectedInstance();

    private void ActivateSelectedInstance()
    {
        var selected = GetSelectedInstance();
        if (selected is null)
        {
            SetInstanceStatus("Сначала выбери сборку в списке.", true);
            return;
        }

        if (_game is { IsRunning: true })
        {
            SetInstanceStatus("Нельзя сменить сборку, пока игра запущена.", true);
            return;
        }

        if (_busy)
        {
            SetInstanceStatus("Дождись завершения текущей операции.", true);
            return;
        }

        if (string.Equals(selected.Id, _instance.Id, StringComparison.OrdinalIgnoreCase))
        {
            CloseInstancesOverlay();
            return;
        }

        if (!SwitchActiveInstance(selected))
        {
            return;
        }

        CloseInstancesOverlay();
        ShowToast("Сборка выбрана", _instance.Name, "#7CD964");
    }

    private bool SwitchActiveInstance(InstanceProfile instance)
    {
        if (_configLoadFailed)
        {
            ShowError("Смена сборки", "Конфиг не был загружен. Изменения не сохраняются.");
            return false;
        }

        var previousId = _instance.Id;
        _config.LastInstanceId = instance.Id;

        try
        {
            _instances.Save(_config);
        }
        catch (Exception ex)
        {
            _config.LastInstanceId = previousId;
            ShowError("Смена сборки", ex.Message);
            return false;
        }

        _instance = instance;
        if (_modsSide == "server")
        {
            try { _ = GetModsTarget(); }
            catch (Exception) { _modsSide = "client"; SyncModsSide(); }
        }
        _catalogRequest?.Cancel();
        if (CatalogTab?.IsChecked == true) ScheduleCatalogSearch();
        _javaInfo = GetCachedJavaFor(instance);
        CreatePackwizService();
        InvalidateInstanceData();
        _ = EnsureJavaInfoAsync();
        _ = UpdateModsCountHintAsync();
        UpdateDashboardText();
        return true;
    }

    private async void InstanceImport_Click(object sender, RoutedEventArgs e)
    {
        if (_instancesBusy)
        {
            return;
        }

        bool fromSidebar = _workspaceSection == "instances" && InstancesOverlay.Visibility != Visibility.Visible;
        var dialog = new OpenFileDialog
        {
            Title = "Импорт сборки",
            Filter = "Архив сборки (*.zip)|*.zip|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetInstancesBusy(true);
        SetInstanceStatus("Импорт сборки...", false);

        try
        {
            var path = dialog.FileName;
            var imported = await Task.Run(() => _instances.Import(_config, path));
            _selectedInstanceId = imported.Id;
            HideInstanceForm();
            RefreshInstanceList();
            SetInstanceStatus($"Импортирована сборка «{imported.Name}».", false);
            ScrollSelectedIntoView();
            if (fromSidebar)
            {
                if (_workspaceSection == "instances" && SelectWorkspaceInstance(imported)) OpenPackTab("Моды");
                ShowToast("Сборка импортирована", imported.Name, "#81CB89");
            }
        }
        catch (Exception ex)
        {
            SetInstanceStatus("Импорт: " + ex.Message, true);
            if (fromSidebar) ShowError("Импорт сборки", ex.Message);
        }
        finally
        {
            SetInstancesBusy(false);
        }
    }

    private async void InstanceExport_Click(object sender, RoutedEventArgs e)
    {
        if (_instancesBusy)
        {
            return;
        }

        var selected = GetSelectedInstance();
        if (selected is null)
        {
            SetInstanceStatus("Сначала выбери сборку в списке.", true);
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт сборки",
            Filter = "Архив сборки (*.zip)|*.zip|Все файлы (*.*)|*.*",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = SanitizeFileName(selected.Name) + ".zip"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        SetInstancesBusy(true);
        SetInstanceStatus("Экспорт сборки...", false);

        try
        {
            var target = dialog.FileName;
            var exported = await Task.Run(() => _instances.Export(selected, target, true));
            SetInstanceStatus($"Экспортировано: {exported}", false);
        }
        catch (Exception ex)
        {
            SetInstanceStatus("Экспорт: " + ex.Message, true);
        }
        finally
        {
            SetInstancesBusy(false);
        }
    }

    private bool IsGameDirInsideInstances(InstanceProfile instance)
    {
        try
        {
            var gameDir = Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(instance.GameDir.Trim().Trim('"')));
            var instancesRoot = Path.GetFullPath(_paths.InstancesDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return gameDir.StartsWith(
                instancesRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void ScrollSelectedIntoView()
    {
        if (string.IsNullOrWhiteSpace(_selectedInstanceId))
        {
            return;
        }

        var index = -1;
        for (var position = 0; position < _instanceRows.Count; position++)
        {
            if (string.Equals(
                    _instanceRows[position].Instance.Id,
                    _selectedInstanceId,
                    StringComparison.OrdinalIgnoreCase))
            {
                index = position;
                break;
            }
        }

        if (index < 0)
        {
            return;
        }

        InstancesItems.UpdateLayout();

        if (InstancesItems.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement container)
        {
            container.BringIntoView();
        }
    }

    internal static string FormatInstanceVersion(InstanceProfile instance)
    {
        var baseVersion = instance.BaseMinecraftVersion?.Trim() ?? "";

        if (baseVersion.Length > 0)
        {
            if (instance.IsFabric)
            {
                var fabricVersion = instance.FabricVersion?.Trim() ?? "";
                return fabricVersion.Length == 0
                    ? $"Minecraft {baseVersion} · Fabric"
                    : $"Minecraft {baseVersion} · Fabric {fabricVersion}";
            }

            if (instance.IsOptiFine)
            {
                return $"Minecraft {baseVersion} · OptiFine";
            }

            if (instance.IsForge)
            {
                var forgeVersion = instance.ForgeVersion?.Trim() ?? "";
                return forgeVersion.Length == 0
                    ? $"Minecraft {baseVersion} · Forge"
                    : $"Minecraft {baseVersion} · Forge {forgeVersion}";
            }

            return $"Minecraft {baseVersion}";
        }

        var versionId = instance.VersionId?.Trim() ?? "";
        if (versionId.Length > 0)
        {
            const string forgeMarker = "-forge-";
            var markerIndex = versionId.IndexOf(forgeMarker, StringComparison.OrdinalIgnoreCase);

            if (markerIndex > 0)
            {
                return $"{versionId[..markerIndex]} · Forge {versionId[(markerIndex + forgeMarker.Length)..]}";
            }

            return versionId;
        }

        return string.IsNullOrWhiteSpace(instance.ForgeVersion)
            ? "версия не задана"
            : $"Forge {instance.ForgeVersion}";
    }

    // ================= ГЛАВНАЯ =================

    /// <summary>
    /// Дешёвое обновление главной из уже загруженного состояния: без Java,
    /// без чтения ресурспаков и без сканирования диска.
    /// </summary>
    private void UpdateDashboardText()
    {
        UpdateCatalogInstanceSelector();
        SelectedInstanceName.Text = _instance.Name;

        SelectedInstanceMeta.Text = FormatInstanceVersion(_instance);
        SelectedInstanceName.ToolTip = _instance.Name;

        NavNickText.Text = _instance.Username;
        NavAvatar.Username = _instance.Username;
        SettingsAvatar.Username = _instance.Username;

        RefreshInstanceDiskSpace();
        UpdatePlayButton();
        HomeBuildButton.IsEnabled = !_busy && _game is not { IsRunning: true };
        if (_pendingVersion is not null) ApplyPendingSelection();
    }

    /// <summary>
    /// Явное/принудительное обновление Java и паков. Не вызывается из частых путей.
    /// </summary>
    private async Task RefreshJavaAndResourcesAsync()
    {
        _javaInfo = ProbeJava(force: true);
        UpdateDashboardText();
        await RefreshResourcesAsync();
    }

    private void RefreshInstanceDiskSpace()
    {
        try
        {
            var gameDir = Path.GetFullPath(_instance.GameDir);
            var root = Path.GetPathRoot(gameDir);
            if (!string.IsNullOrWhiteSpace(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    var freeGb = drive.AvailableFreeSpace / 1024d / 1024d / 1024d;
                    InstanceDiskFreeText.Text = $"{freeGb:0.#} ГБ свободно";
                    InstanceDiskFreeText.ToolTip = drive.Name;
                    return;
                }
            }

            InstanceDiskFreeText.Text = "нет данных";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            InstanceDiskFreeText.Text = "нет данных";
        }
    }

    private void UpdatePlayButton()
    {
        if (_busy)
        {
            return;
        }

        if (_game is { IsRunning: true })
        {
            HomeOperationStrip.Visibility = Visibility.Visible;
            PlayButton.Content = "ОСТАНОВИТЬ";
            PlayButton.IconKind = PixelIconKind.Power;
            PlayButton.Variant = PixelButtonVariant.Danger;
            PlayButton.IsEnabled = true;
            return;
        }

        if (_pendingVersion is not null)
        {
            PlayButton.Content = "УСТАНОВИТЬ";
            PlayButton.IconKind = PixelIconKind.Download;
            PlayButton.Variant = PixelButtonVariant.Primary;
            PlayButton.IsEnabled = true;
            return;
        }

        if (!IsVersionInstalled(_instance))
        {
            PlayButton.Content = "УСТАНОВИТЬ";
            PlayButton.IconKind = PixelIconKind.Download;
            PlayButton.Variant = PixelButtonVariant.Primary;
            PlayButton.IsEnabled = true;
            return;
        }

        PlayButton.Content = "Играть";
        PlayButton.IconKind = PixelIconKind.Play;
        PlayButton.Variant = PixelButtonVariant.Primary;
        PlayButton.IsEnabled = true;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_game is { IsRunning: true })
        {
            StopGame();
            return;
        }

        if (_busy)
        {
            return;
        }

        if (_pendingVersion is not null)
        {
            await InstallPendingVersionAsync();
            return;
        }

        await StartGameAsync();
    }

    private async Task StartGameAsync(string? serverAddress = null, bool installOnly = false)
    {
        _pendingServerAddress = string.IsNullOrWhiteSpace(serverAddress) ? null : serverAddress.Trim();
        _busy = true;
        _operationCts = new CancellationTokenSource();
        var ct = _operationCts.Token;

        SetBusy(true);
        ResetProgress();

        var bootStatus = _pendingServerAddress is null
            ? "Проверка установки..."
            : $"Подключение к {_pendingServerAddress}";
        ShowBoot($"ЗАПУСК {_instance.Name}", bootStatus);

        try
        {
            var progress = new Progress<LauncherProgress>(OnProgress);
            var loader = _instance.LoaderKey;
            var loaderLabel = MinecraftLoader.Label(loader);

            await EnsureJavaInfoAsync();

            if (NeedsInstall())
            {
                string javaPath;
                if (loader == MinecraftLoader.Vanilla)
                {
                    var versionId = !string.IsNullOrWhiteSpace(_instance.VersionId)
                        ? _instance.VersionId.Trim()
                        : _instance.MinecraftVersion?.Trim() ?? "";
                    if (versionId.Length == 0)
                    {
                        throw new LauncherException("Не указана версия Minecraft для установки.");
                    }

                    BootStatusText.Text = "Установка Minecraft...";
                    StatusText.Text = BootStatusText.Text;
                    javaPath = await _installer.EnsureVanillaInstalledAsync(_instance, versionId, progress, ct);
                }
                else if (loader == MinecraftLoader.Fabric)
                {
                    var baseVersion = RequireBaseMinecraftVersion(_instance);
                    BootStatusText.Text = $"Установка Minecraft {baseVersion} · Fabric...";
                    StatusText.Text = BootStatusText.Text;
                    javaPath = await _installer.EnsureFabricInstalledAsync(_instance, baseVersion, progress, ct);
                }
                else
                {
                    var baseVersion = RequireBaseMinecraftVersion(_instance);
                    var wasOptiFine = _instance.IsOptiFine;
                    var optiFineVersion = _instance.OptiFineVersion;

                    if (wasOptiFine && string.IsNullOrWhiteSpace(optiFineVersion))
                    {
                        throw new LauncherException(
                            "В профиле OptiFine не указан установленный .jar. " +
                            "Выберите OptiFine заново в списке версий или переустановите версию.");
                    }

                    var forgeVersion = string.IsNullOrWhiteSpace(_instance.ForgeVersion)
                        ? null
                        : _instance.ForgeVersion.Trim();

                    BootStatusText.Text = $"Установка Minecraft {baseVersion} · {loaderLabel}...";
                    StatusText.Text = BootStatusText.Text;

                    var javaFromInstaller = "";
                    try
                    {
                        javaFromInstaller = await _installer.EnsureForgeInstalledAsync(
                            _instance,
                            baseVersion,
                            forgeVersion,
                            progress,
                            ct);
                    }
                    finally
                    {
                        // OptiFine запускается поверх Forge: установщик Forge не должен
                        // превращать профиль в обычный Forge.
                        if (wasOptiFine)
                        {
                            _instance.Loader = MinecraftLoader.OptiFine;
                            _instance.OptiFineVersion = optiFineVersion;
                        }
                    }

                    javaPath = javaFromInstaller;
                }

                var installedJava = await Task.Run(() =>
                {
                    try
                    {
                        return JavaLocatorImpl.TryProbe(javaPath);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                });
                if (installedJava is not null)
                {
                    _javaInfoCache[JavaCacheKey(_instance)] = installedJava;
                    _javaInfo = installedJava;
                }
                else
                {
                    _javaInfo = await Task.Run(() => ProbeJavaCore(_instance));
                    _javaInfoCache[JavaCacheKey(_instance)] = _javaInfo;
                }

                TrySaveConfig();
            }

            if (IsRemotePack(_instance.PackUrl))
            {
                BootStatusText.Text = "Обновление сборки...";
                StatusText.Text = BootStatusText.Text;
                var result = await _packwiz.UpdateAsync(_instance, progress, ct);
                if (!result.Success)
                {
                    throw new LauncherException("packwiz завершился с ошибкой. Подробности — в логах.");
                }

                if (result.Changed)
                {
                    _modsDirty = true;
                    _modsLoading = false;
                    _modsFingerprint = null;
                    _modsScanVersion++;
                    _modUpdates.Clear();
                    _modUpdateRows.Clear();
                    _ = UpdateModsCountHintAsync();
                    ShowToast(
                        "Сборка обновлена",
                        $"Изменено файлов: {result.ChangedFiles.Count}",
                        "#58C7F0");
                }
            }

            if (installOnly)
            {
                StatusText.Text = "Сборка установлена";
                return;
            }
            HomeUnknownProgress.Visibility = Visibility.Visible;
            MainProgress.Visibility = Visibility.Collapsed;
            ProgressPercentText.Text = "";
            StatusText.Text = "Запуск Java… · ожидаем процесс игры";
            BootStatusText.Text = _pendingServerAddress is null
                ? (loader == MinecraftLoader.Vanilla ? "Запуск Minecraft..." : $"Запуск {loaderLabel}...")
                : $"Подключение к {_pendingServerAddress}";
            _game = _launcher.Start(_instance, null, _pendingServerAddress);
            HookGame(_game);

            _instance.LastPlayed = DateTimeOffset.UtcNow;
            TrySaveConfig();

            ProgressPercentText.Text = "";
            StatusText.Text = "Процесс игры работает · загрузка меню не подтверждена";
            HideBoot();
            _busy = false;
            SetBusy(false);

            if (_config.CloseOnLaunch)
            {
                await Task.Delay(900);
                Application.Current.Shutdown();
            }
        }
        catch (OperationCanceledException)
        {
            HideBoot();
        }
        catch (Exception ex)
        {
            HideBoot();
            _homeError = true;
            StatusText.Text = "Ошибка: " + ex.Message;
            if (_pendingServerAddress is null)
            {
                HomeOperationStrip.Visibility = Visibility.Visible;
            }
            else
            {
                ShowToast("Быстрый вход", ex.Message, "#E5533D");
            }
        }
        finally
        {
            _busy = false;
            SetBusy(false);
            _operationCts?.Dispose();
            _operationCts = null;
            _pendingServerAddress = null;
            UpdateDashboardText();

            if (_game is { IsRunning: true } && BootOverlay.Visibility == Visibility.Visible)
            {
                _ = HideBootAfterDelayAsync();
            }
        }
    }

    private async Task HideBootAfterDelayAsync()
    {
        await Task.Delay(1800);
        if (_game is { IsRunning: true })
        {
            HideBoot();
        }
    }

    private void StopGame()
    {
        if (_game is not { IsRunning: true })
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "Остановить игру? Несохранённый прогресс может потеряться.",
            "Launcher",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _game.Kill();
            ShowToast("Игра остановлена", "Процесс Minecraft завершён.", "#E5533D");
        }
        catch (Exception ex)
        {
            ShowError("Остановка игры", ex.Message);
        }
    }

    private void HookGame(GameProcess game)
    {
        game.LineReceived += line =>
        {
            if (line.IsError && !string.IsNullOrWhiteSpace(line.Text))
            {
                Debug.WriteLine("[ИГРА] " + Trim(line.Text, 180));
            }
        };

        game.Exited += code =>
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                var process = _game;
                _game = null;
                process?.Dispose();

                MainProgress.ShowText = false;
                MainProgress.Value = 0;
                BootProgress.Value = 0;
                ProgressPercentText.Text = "";
                StatusText.Text = code == 0 ? "Игра завершена" : "Игра вылетела — смотрите логи и краши";
                _worldsDirty = true;

                UpdateDashboardText();
                HideBoot();
                HomeOperationStrip.Visibility = code == 0 ? Visibility.Collapsed : Visibility.Visible;
                _homeError = code != 0;

                if (code != 0)
                {
                    ShowToast("Игра вылетела", "Открой «Логи → Краши», чтобы увидеть причину.", "#E5533D");
                    NavLogs.IsChecked = true;
                    ShowPage("logs");
                    ShowCrashTab();
                }
            });
        };
    }

    private static string RequireBaseMinecraftVersion(InstanceProfile instance)
    {
        var version = instance.BaseMinecraftVersion?.Trim() ?? "";
        if (version.Length == 0)
        {
            throw new LauncherException("Не указана версия Minecraft для установки.");
        }

        return version;
    }

    private bool NeedsInstall()
    {
        if (!IsVersionInstalled(_instance))
        {
            return true;
        }

        if (_javaInfo is null)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(_instance.VersionId))
        {
            return true;
        }

        var versionJson = Path.Combine(
            _instance.GameDir,
            "versions",
            _instance.VersionId,
            _instance.VersionId + ".json");

        return !File.Exists(versionJson);
    }

    private void UpdatePack_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instance.PackUrl))
        {
            ShowError("Обновление сборки", "Не задан pack.toml. Укажи URL или локальный путь в настройках.");
            ShowPage("settings");
            return;
        }

        _ = RunBusyAsync("ОБНОВЛЕНИЕ СБОРКИ", "Подготовка packwiz...", async ct =>
        {
            var result = await _packwiz.UpdateAsync(_instance, new Progress<LauncherProgress>(OnProgress), ct);
            if (!result.Success)
            {
                throw new LauncherException("packwiz завершился с ошибкой. Подробности — в логах.");
            }

            var message = result.Changed
                ? $"Обновлено файлов: {result.ChangedFiles.Count}"
                : "Сборка уже актуальна.";

            if (!string.IsNullOrWhiteSpace(result.BackupPath))
            {
                message += $"\nБэкап: {result.BackupPath}";
            }

            if (result.Warnings.Count > 0)
            {
                message += "\n" + string.Join("\n", result.Warnings.Take(3));
            }

            ShowToast("Сборка обновлена", message, "#58C7F0");
            _modsDirty = true;
            _modsLoading = false;
            _modsFingerprint = null;
            _modsScanVersion++;
            _modUpdates.Clear();
            _modUpdateRows.Clear();
            _ = UpdateModsCountHintAsync();
            if (string.Equals(_currentPage, "mods", StringComparison.Ordinal))
            {
                await RefreshModsAsync(force: true);
            }
        });
    }

    private void Verify_Click(object sender, RoutedEventArgs e)
    {
        _ = RunBusyAsync("ПРОВЕРКА ЦЕЛОСТНОСТИ", "Сканирование файлов...", async ct =>
        {
            var report = await _integrity.ScanAsync(_instance, new Progress<LauncherProgress>(OnProgress), ct);
            ShowReport(report);
        });
    }

    private void ShowReport(IntegrityReport report)
    {
        var lines = new List<string>
        {
            $"Проверено файлов: {report.CheckedFiles}",
            $"Отсутствует: {report.Missing.Count}",
            $"Изменено: {report.Modified.Count}",
            $"Лишних: {report.Extra.Count}",
            $"Защищённых: {report.Protected.Count} (целы: {(report.ProtectedIntact ? "да" : "нет")})"
        };

        if (report.Missing.Count > 0)
        {
            lines.Add("");
            lines.Add("Отсутствуют:");
            lines.AddRange(report.Missing.Take(8).Select(item => "  " + item.Path));
        }

        if (report.Modified.Count > 0)
        {
            lines.Add("");
            lines.Add("Изменены:");
            lines.AddRange(report.Modified.Take(8).Select(item => "  " + item.Path));
        }

        var text = string.Join(Environment.NewLine, lines);
        StatusText.Text = report.IsHealthy
            ? $"Проверено {report.CheckedFiles} файлов — расхождений нет."
            : $"Проверено {report.CheckedFiles}: отсутствует {report.Missing.Count}, изменено {report.Modified.Count}.";

        ShowToast(
            report.IsHealthy ? "Проверка пройдена" : "Найдены проблемы",
            text,
            report.IsHealthy ? "#5FBE4A" : "#E5533D");
    }

    private async Task RunBusyAsync(string title, string initial, Func<CancellationToken, Task> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _operationCts = new CancellationTokenSource();
        SetBusy(true);
        ResetProgress();
        ShowBoot(title, initial);

        try
        {
            await action(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            ShowToast("Отменено", "Операция остановлена.", "#F5C542");
        }
        catch (Exception ex)
        {
            _homeError = true;
            StatusText.Text = "Ошибка: " + ex.Message;
            HomeOperationStrip.Visibility = Visibility.Visible;
        }
        finally
        {
            _busy = false;
            SetBusy(false);
            HideBoot();
            _operationCts?.Dispose();
            _operationCts = null;
            UpdateDashboardText();
        }
    }

    private void SetBusy(bool busy)
    {
        if (_homeUpdateButton is not null) _homeUpdateButton.IsEnabled = !busy;
        PlayButton.IsEnabled = !busy;
        HomeBuildButton.IsEnabled = !busy && _game is not { IsRunning: true };
        SetQuickBuildOpen(false);
        HomeLogsButton.Visibility = !busy && _homeError ? Visibility.Visible : Visibility.Collapsed;
        HomeCancelButton.IsEnabled = busy;
        HomeCancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        HomeOperationStrip.Visibility = busy || _game is { IsRunning: true } || _homeError ? Visibility.Visible : Visibility.Collapsed;
        MainProgress.Visibility = busy ? MainProgress.Visibility : Visibility.Collapsed;
        HomeUnknownProgress.Visibility = busy ? HomeUnknownProgress.Visibility : Visibility.Collapsed;
        BootCancelButton.IsEnabled = busy;

        if (busy)
        {
            PlayButton.Content = "ПОДОЖДИТЕ";
            PlayButton.IconKind = PixelIconKind.Clock;
        }
        else
        {
            UpdatePlayButton();
        }
    }

    private void ResetProgress()
    {
        _homeError = false;
        HomeOperationStrip.Visibility = Visibility.Visible;
        MainProgress.Visibility = Visibility.Collapsed;
        HomeUnknownProgress.Visibility = Visibility.Visible;
        MainProgress.ShowText = false;
        MainProgress.Value = 0;
        BootProgress.Value = 0;
        ProgressPercentText.Text = "";
        StatusText.Text = "Подготовка...";
    }

    private void OnProgress(LauncherProgress progress)
    {
        if (!_busy && _game is { IsRunning: true }) return;
        var percent = progress.Percent ?? 0;
        MainProgress.ShowText = false;
        MainProgress.Visibility = progress.Percent.HasValue ? Visibility.Visible : Visibility.Collapsed;
        HomeUnknownProgress.Visibility = progress.Percent.HasValue ? Visibility.Collapsed : Visibility.Visible;
        MainProgress.Value = percent;
        BootProgress.Value = percent;
        ProgressPercentText.Text = progress.Percent is null ? "" : $"{percent:0}%";
        StatusText.Text = progress.Message;

        if (BootOverlay.Visibility == Visibility.Visible)
        {
            BootStatusText.Text = progress.Message;
        }
    }

    private void ShowBoot(string title, string status)
    {
        BootTitle.Text = title.ToUpperInvariant();
        BootStatusText.Text = status;
        BootProgress.Value = 0;
        BootTipText.Text = BootTips[Random.Shared.Next(BootTips.Length)];
        BootBackButton.Visibility = Visibility.Collapsed;
        BootCancelButton.Visibility = Visibility.Visible;
        HomeOperationStrip.Visibility = Visibility.Visible;
        HomeCancelButton.Visibility = Visibility.Visible;
        HomeUnknownProgress.Visibility = Visibility.Visible;
        StatusText.Text = status;
        UpdatePanoramaPause();
    }

    private void HideBoot()
    {
        BootOverlay.Visibility = Visibility.Collapsed;
        UpdatePanoramaPause();
    }

    private void BootCancel_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        BootStatusText.Text = "Отмена...";
        StatusText.Text = "Отмена…";
        HomeCancelButton.IsEnabled = false;
    }

    private void BootBack_Click(object sender, RoutedEventArgs e) => HideBoot();

    // ================= МОДЫ =================

    private async Task UpdateModsCountHintAsync()
    {
        var instance = _instance;
        var version = _modsScanVersion;

        try
        {
            var count = await Task.Run(() => CountModFiles(instance));
            if (version != _modsScanVersion || !IsCurrentInstance(instance))
            {
                return;
            }

            _modsCountHint = count;
            if (!_modsLoading)
            {
                NavModsCount.Text = count.ToString(CultureInfo.InvariantCulture);
            }

            UpdateDashboardText();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[МОДЫ] подсчёт файлов: " + ex.Message);
        }
    }

    private static int CountModFiles(InstanceProfile instance)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(instance.GameDir))
            {
                return 0;
            }

            var modsDir = Path.Combine(instance.GameDir, "mods");
            return Directory.Exists(modsDir)
                ? Directory.EnumerateFiles(modsDir, "*", SearchOption.TopDirectoryOnly)
                    .Count(path => IsModFile(Path.GetFileName(path)))
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private async Task EnsureModsLoadedAsync()
    {
        if (_modsLoading)
        {
            return;
        }

        if (!_modsDirty && _modAnalysis is not null)
        {
            ApplyModFilter();
            UpdateModAnalysisBanner();
            UpdateModsHeader();
            return;
        }

        await RefreshModsAsync();
    }

    private async Task RefreshModsAsync(bool force = false)
    {
        InstanceProfile instance;
        try { instance = GetModsTarget(); }
        catch (Exception ex) { ShowError("Моды", ex.Message); return; }
        var version = ++_modsScanVersion;
        _modsLoading = true;
        ModAnalysisBanner.Visibility = Visibility.Collapsed;
        ModsList.IsEnabled = false;
        ModsList.Visibility = Visibility.Hidden;
        NoModsText.Text = "Читаем моды…"; NoModsText.Visibility = Visibility.Visible;

        if (_allMods.Count == 0)
        {
            NoModsText.Text = "Читаем моды…";
            NoModsText.Visibility = Visibility.Visible;
        }

        if (_modAnalysis is null)
        {
            ModAnalysisTitle.Text = "АНАЛИЗ: ЧТЕНИЕ...";
            ModAnalysisText.Text = "Сканируем папку mods и читаем метаданные jar.";
            ModAnalysisAccent.Background = Brush("#F5C542");
            ModAnalysisBanner.BorderBrush = Brush("#F5C542");
            ModAnalysisTitle.Foreground = Brush("#F5C542");
        }

        ModsSubtitle.Text = "Чтение папки mods...";

        try
        {
            var snapshot = await Task.Run(() => CaptureModsSnapshot(instance));
            if (version != _modsScanVersion || !IsCurrentModsTarget(instance))
            {
                return;
            }

            if (!force &&
                _modAnalysis is not null &&
                string.Equals(snapshot.Fingerprint, _modsFingerprint, StringComparison.Ordinal))
            {
                _modsDirty = false;
                _modsLoading = false;
                ApplyModFilter();
                UpdateModAnalysisBanner();
                UpdateModsHeader();
                return;
            }

            var report = await Task.Run(() => _mods.Analyze(instance));
            var catalogRecords = await Task.Run(() => ReadVerifiedCatalogRecords(instance, snapshot.Entries));
            if (version != _modsScanVersion || !IsCurrentModsTarget(instance))
            {
                return;
            }

            ApplyModScanResult(snapshot, report, catalogRecords);
        }
        catch (Exception ex)
        {
            if (version != _modsScanVersion)
            {
                return;
            }

            _modAnalysis = null;
            _allMods.Clear();
            _modRows.Clear();
            _modsDirty = true;
            _modsLoading = false;
            NoModsText.Text = "Не удалось прочитать папку mods";
            NoModsText.Visibility = Visibility.Visible;
            UpdateModAnalysisBanner();
            UpdateModsHeader();
            ModsSubtitle.Text = "Ошибка чтения модов";
            ShowToast("Моды", ex.Message, "#E5533D");
        }
        finally
        {
            if (version == _modsScanVersion)
            {
                _modsLoading = false;
                ModsList.IsEnabled = true; ModsList.Visibility = Visibility.Visible;
            }
        }
    }

    private void ApplyModScanResult(ModsSnapshot snapshot, ModAnalysisReport report,
        IReadOnlyDictionary<string, CatalogModRecord> catalogRecords)
    {
        _modAnalysis = report;
        _modsFingerprint = snapshot.Fingerprint;
        _modsDirty = false;
        _modsLoading = false;
        _modsCountHint = null;
        _allMods.Clear();

        var metadata = report.Mods
            .GroupBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var problems = BuildModProblems(report, metadata, snapshot.Entries);
        var updates = _modUpdates
            .GroupBy(update => update.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in snapshot.Entries)
        {
            metadata.TryGetValue(entry.FileName, out var meta);
            catalogRecords.TryGetValue(entry.FileName, out var catalogRecord);
            var row = new ModRow(entry, meta, catalogRecord);

            if (problems.TryGetValue(entry.FileName, out var problem))
            {
                row.SetProblem(problem);
            }

            if (updates.TryGetValue(entry.FileName, out var update))
            {
                row.SetUpdate(update.LatestVersion);
            }

            _allMods.Add(row);
        }

        ApplyModFilter();
        FadeContentIn(ModsList);
        UpdateModAnalysisBanner();
        UpdateModsHeader();
        UpdateDashboardText();
        if (ModsView.Visibility == Visibility.Visible && CatalogTab.IsChecked == true)
            _ = LoadCatalogAsync(false);
    }

    private void UpdateModsHeader()
    {
        if (_modsLoading && _allMods.Count == 0)
        {
            NavModsCount.Text = "…";
            return;
        }

        NavModsCount.Text = _allMods.Count.ToString(CultureInfo.InvariantCulture);

        var enabled = _allMods.Count(row => row.Enabled);
        var updateCount = _allMods.Count(row => row.HasUpdate);
        var updatePart = updateCount > 0
            ? $" · {updateCount} {Plural(updateCount, "обновление", "обновления", "обновлений")}"
            : "";

        ModsSubtitle.Text =
            $"{(_modsSide == "server" ? _hostProfile.Name : _instance.Name)} · Minecraft {GetCatalogMinecraft(_instance)} · {GetCatalogLoader(_instance)} · {_allMods.Count} {Plural(_allMods.Count, "мод", "мода", "модов")} · {enabled} включено" +
            updatePart;
    }

    private bool IsCurrentInstance(InstanceProfile instance) =>
        string.Equals(instance.Id, _instance.Id, StringComparison.OrdinalIgnoreCase);

    private static ModsSnapshot CaptureModsSnapshot(InstanceProfile instance)
    {
        var modsDir = string.IsNullOrWhiteSpace(instance.GameDir)
            ? ""
            : Path.Combine(instance.GameDir, "mods");
        var entries = new List<ModEntry>();

        if (!string.IsNullOrWhiteSpace(modsDir) && Directory.Exists(modsDir))
        {
            var files = new List<FileInfo>();
            foreach (var path in Directory.EnumerateFiles(modsDir, "*", SearchOption.TopDirectoryOnly))
            {
                var fileName = Path.GetFileName(path);
                if (!IsModFile(fileName))
                {
                    continue;
                }

                var info = new FileInfo(path);
                if (info.Exists)
                {
                    files.Add(info);
                }
            }

            files.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Name, right.Name));

            var fingerprint = new System.Text.StringBuilder();
            fingerprint.Append(instance.Id).Append('\n');

            foreach (var info in files)
            {
                fingerprint
                    .Append(info.Name)
                    .Append('|')
                    .Append(info.Length)
                    .Append('|')
                    .Append(info.LastWriteTimeUtc.Ticks)
                    .Append('\n');

                entries.Add(new ModEntry
                {
                    FileName = info.Name,
                    FullPath = info.FullName,
                    Size = info.Length,
                    Enabled = !info.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                    ModifiedAt = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                });
            }

            return new ModsSnapshot(fingerprint.ToString(), entries);
        }

        return new ModsSnapshot(instance.Id + "\n", entries);
    }

    private static bool IsModFile(string fileName) =>
        fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string> BuildModProblems(
        ModAnalysisReport report,
        IReadOnlyDictionary<string, ModMetadata> metadata,
        IReadOnlyList<ModEntry> entries)
    {
        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            if (!metadata.ContainsKey(entry.FileName))
            {
                problems[entry.FileName] = "Метаданные не прочитаны";
            }
        }

        foreach (var duplicate in report.DuplicateModIds)
        {
            foreach (var file in duplicate.FileNames)
            {
                AppendModProblem(problems, file, $"Дубликат modId {duplicate.ModId}");
            }
        }

        foreach (var file in report.DuplicateFileNames)
        {
            AppendModProblem(problems, file, "Дубликат имени файла");
        }

        foreach (var issue in report.MissingDependencies)
        {
            AppendModProblem(
                problems,
                issue.RequiredByFileName,
                $"Нет зависимости {issue.ModId}");
        }

        foreach (var issue in report.DisabledDependencies)
        {
            AppendModProblem(
                problems,
                issue.RequiredByFileName,
                $"Зависимость {issue.ModId} отключена");
        }

        return problems;
    }

    private static void AppendModProblem(IDictionary<string, string> problems, string fileName, string text)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        if (problems.TryGetValue(fileName, out var existing))
        {
            if (!existing.Contains(text, StringComparison.OrdinalIgnoreCase))
            {
                problems[fileName] = existing + "; " + text;
            }

            return;
        }

        problems[fileName] = text;
    }

    private void ApplyModFilter()
    {
        if (ModSearch is null || NoModsText is null)
        {
            return;
        }

        if (_modsLoading && _allMods.Count == 0)
        {
            return;
        }

        var query = (ModSearch.Text ?? "").Trim();
        var filter = GetModFilter();
        IEnumerable<ModRow> filtered = _allMods;

        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered.Where(row =>
                row.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                row.Subtitle.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        filtered = filter switch
        {
            "enabled" => filtered.Where(row => row.Enabled),
            "disabled" => filtered.Where(row => !row.Enabled),
            "updates" => filtered.Where(row => row.HasUpdate),
            "problems" => filtered.Where(row => row.IsProblem),
            _ => filtered
        };

        var rows = filtered.OrderByDescending(row => row.CatalogRecord?.InstalledAt ?? DateTimeOffset.MinValue).ToList();

        _modRows.Clear();
        foreach (var row in rows)
        {
            _modRows.Add(row);
        }

        RefreshInstalledContent();
        NoModsText.Visibility = _installedContent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoModsText.Text = _allMods.Count == 0
            ? "Модов нет. Нажми «Импорт», чтобы добавить jar."
            : filter == "updates" && _modUpdates.Count == 0
                ? "Обновления ещё не проверены. Нажми «ОБНОВЛЕНИЯ»."
                : "Ничего не найдено по запросу или фильтру.";
    }

    private string GetModFilter()
    {
        return (InstalledFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
    }

    private void ModFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _modFilterTimer?.Stop();
        ApplyModFilter();
    }

    private void ModSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ModSearchPlaceholder is not null)
        {
            ModSearchPlaceholder.Visibility = string.IsNullOrEmpty(ModSearch.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (!IsLoaded)
        {
            return;
        }

        if (_modFilterTimer is null)
        {
            _modFilterTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _modFilterTimer.Tick += (_, _) =>
            {
                _modFilterTimer.Stop();
                ApplyModFilter();
            };
        }

        _modFilterTimer.Stop();
        _modFilterTimer.Start();
    }

    private void UpdateModAnalysisBanner()
    {
        if (ModAnalysisBanner is null)
        {
            return;
        }

        var report = _modAnalysis;
        var duplicates = report?.DuplicateModIds.Count ?? 0;
        var duplicateFiles = report?.DuplicateFileNames.Count ?? 0;
        var missing = report?.MissingDependencies.Count ?? 0;
        var disabled = report?.DisabledDependencies.Count ?? 0;
        var warnings = report?.Warnings.Count ?? 0;
        var total = duplicates + duplicateFiles + missing + disabled + warnings;
        ModAnalysisBanner.Visibility = !_modsLoading && !_modWarningsDismissed && report is not null && total > 0
            ? Visibility.Visible : Visibility.Collapsed;

        if (report is null)
        {
            ModAnalysisTitle.Text = "АНАЛИЗ НЕДОСТУПЕН";
            ModAnalysisText.Text = "Не удалось прочитать папку mods.";
            ModAnalysisAccent.Background = Brush("#E5533D");
            ModAnalysisBanner.BorderBrush = Brush("#E5533D");
            ModAnalysisTitle.Foreground = Brush("#E5533D");
            return;
        }

        if (total == 0)
        {
            ModAnalysisTitle.Text = "АНАЛИЗ: ПРОБЛЕМ НЕ НАЙДЕНО";
            ModAnalysisText.Text = "Дубликатов, отсутствующих зависимостей и предупреждений нет.";
            ModAnalysisAccent.Background = Brush("#5FBE4A");
            ModAnalysisBanner.BorderBrush = Brush("#5FBE4A");
            ModAnalysisTitle.Foreground = Brush("#7CD964");
            return;
        }

        ModAnalysisTitle.Text = $"АНАЛИЗ: {total} {Plural(total, "проблема", "проблемы", "проблем")}";
        ModAnalysisText.Text =
            $"Дубликаты: {duplicates + duplicateFiles} · Нет зависимостей: {missing} · " +
            $"Отключены: {disabled} · Предупреждения: {warnings}";
        var color = missing + duplicates + duplicateFiles > 0 ? "#E5533D" : "#F5C542";
        ModAnalysisAccent.Background = Brush(color);
        ModAnalysisBanner.BorderBrush = Brush(color);
        ModAnalysisTitle.Foreground = Brush(color);
    }

    private void ModAnalysisDetails_Click(object sender, RoutedEventArgs e)
    {
        if (_modAnalysis is null)
        {
            ShowToast("Анализ модов", "Дождись окончания чтения папки mods.", "#F5C542");
            return;
        }

        _modIssueRows.Clear();

        if (_modAnalysis is not null)
        {
            foreach (var duplicate in _modAnalysis.DuplicateModIds)
            {
                _modIssueRows.Add(new ModIssueRow
                {
                    Category = "ДУБЛИКАТ MODID",
                    Text = $"«{duplicate.ModId}»: {string.Join(", ", duplicate.FileNames)}",
                    Color = "#E5533D"
                });
            }

            foreach (var file in _modAnalysis.DuplicateFileNames)
            {
                _modIssueRows.Add(new ModIssueRow
                {
                    Category = "ДУБЛИКАТ ФАЙЛА",
                    Text = file,
                    Color = "#E5533D"
                });
            }

            foreach (var issue in _modAnalysis.MissingDependencies)
            {
                var suffix = string.IsNullOrWhiteSpace(issue.VersionRange)
                    ? ""
                    : $" {issue.VersionRange}";
                var found = string.IsNullOrWhiteSpace(issue.FoundFileName)
                    ? "не найден"
                    : $"найден {issue.FoundFileName} ({issue.FoundVersion})";
                _modIssueRows.Add(new ModIssueRow
                {
                    Category = "НЕТ ЗАВИСИМОСТИ",
                    Text = $"{issue.ModId}{suffix} — требуется для {issue.RequiredByFileName}; {found}.",
                    Color = "#E5533D"
                });
            }

            foreach (var issue in _modAnalysis.DisabledDependencies)
            {
                _modIssueRows.Add(new ModIssueRow
                {
                    Category = "ЗАВИСИМОСТЬ ОТКЛЮЧЕНА",
                    Text = $"{issue.ModId} — требуется для {issue.RequiredByFileName}; " +
                           $"файл {issue.FoundFileName} отключён.",
                    Color = "#F5C542"
                });
            }

            foreach (var warning in _modAnalysis.Warnings)
            {
                _modIssueRows.Add(new ModIssueRow
                {
                    Category = "ПРЕДУПРЕЖДЕНИЕ",
                    Text = warning,
                    Color = "#F5C542"
                });
            }
        }

        if (_modIssueRows.Count == 0)
        {
            _modIssueRows.Add(new ModIssueRow
            {
                Category = "ПРОБЛЕМ НЕТ",
                Text = "Все моды прочитаны, дубликатов и отсутствующих зависимостей не найдено.",
                Color = "#7CD964"
            });
        }

        ModDetailsInstanceText.Text = $"{GetModsTarget().Name} · Minecraft {GetModsTarget().BaseMinecraftVersion}";
        ModDetailsCountText.Text = $"Найдено: {_modIssueRows.Count}";
        PageScroll.Visibility = Visibility.Hidden;
        ModDetailsOverlay.Visibility = Visibility.Visible;
        ModDetailsCloseButton.Focus();
        UpdatePanoramaPause();
    }

    private void ModDetailsClose_Click(object sender, RoutedEventArgs e)
    {
        ModDetailsOverlay.Visibility = Visibility.Collapsed;
        PageScroll.Visibility = Visibility.Visible;
        if (ModAnalysisDetailsButton.IsVisible) ModAnalysisDetailsButton.Focus();
        UpdatePanoramaPause();
    }

    private async void CheckModUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_allMods.Count == 0)
        {
            ShowToast("Обновления", "Список модов пуст.", "#F5C542");
            return;
        }

        var pulse = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        double progress = 0;
        pulse.Tick += (_, _) =>
        {
            progress = Math.Min(92, progress + 5);
            BootProgress.Value = progress;
        };

        var success = false;
        var canceled = false;
        IReadOnlyList<ModUpdateInfo> found = Array.Empty<ModUpdateInfo>();

        await RunBusyAsync(
            "ПРОВЕРКА ОБНОВЛЕНИЙ",
            "Считаем SHA-512 и запрашиваем Modrinth...",
            async ct =>
            {
                pulse.Start();
                try
                {
                    found = await _mods.CheckUpdatesAsync(GetModsTarget(), ct);
                    success = true;
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    throw;
                }
                finally
                {
                    pulse.Stop();
                    BootProgress.Value = 100;
                }
            });

        pulse.Stop();

        if (canceled || !success)
        {
            return;
        }

        _modUpdates.Clear();
        _modUpdates.AddRange(found);
        ApplyModUpdateFlags();

        if (_modUpdates.Count == 0)
        {
            ShowToast("Обновлений нет", "Все моды соответствуют версиям Modrinth.", "#7CD964");
            return;
        }

        OpenModUpdatesOverlay();
    }

    private void OpenModUpdatesOverlay()
    {
        _modUpdateRows.Clear();
        foreach (var update in _modUpdates)
        {
            _modUpdateRows.Add(new ModUpdateRow(update));
        }

        ModUpdatesSummaryText.Text =
            $"{_modUpdates.Count} {Plural(_modUpdates.Count, "обновление", "обновления", "обновлений")} готово к установке";
        ModUpdateAllButton.IsEnabled = _modUpdates.Count > 0;
        ModUpdatesOverlay.Visibility = Visibility.Visible;
        UpdatePanoramaPause();
    }

    private void ModUpdatesClose_Click(object sender, RoutedEventArgs e)
    {
        ModUpdatesOverlay.Visibility = Visibility.Collapsed;
        UpdatePanoramaPause();
    }

    private bool CanChangeMods()
    {
        if (_modsSide == "server")
        {
            if (_hostProcess is not { IsRunning: true } && !_hostBusy) return true;
            ShowError("Моды", "Остановите сервер и дождитесь завершения его операций.");
            return false;
        }
        if (_game is not { IsRunning: true }) return true;
        ShowError("Моды", "Закройте игру перед изменением файлов сборки.");
        return false;
    }
    private async void UpdateAllMods_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeMods()) return;
        if (_modUpdates.Count == 0)
        {
            return;
        }

        var updates = _modUpdates.ToList();
        var target = GetModsTarget();
        var serverTarget = _modsSide == "server";
        var modsDir = Path.Combine(target.GameDir, "mods");
        var errors = new List<string>();
        var succeeded = 0;

        if (serverTarget) SetHostBusy(true);
        try { await RunBusyAsync(
            "ОБНОВЛЕНИЕ МОДОВ",
            $"Подготовка: {updates.Count}",
            async ct =>
            {
                for (var index = 0; index < updates.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var update = updates[index];

                    OnProgress(new LauncherProgress(
                        ProgressStage.Update,
                        $"Обновление {index + 1} из {updates.Count}: {update.FileName}",
                        index,
                        updates.Count));

                    try
                    {
                        var row = _allMods.FirstOrDefault(item => string.Equals(item.FileName, update.FileName, StringComparison.OrdinalIgnoreCase));
                        if (row?.CatalogRecord is not null)
                        {
                            var installer = new ModCatalogInstaller(_catalogClient);
                            var plan = await installer.PrepareAsync(update.ProjectId, update.VersionId, GetCatalogMinecraft(target), GetCatalogLoader(target), ct, serverTarget ? "server" : "client");
                            await installer.InstallAsync(target, plan, new Progress<string>(message => BootStatusText.Text = message), ct);
                        }
                        else if (!serverTarget)
                        {
                            await _mods.DownloadUpdateAsync(update, modsDir, ct);
                        }
                        else throw new InvalidOperationException("Для безопасного обновления серверного мода нужно подтверждение его установки через каталог.");
                        succeeded++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"{update.FileName}: {ex.Message}");
                    }
                }
            }); }
        finally { if (serverTarget) SetHostBusy(false); }

        ModUpdatesOverlay.Visibility = Visibility.Collapsed;
        UpdatePanoramaPause();
        _modUpdates.Clear();
        await RefreshModsAsync(force: true);

        if (errors.Count > 0)
        {
            ShowError(
                "Обновление модов",
                $"Обновлено: {succeeded}. Ошибок: {errors.Count}.\n\n" +
                string.Join("\n", errors.Take(5)));
        }
        else if (succeeded > 0)
        {
            ShowToast(
                "Моды обновлены",
                $"Установлено обновлений: {succeeded}",
                "#7CD964");
        }
    }

    private void ModToggle_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeMods()) return;
        if (sender is not FrameworkElement { Tag: ModRow row })
        {
            return;
        }

        var oldFileName = row.FileName;

        try
        {
            if (!_mods.SetEnabled(row.Entry, !row.Enabled))
            {
                return;
            }

            row.Enabled = row.Entry.Enabled;

            foreach (var update in _modUpdates)
            {
                if (string.Equals(update.FileName, oldFileName, StringComparison.OrdinalIgnoreCase))
                {
                    update.FileName = row.Entry.FileName;
                }
            }

            if (_modAnalysis is not null)
            {
                var metadata = _modAnalysis.Mods.FirstOrDefault(mod =>
                    string.Equals(mod.FileName, oldFileName, StringComparison.OrdinalIgnoreCase));
                if (metadata is not null)
                {
                    metadata.FileName = row.Entry.FileName;
                    metadata.FullPath = row.Entry.FullPath;
                    metadata.Enabled = row.Entry.Enabled;
                }

                UpdateModFileReferences(_modAnalysis, oldFileName, row.Entry.FileName);
                ModAnalyzer.RecomputeDependencyIssues(_modAnalysis);
                RefreshModProblems();
            }

            ApplyModFilter();
            UpdateModAnalysisBanner();
            UpdateModsHeader();
            ShowToast(row.Enabled ? "Мод включён" : "Мод отключён", row.DisplayName, "#58C7F0");
        }
        catch (Exception ex)
        {
            ShowError("Моды", ex.Message);
            _modsDirty = true;
            _ = RefreshModsAsync(force: true);
        }
    }

    private void UpdateModFileReferences(
        ModAnalysisReport report,
        string oldFileName,
        string newFileName)
    {
        foreach (var duplicate in report.DuplicateModIds)
        {
            for (var index = 0; index < duplicate.FileNames.Count; index++)
            {
                if (string.Equals(duplicate.FileNames[index], oldFileName, StringComparison.OrdinalIgnoreCase))
                {
                    duplicate.FileNames[index] = newFileName;
                }
            }
        }

        for (var index = 0; index < report.DuplicateFileNames.Count; index++)
        {
            if (string.Equals(report.DuplicateFileNames[index], oldFileName, StringComparison.OrdinalIgnoreCase))
            {
                report.DuplicateFileNames[index] = newFileName;
            }
        }

        foreach (var issue in report.MissingDependencies.Concat(report.DisabledDependencies))
        {
            if (string.Equals(issue.RequiredByFileName, oldFileName, StringComparison.OrdinalIgnoreCase))
            {
                issue.RequiredByFileName = newFileName;
            }

            if (string.Equals(issue.FoundFileName, oldFileName, StringComparison.OrdinalIgnoreCase))
            {
                issue.FoundFileName = newFileName;
            }
        }
    }

    private void RefreshModProblems()
    {
        if (_modAnalysis is null)
        {
            return;
        }

        var metadata = _modAnalysis.Mods
            .GroupBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var entries = _allMods.Select(row => row.Entry).ToList();
        var problems = BuildModProblems(_modAnalysis, metadata, entries);

        foreach (var row in _allMods)
        {
            row.SetProblem(problems.TryGetValue(row.FileName, out var problem) ? problem : null);
        }
    }

    private void ApplyModUpdateFlags()
    {
        var updates = _modUpdates
            .GroupBy(update => update.FileName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var row in _allMods)
        {
            row.SetUpdate(updates.TryGetValue(row.FileName, out var update)
                ? update.LatestVersion
                : null);
        }

        ApplyModFilter();
        UpdateModsHeader();
    }

    private async void DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeMods()) return;
        if (sender is not FrameworkElement { Tag: ModRow row })
        {
            return;
        }

        var targetMetadata = _modAnalysis?.Mods.FirstOrDefault(mod =>
            string.Equals(mod.FileName, row.FileName, StringComparison.OrdinalIgnoreCase));
        var dependents = targetMetadata is null || string.IsNullOrWhiteSpace(targetMetadata.ModId)
            ? new List<string>()
            : _modAnalysis!.Mods.Where(mod => mod.Enabled && mod.Dependencies.Any(dependency =>
                dependency.Mandatory && string.Equals(dependency.ModId, targetMetadata.ModId, StringComparison.OrdinalIgnoreCase)))
                .Select(mod => string.IsNullOrWhiteSpace(mod.Name) ? mod.FileName : mod.Name).Distinct().ToList();
        var dependencyWarning = dependents.Count == 0 ? "" :
            "\n\nОт этого мода зависят:\n" + string.Join("\n", dependents.Select(name => "• " + name));
        var confirmed = ShowDarkMessage("Удаление мода",
            $"Удалить мод?\n\n{row.FileName}\n\nФайл будет перемещён в папку бэкапов.{dependencyWarning}", confirm: true);

        if (!confirmed)
        {
            return;
        }

        if (!CanChangeMods()) return;

        try
        {
            _mods.Delete(row.Entry);
            _modUpdates.RemoveAll(update =>
                string.Equals(update.FileName, row.FileName, StringComparison.OrdinalIgnoreCase));
            await RefreshModsAsync(force: true);
            ShowToast("Мод удалён", row.DisplayName, "#F5C542");
        }
        catch (Exception ex)
        {
            ShowError("Удаление мода", ex.Message);
        }
    }

    private async void ImportMods_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeMods()) return;
        var dialog = new OpenFileDialog
        {
            Title = "Импорт модов",
            Filter = "Моды Minecraft (*.jar)|*.jar|Все файлы (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true || dialog.FileNames.Length == 0)
        {
            return;
        }

        if (!CanChangeMods()) return;

        try
        {
            _mods.Import(GetModsTarget(), dialog.FileNames);
            _modUpdates.Clear();
            await RefreshModsAsync(force: true);
            ShowToast("Моды добавлены", $"Импортировано: {dialog.FileNames.Length}", "#7CD964");
        }
        catch (Exception ex)
        {
            ShowError("Импорт модов", ex.Message);
        }
    }

    private void OpenModsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _mods.OpenModsFolder(GetModsTarget());
        }
        catch (Exception ex)
        {
            ShowError("Папка модов", ex.Message);
        }
    }

    private async void RefreshMods_Click(object sender, RoutedEventArgs e)
    {
        await RefreshModsAsync(force: true);
        ShowToast("Список обновлён", $"Найдено модов: {_allMods.Count}", "#58C7F0");
    }

    // ================= МИРЫ =================

    private async Task RefreshWorldsAsync(bool force = false)
    {
        if (!force && !_worldsDirty)
        {
            return;
        }

        var instance = _instance;
        var version = ++_worldsScanVersion;
        NoWorldsPageText.Visibility = Visibility.Collapsed;
        WorldsSubtitle.Text = "Чтение миров...";

        try
        {
            var worlds = await Task.Run(() => _worlds.List(instance, includeSize: false));
            if (version != _worldsScanVersion || !IsCurrentInstance(instance))
            {
                return;
            }

            _worldRows.Clear();
            foreach (var world in worlds)
            {
                _worldRows.Add(new WorldRow(world));
            }

            _worldsDirty = false;

            var valid = _worldRows.Count(row => row.IsValid);
            var invalid = _worldRows.Count - valid;
            var invalidPart = invalid > 0
                ? $" · {invalid} {Plural(invalid, "повреждён", "повреждены", "повреждено")}"
                : "";

            NoWorldsPageText.Text = "Миров пока нет. Запусти игру и создай первый.";
            NoWorldsPageText.Visibility = _worldRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            WorldsSubtitle.Text =
                $"{_worldRows.Count} {Plural(_worldRows.Count, "мир", "мира", "миров")} · " +
                $"{valid} {Plural(valid, "рабочий", "рабочих", "рабочих")}{invalidPart} · " +
                $"бэкапов: {CountWorldBackups()}";
        }
        catch (Exception ex)
        {
            if (version != _worldsScanVersion)
            {
                return;
            }

            _worldsDirty = true;
            _worldRows.Clear();
            NoWorldsPageText.Text = "Не удалось прочитать миры";
            NoWorldsPageText.Visibility = Visibility.Visible;
            WorldsSubtitle.Text = "Ошибка чтения миров";
            ShowToast("Миры", ex.Message, "#E5533D");
        }
    }

    private int CountWorldBackups()
    {
        try
        {
            var root = _worlds.BackupsRoot;
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly).Count()
                : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void OpenWorld_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldRow row })
        {
            return;
        }

        try
        {
            _worlds.OpenFolder(_instance, row.FolderName);
        }
        catch (Exception ex)
        {
            ShowError("Папка мира", ex.Message);
        }
    }

    private async void BackupWorld_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldRow row })
        {
            return;
        }

        string? created = null;
        var canceled = false;

        await RunBusyAsync(
            "БЭКАП МИРА",
            $"Подготовка: {row.Name}",
            async ct =>
            {
                try
                {
                    created = await _worlds.CreateBackupAsync(
                        _instance,
                        row.FolderName,
                        progress: new Progress<LauncherProgress>(OnProgress),
                        ct: ct);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                    throw;
                }
            });

        if (created is not null && !canceled)
        {
            await RefreshWorldsAsync(force: true);
            ShowToast("Бэкап создан", Path.GetFileName(created), "#7CD964");
        }
    }

    private void RestoreWorld_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldRow row })
        {
            return;
        }

        _restoreWorld = row;
        WorldRestoreTargetText.Text = $"ВОССТАНОВЛЕНИЕ: {row.Name}";
        RefreshRestoreBackupList();
        WorldRestoreOverlay.Visibility = Visibility.Visible;
        UpdatePanoramaPause();
    }

    private void RefreshRestoreBackupList()
    {
        _restoreBackupRows.Clear();

        try
        {
            var root = _worlds.BackupsRoot;
            if (Directory.Exists(root))
            {
                var files = Directory
                    .EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly)
                    .Select(path => new FileInfo(path))
                    .Where(file => file.Exists)
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ToList();

                foreach (var file in files)
                {
                    _restoreBackupRows.Add(new WorldBackupRow
                    {
                        Path = file.FullName,
                        DisplayName = Path.GetFileNameWithoutExtension(file.Name),
                        DateText = file.LastWriteTime.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
                        SizeText = ModRow.FormatSize(file.Length)
                    });
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowToast("Бэкапы миров", ex.Message, "#E5533D");
        }

        NoRestoreBackupsText.Visibility =
            _restoreBackupRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        WorldRestoreCountText.Text = $"{_restoreBackupRows.Count}";
    }

    private void WorldRestoreClose_Click(object sender, RoutedEventArgs e)
    {
        WorldRestoreOverlay.Visibility = Visibility.Collapsed;
        _restoreWorld = null;
        UpdatePanoramaPause();
    }

    private void WorldRestorePick_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: WorldBackupRow row })
        {
            _ = RestoreWorldFromZipAsync(row.Path, row.FileName);
        }
    }

    private void WorldRestoreBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выбрать бэкап мира",
            InitialDirectory = Directory.Exists(_worlds.BackupsRoot) ? _worlds.BackupsRoot : "",
            Filter = "Архив мира (*.zip)|*.zip|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            _ = RestoreWorldFromZipAsync(dialog.FileName, Path.GetFileName(dialog.FileName));
        }
    }

    private async Task RestoreWorldFromZipAsync(string zipPath, string label)
    {
        if (_restoreWorld is not { } target)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Восстановить мир «{target.Name}» из архива?\n\n{label}\n\n" +
            "Если папка мира уже существует, лаунчер предложит перезапись.",
            "Восстановление мира",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        WorldRestoreOverlay.Visibility = Visibility.Collapsed;
        UpdatePanoramaPause();
        var restored = false;
        var overwritten = false;

        await RunBusyAsync(
            "ВОССТАНОВЛЕНИЕ МИРА",
            $"Распаковка: {label}",
            async ct =>
            {
                try
                {
                    await _worlds.RestoreBackupAsync(zipPath, _instance, overwrite: false, ct);
                    restored = true;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (LauncherException ex) when (
                    ex.Message.Contains("уже существует", StringComparison.OrdinalIgnoreCase))
                {
                    var overwrite = MessageBox.Show(
                        this,
                        $"Мир «{target.Name}» уже существует.\n\nПерезаписать его данными из архива?",
                        "Перезапись мира",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (overwrite != MessageBoxResult.Yes)
                    {
                        return;
                    }

                    await _worlds.RestoreBackupAsync(zipPath, _instance, overwrite: true, ct);
                    restored = true;
                    overwritten = true;
                }
            });

        if (restored)
        {
            await RefreshWorldsAsync(force: true);
            ShowToast(
                overwritten ? "Мир перезаписан" : "Мир восстановлен",
                target.Name,
                "#7CD964");
        }
        else
        {
            WorldRestoreOverlay.Visibility = Visibility.Visible;
            UpdatePanoramaPause();
        }
    }

    private async void ExportWorld_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldRow row })
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт мира",
            FileName = $"{SanitizeFileName(row.Name)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            Filter = "Архив мира (*.zip)|*.zip|Все файлы (*.*)|*.*",
            OverwritePrompt = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var target = dialog.FileName;
        string? exported = null;

        await RunBusyAsync(
            "ЭКСПОРТ МИРА",
            $"Подготовка: {row.Name}",
            async ct =>
            {
                var tempDir = Path.Combine(
                    Path.GetTempPath(),
                    "samara-world-export-" + Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(tempDir);

                try
                {
                    var zip = await _worlds.ExportAsync(
                        _instance,
                        row.FolderName,
                        tempDir,
                        new Progress<LauncherProgress>(OnProgress),
                        ct);

                    await Task.Run(() => File.Move(zip, target, overwrite: true), ct);
                    exported = target;
                }
                finally
                {
                    try
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            });

        if (exported is not null)
        {
            ShowToast("Мир экспортирован", exported, "#7CD964");
        }
    }

    private async void ImportWorld_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Импорт мира",
            Filter = "Архив мира (*.zip)|*.zip|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Импортировать мир из архива?\n\n{Path.GetFileName(dialog.FileName)}\n\n" +
            "Если мир уже существует, лаунчер предложит перезапись.",
            "Импорт мира",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var zipPath = dialog.FileName;
        string? imported = null;
        var overwritten = false;

        await RunBusyAsync(
            "ИМПОРТ МИРА",
            $"Распаковка: {Path.GetFileName(zipPath)}",
            async ct =>
            {
                try
                {
                    imported = await _worlds.ImportAsync(zipPath, _instance, overwrite: false, ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (LauncherException ex) when (
                    ex.Message.Contains("уже существует", StringComparison.OrdinalIgnoreCase))
                {
                    var overwrite = MessageBox.Show(
                        this,
                        $"Мир из архива уже существует.\n\nПерезаписать его?",
                        "Перезапись мира",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (overwrite != MessageBoxResult.Yes)
                    {
                        return;
                    }

                    imported = await _worlds.ImportAsync(zipPath, _instance, overwrite: true, ct);
                    overwritten = true;
                }
            });

        if (imported is not null)
        {
            await RefreshWorldsAsync(force: true);
            ShowToast(
                overwritten ? "Мир перезаписан" : "Мир импортирован",
                Path.GetFileName(imported),
                "#7CD964");
        }
    }

    private async void DuplicateWorld_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldRow row })
        {
            return;
        }

        string? copy = null;

        await RunBusyAsync(
            "ДУБЛИРОВАНИЕ МИРА",
            $"Копирование: {row.Name}",
            async ct =>
            {
                copy = await _worlds.DuplicateAsync(_instance, row.FolderName, ct: ct);
            });

        if (copy is not null)
        {
            await RefreshWorldsAsync(force: true);
            ShowToast("Мир дублирован", Path.GetFileName(copy), "#7CD964");
        }
    }

    private async void DeleteWorld_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: WorldRow row })
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Удалить мир «{row.Name}»?\n\nПапка мира будет перемещена в backups\\removed-worlds.",
            "Удаление мира",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var deleted = await Task.Run(() =>
                _worlds.Delete(_instance, row.FolderName, moveToBackups: true));

            await RefreshWorldsAsync(force: true);

            if (deleted)
            {
                ShowToast("Мир удалён", $"{row.Name} → backups\\removed-worlds", "#F5C542");
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or LauncherException)
        {
            ShowError("Удаление мира", ex.Message);
        }
    }

    private void OpenSavesFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _worlds.OpenFolder(_instance);
        }
        catch (Exception ex)
        {
            ShowError("Папка миров", ex.Message);
        }
    }

    private async void RefreshWorlds_Click(object sender, RoutedEventArgs e)
    {
        await RefreshWorldsAsync(force: true);
        ShowToast("Миры обновлены", $"Найдено миров: {_worldRows.Count}", "#58C7F0");
    }

    // ================= ЛОГИ И КРАШИ =================

    private async Task RefreshLogsAsync()
    {
        var instance = _instance;
        var version = ++_logsScanVersion;
        var cts = new CancellationTokenSource();
        var previous = _logsCts;
        _logsCts = cts;
        previous?.Cancel();
        var token = cts.Token;

        if (string.IsNullOrWhiteSpace(LogTextBox.Text) || LogTextBox.Text.StartsWith("Чтение ", StringComparison.Ordinal))
        {
            LogTextBox.Text = "Чтение latest.log...";
        }

        try
        {
            var snapshot = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();

                var lines = _logService.TailLatestLog(
                    instance.GameDir,
                    fromBeginning: false,
                    maxLines: 2000);

                token.ThrowIfCancellationRequested();

                var latest = Path.Combine(instance.GameDir, "logs", "latest.log");
                var exists = File.Exists(latest);
                CrashSummary? crash = null;

                try
                {
                    crash = _crashHelper.GetSummary(instance);
                }
                catch (Exception)
                {
                    // Сводка по крашу не должна ломать чтение latest.log.
                }

                return new LogSnapshot(lines, latest, exists, crash);
            }, token);

            token.ThrowIfCancellationRequested();
            if (version != _logsScanVersion || !IsCurrentInstance(instance))
            {
                return;
            }

            LogTextBox.Text = string.Join(Environment.NewLine, snapshot.Lines.Select(line => line.Text));
            LogPathText.Text = snapshot.Exists ? snapshot.Path : "latest.log не найден";
            ApplyCrashSummary(snapshot.Crash);

            if (snapshot.Lines.Count > 0)
            {
                await Dispatcher.Yield(DispatcherPriority.Background);
                if (version == _logsScanVersion)
                {
                    LogTextBox.ScrollToEnd();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version != _logsScanVersion)
            {
                return;
            }

            LogTextBox.Text = "Не удалось прочитать latest.log:\n" + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_logsCts, cts))
            {
                _logsCts = null;
            }

            cts.Dispose();
        }
    }

    private void RefreshCrashSummary()
    {
        try
        {
            ApplyCrashSummary(_crashHelper.GetSummary(_instance));
        }
        catch (Exception ex)
        {
            CrashTitleText.Text = "КРАШ-РЕПОРТ";
            CrashSummaryText.Text = "Не удалось разобрать crash-report: " + ex.Message;
            CrashRawText.Text = "";
        }
    }

    private void ApplyCrashSummary(CrashSummary? crash)
    {
        if (crash is null)
        {
            CrashTitleText.Text = "КРАШ-РЕПОРТ";
            CrashSummaryText.Text = "Крашей не найдено. Это хорошая новость.";
            CrashRawText.Text = "";
            return;
        }

        CrashTitleText.Text = $"КРАШ · {Path.GetFileName(crash.CrashFile)}";

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(crash.Description))
        {
            lines.Add(crash.Description);
        }

        if (!string.IsNullOrWhiteSpace(crash.SuspectedMod))
        {
            lines.Add("Подозрительный мод: " + crash.SuspectedMod);
        }

        if (!string.IsNullOrWhiteSpace(crash.Cause))
        {
            lines.Add("Причина: " + crash.Cause);
        }

        if (!string.IsNullOrWhiteSpace(crash.Recommendation))
        {
            lines.Add("Что делать: " + crash.Recommendation);
        }

        CrashSummaryText.Text = string.Join(Environment.NewLine, lines);
        CrashRawText.Text = crash.RawExcerpt ?? "";
    }

    private void LogTab_Click(object sender, RoutedEventArgs e) => ShowLogTab();

    private void CrashTab_Click(object sender, RoutedEventArgs e) => ShowCrashTab();

    private void LogTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        if (sender is RadioButton { Tag: string tab })
        {
            if (tab == "crash")
            {
                ShowCrashTab();
            }
            else
            {
                ShowLogTab();
            }
        }
    }

    private void ShowLogTab()
    {
        LogsPanel.Visibility = Visibility.Visible;
        CrashPanel.Visibility = Visibility.Collapsed;
        LogTabButton.IsChecked = true;
        _ = RefreshLogsAsync();
    }

    private void ShowCrashTab()
    {
        LogsPanel.Visibility = Visibility.Collapsed;
        CrashPanel.Visibility = Visibility.Visible;
        CrashTabButton.IsChecked = true;
        RefreshCrashSummary();
    }

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e)
    {
        await RefreshLogsAsync();
        ShowToast("Логи обновлены", "latest.log и crash-reports перечитаны.", "#58C7F0");
    }

    private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
    {
        var logs = Path.Combine(_instance.GameDir, "logs");
        Directory.CreateDirectory(logs);
        OpenPath(logs);
    }

    private void OpenCrashFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _crashHelper.OpenCrashFolder(_instance);
        }
        catch (Exception ex)
        {
            ShowError("Папка крашей", ex.Message);
        }
    }

    // ================= СЕРВЕРЫ =================

    private async Task RefreshServersAsync(bool notify = false, bool forcePing = false)
    {
        if (_serversBusy)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = _serversCts;
        _serversCts = cts;
        previous?.Cancel();

        SetServersBusy(true);
        _selectedServer = null;
        ServersStatusText.Text = "Чтение servers.dat...";

        try
        {
            var entries = await Task.Run(() => _serverList.Load(_instance), cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            _serverRows.Clear();
            foreach (var entry in entries)
            {
                var row = new ServerRow(entry);
                row.MarkPinging();
                _serverRows.Add(row);
            }

            NoServersText.Visibility = _serverRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ServersStatusText.Text = _serverRows.Count == 0
                ? "Серверов пока нет."
                : $"Серверов: {_serverRows.Count} · пинг...";
            UpdateServerActions();

            if (_serverRows.Count > 0)
            {
                await PingServersAsync(_serverRows.ToList(), forcePing, cts.Token);
                cts.Token.ThrowIfCancellationRequested();

                var online = _serverRows.Count(row => row.IsOnline);
                ServersStatusText.Text =
                    $"Серверов: {_serverRows.Count} · онлайн: {online} · пинг завершён";
            }

            if (notify)
            {
                ShowToast("Серверы обновлены", ServersStatusText.Text, "#58C7F0");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ServersStatusText.Text = "Ошибка: " + ex.Message;
            ShowToast("Серверы", ex.Message, "#E5533D");
        }
        finally
        {
            if (ReferenceEquals(_serversCts, cts))
            {
                _serversCts = null;
            }

            cts.Dispose();
            SetServersBusy(false);
        }
    }

    private async Task PingServersAsync(
        IReadOnlyList<ServerRow> rows,
        bool force,
        CancellationToken ct)
    {
        // Одновременно не больше четырёх ping, чтобы не забивать сеть и UI.
        using var gate = new SemaphoreSlim(4);
        var tasks = rows.Select(row => PingServerAsync(row, gate, force, ct)).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task PingServerAsync(
        ServerRow row,
        SemaphoreSlim gate,
        bool force,
        CancellationToken ct)
    {
        var cacheKey = row.Entry.Host + "\n" + row.Entry.Port;

        if (!force)
        {
            ServerPingResult? cached = null;
            lock (_serverPingCacheLock)
            {
                if (_serverPingCache.TryGetValue(cacheKey, out var entry) &&
                    DateTimeOffset.UtcNow - entry.CachedAt < ServerPingCacheDuration)
                {
                    cached = entry.Result;
                }
            }

            if (cached is not null)
            {
                await Dispatcher.InvokeAsync(() => row.ApplyPing(cached));
                return;
            }
        }

        try
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);

            try
            {
                var result = await _serverPinger
                    .PingAsync(row.Entry.Host, row.Entry.Port, TimeSpan.FromSeconds(6), ct)
                    .ConfigureAwait(false);

                lock (_serverPingCacheLock)
                {
                    _serverPingCache[cacheKey] = new CachedServerPing(DateTimeOffset.UtcNow, result);
                }

                await Dispatcher.InvokeAsync(() => row.ApplyPing(result));
            }
            finally
            {
                gate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try
            {
                await Dispatcher.InvokeAsync(() => row.ApplyError(ex.Message));
            }
            catch (Exception)
            {
                // Окно закрывается — обновлять строку не нужно.
            }
        }
    }

    private void SetServersBusy(bool busy)
    {
        _serversBusy = busy;
        ServersRefreshButton.IsEnabled = !busy;
        ServersAddButton.IsEnabled = !busy;
        ServersDeleteButton.IsEnabled = !busy && _selectedServer is not null;
        ServerSaveButton.IsEnabled = !busy;
        ServerNameBox.IsEnabled = !busy;
        ServerAddressBox.IsEnabled = !busy;
    }

    private void UpdateServerActions()
    {
        ServersDeleteButton.IsEnabled = !_serversBusy && _selectedServer is not null;
    }

    private void ServerItem_Click(object sender, RoutedEventArgs e)
    {
        if (_serversBusy || sender is not FrameworkElement { Tag: ServerRow row })
        {
            return;
        }

        if (ReferenceEquals(_selectedServer, row))
        {
            return;
        }

        if (_selectedServer is not null)
        {
            _selectedServer.IsSelected = false;
        }

        _selectedServer = row;
        row.IsSelected = true;
        UpdateServerActions();
    }

    private async void QuickJoinServer_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: ServerRow row })
        {
            return;
        }

        if (_game is { IsRunning: true })
        {
            ShowToast("Быстрый вход", "Игра уже запущена. Сначала останови её.", "#F5C542");
            return;
        }

        if (_busy)
        {
            ShowToast("Быстрый вход", "Дождись завершения текущей операции.", "#F5C542");
            return;
        }

        if (string.IsNullOrWhiteSpace(row.Address))
        {
            ShowToast("Быстрый вход", "У сервера не указан адрес.", "#E5533D");
            return;
        }

        try
        {
            await StartGameAsync(row.Address);
        }
        catch (Exception ex)
        {
            ShowToast("Быстрый вход", ex.Message, "#E5533D");
        }
    }

    private async void RefreshServers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshServersAsync(notify: true, forcePing: true);
        }
        catch (Exception ex)
        {
            ServersStatusText.Text = "Ошибка: " + ex.Message;
            ShowToast("Серверы", ex.Message, "#E5533D");
        }
    }

    private void AddServer_Click(object sender, RoutedEventArgs e)
    {
        if (_serversBusy)
        {
            return;
        }

        ServerFormError.Text = "";
        ServerNameBox.Text = "";
        ServerAddressBox.Text = "";
        ServerAddPanel.Visibility = Visibility.Visible;
        ServerNameBox.Focus();
    }

    private void CancelServer_Click(object sender, RoutedEventArgs e)
    {
        ServerAddPanel.Visibility = Visibility.Collapsed;
        ServerFormError.Text = "";
    }

    private async void SaveServer_Click(object sender, RoutedEventArgs e)
    {
        if (_serversBusy)
        {
            return;
        }

        var name = ServerNameBox.Text.Trim();
        var address = ServerAddressBox.Text.Trim();

        if (name.Length == 0)
        {
            ServerFormError.Text = "Укажи имя сервера.";
            ServerNameBox.Focus();
            return;
        }

        if (address.Length == 0)
        {
            ServerFormError.Text = "Укажи адрес вида host или host:port.";
            ServerAddressBox.Focus();
            return;
        }

        try
        {
            SetServersBusy(true);
            ServerFormError.Text = "";

            var entry = new ServerEntry { Name = name, Host = address };
            await Task.Run(() => _serverList.AddOrUpdate(_instance, entry));

            ServerAddPanel.Visibility = Visibility.Collapsed;
            ServerNameBox.Text = "";
            ServerAddressBox.Text = "";
            ShowToast("Сервер добавлен", $"{name} · {address}", "#7CD964");
        }
        catch (LauncherException ex)
        {
            ServerFormError.Text = ex.Message;
            return;
        }
        catch (Exception ex)
        {
            ServerFormError.Text = ex.Message;
            ShowToast("Серверы", ex.Message, "#E5533D");
            return;
        }
        finally
        {
            SetServersBusy(false);
        }

        await RefreshServersAsync();
    }

    private async void DeleteServer_Click(object sender, RoutedEventArgs e)
    {
        var row = _selectedServer;
        if (row is null || _serversBusy)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Удалить сервер «{row.Name}» ({row.Address}) из servers.dat?",
            "Launcher — Серверы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetServersBusy(true);
            ServersStatusText.Text = "Удаление сервера...";

            var removed = await Task.Run(() => _serverList.Remove(_instance, row.Entry));
            if (removed)
            {
                _selectedServer = null;
                ShowToast("Сервер удалён", $"{row.Name} · {row.Address}", "#E5533D");
            }
            else
            {
                ShowToast("Сервер не найден", row.Address, "#F5C542");
            }
        }
        catch (Exception ex)
        {
            ServersStatusText.Text = "Удаление: " + ex.Message;
            ShowToast("Серверы", ex.Message, "#E5533D");
            return;
        }
        finally
        {
            SetServersBusy(false);
        }

        await RefreshServersAsync();
    }

    // ================= МОЙ СЕРВЕР =================

    private async Task RefreshHostPageAsync()
    {
        if (_hostBusy)
        {
            return;
        }

        PopulateHostFields();

        var cts = new CancellationTokenSource();
        var previous = _hostCts;
        _hostCts = cts;
        previous?.Cancel();
        var ct = cts.Token;

        HostProgressText.Text = "Проверка папки сервера...";

        try
        {
            var inspectTask = Task.Run(() => _serverHost.Inspect(_hostProfile), ct);
            var addressTask = _serverHost.GetAddressInfoAsync(_hostProfile.Port, ct);

            await Task.WhenAll(inspectTask, addressTask);
            ct.ThrowIfCancellationRequested();

            var inspection = inspectTask.Result;
            var addresses = addressTask.Result;

            UpdateHostInspectionUi(inspection);
            UpdateHostAddressUi(addresses);
            UpdateHostRuntimeUi();
            await RefreshHostWorldsAsync(ct);

            if (_hostProcess is { IsRunning: true })
            {
                HostProgressText.Text = _hostReady ? "Сервер готов." : "Сервер запускается...";
            }
            else
            {
                HostProgressText.Text = string.IsNullOrWhiteSpace(inspection.Error)
                    ? "Проверка завершена."
                    : inspection.Error;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HostProgressText.Text = ex.Message;
            ShowToast("Мой сервер", ex.Message, "#E5533D");
        }
        finally
        {
            if (ReferenceEquals(_hostCts, cts))
            {
                _hostCts = null;
            }

            cts.Dispose();
        }
    }

    private async Task RefreshHostWorldsAsync(CancellationToken ct = default)
    {
        if (_hostBusy)
        {
            return;
        }

        var version = ++_hostWorldsVersion;

        try
        {
            var state = await Task.Run(
                () => (
                    Snapshot: _serverWorlds.Inspect(_hostProfile),
                    Backups: _serverWorlds.ListBackups(_hostProfile)),
                ct);

            if (version != _hostWorldsVersion)
            {
                return;
            }

            _serverWorldRows.Clear();
            foreach (var world in state.Snapshot.Worlds)
            {
                _serverWorldRows.Add(new ServerWorldRow(world));
            }

            _serverWorldBackupRows.Clear();
            foreach (var backup in state.Backups)
            {
                _serverWorldBackupRows.Add(new ServerWorldBackupRow(backup));
            }

            HostWorldsCountText.Text = _serverWorldRows.Count.ToString(CultureInfo.InvariantCulture);
            HostBackupsCountText.Text = _serverWorldBackupRows.Count.ToString(CultureInfo.InvariantCulture);
            HostWorldsEmptyText.Visibility = _serverWorldRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            HostBackupsEmptyText.Visibility = _serverWorldBackupRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;

            var active = state.Snapshot.ActiveWorld;
            var parts = new List<string>();

            if (active is null)
            {
                parts.Add($"level-name: {state.Snapshot.LevelName}");
            }
            else if (!active.Exists)
            {
                parts.Add($"Активный мир «{active.Name}» ещё не создан");
            }
            else if (!active.IsValid)
            {
                parts.Add($"Активный мир «{active.Name}» повреждён");
            }
            else
            {
                parts.Add($"Активный мир: {active.Name}");
            }

            parts.Add($"бэкапов: {_serverWorldBackupRows.Count}");

            if (!string.IsNullOrWhiteSpace(state.Snapshot.Error))
            {
                parts.Add(state.Snapshot.Error);
            }

            HostWorldsStatusText.Text = string.Join(" · ", parts);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (version != _hostWorldsVersion)
            {
                return;
            }

            _serverWorldRows.Clear();
            _serverWorldBackupRows.Clear();
            HostWorldsEmptyText.Visibility = Visibility.Visible;
            HostBackupsEmptyText.Visibility = Visibility.Visible;
            HostWorldsStatusText.Text = "Ошибка чтения миров: " + ex.Message;
        }
    }

    private Progress<LauncherProgress> CreateHostWorldProgress() =>
        new(progress =>
        {
            OnProgress(progress);

            if (!string.IsNullOrWhiteSpace(progress.Message))
            {
                HostProgressText.Text = progress.Message;
            }
        });

    private static ServerWorldRow? GetHostWorldRow(object sender) =>
        sender is FrameworkElement { Tag: ServerWorldRow row } ? row : null;

    private static ServerWorldBackupRow? GetHostWorldBackupRow(object sender) =>
        sender is FrameworkElement { Tag: ServerWorldBackupRow row } ? row : null;

    private bool EnsureHostWorldsAccessible()
    {
        if (_hostProcess is { IsRunning: true })
        {
            ShowToast(
                "Сервер запущен",
                "Остановите сервер, чтобы менять миры и бэкапы.",
                "#F5C542");
            return false;
        }

        return !_hostBusy;
    }

    private async void CreateHostWorld_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureHostWorldsAccessible() || !TryReadHostSettings())
        {
            return;
        }

        var name = HostWorldNameBox.Text.Trim();
        var seed = HostWorldSeedBox.Text.Trim();
        string? created = null;

        await RunBusyAsync(
            "НОВЫЙ МИР",
            $"Подготовка мира: {name}",
            async ct =>
            {
                created = await _serverWorlds.CreateWorldAsync(
                    _hostProfile,
                    name,
                    seed,
                    CreateHostWorldProgress(),
                    ct);
            });

        if (created is null)
        {
            return;
        }

        HostWorldNameBox.Text = "new-world";
        HostWorldSeedBox.Text = "";
        await RefreshHostPageAsync();
        ShowToast(
            "Мир создан",
            $"«{created}» появится после запуска сервера.",
            "#7CD964");
    }

    private async void ActivateHostWorld_Click(object sender, RoutedEventArgs e)
    {
        var row = GetHostWorldRow(sender);
        if (row is null || !EnsureHostWorldsAccessible() || !TryReadHostSettings())
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Сделать мир «{row.Name}» активным?\n\n" +
            "Текущий активный мир будет сохранён в страховочный бэкап. " +
            "Изменение применится при следующем запуске сервера.",
            "Launcher — Миры сервера",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var activated = false;

        await RunBusyAsync(
            "АКТИВНЫЙ МИР",
            $"Переключение: {row.Name}",
            async ct =>
            {
                await _serverWorlds.ActivateWorldAsync(
                    _hostProfile,
                    row.FolderName,
                    CreateHostWorldProgress(),
                    ct);
                activated = true;
            });

        if (!activated)
        {
            return;
        }

        await RefreshHostPageAsync();
        ShowToast("Активный мир изменён", row.Name, "#7CD964");
    }

    private async void BackupHostActiveWorld_Click(object sender, RoutedEventArgs e)
    {
        var row = _serverWorldRows.FirstOrDefault(world => world.IsActive);
        await BackupHostWorldAsync(row, "БЭКАП АКТИВНОГО МИРА");
    }

    private async void BackupHostWorld_Click(object sender, RoutedEventArgs e)
    {
        var row = GetHostWorldRow(sender);
        await BackupHostWorldAsync(row, "БЭКАП МИРА СЕРВЕРА");
    }

    private async Task BackupHostWorldAsync(ServerWorldRow? row, string title)
    {
        if (!EnsureHostWorldsAccessible() || !TryReadHostSettings())
        {
            return;
        }

        if (row is null)
        {
            ShowToast("Бэкап мира", "Активный мир не найден.", "#F5C542");
            return;
        }

        if (!row.CanBackup)
        {
            ShowToast(
                "Бэкап мира",
                "Папка мира ещё не создана или level.dat повреждён.",
                "#F5C542");
            return;
        }

        var retention = Math.Clamp(_config.WorldBackupRetention, 1, 1000);
        string? created = null;

        await RunBusyAsync(
            title,
            $"Бэкап мира: {row.Name}",
            async ct =>
            {
                created = await _serverWorlds.BackupAsync(
                    _hostProfile,
                    row.FolderName,
                    retention,
                    CreateHostWorldProgress(),
                    ct);
            });

        if (created is null)
        {
            return;
        }

        await RefreshHostPageAsync();
        ShowToast("Бэкап серверного мира создан", Path.GetFileName(created), "#7CD964");
    }

    private async void DeleteHostWorld_Click(object sender, RoutedEventArgs e)
    {
        var row = GetHostWorldRow(sender);
        if (row is null || !EnsureHostWorldsAccessible() || !TryReadHostSettings())
        {
            return;
        }

        if (row.IsActive)
        {
            ShowToast(
                "Активный мир",
                "Нельзя удалить активный мир. Сначала активируйте другой.",
                "#F5C542");
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Удалить мир «{row.Name}»?\n\n" +
            "Папка мира будет перенесена в backups\\servers\\…\\removed-worlds, " +
            "безвозвратного удаления не будет.",
            "Launcher — Миры сервера",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var deleted = false;

        await RunBusyAsync(
            "УДАЛЕНИЕ МИРА",
            $"Перенос: {row.Name}",
            async ct =>
            {
                deleted = await _serverWorlds.DeleteWorldAsync(
                    _hostProfile,
                    row.FolderName,
                    ct);
            });

        if (!deleted)
        {
            ShowToast("Мир не найден", row.Name, "#F5C542");
            return;
        }

        await RefreshHostPageAsync();
        ShowToast("Мир удалён", $"{row.Name} → backups\\servers\\…\\removed-worlds", "#F5C542");
    }

    private void OpenHostWorldFolder_Click(object sender, RoutedEventArgs e)
    {
        var row = GetHostWorldRow(sender);
        if (row is null)
        {
            return;
        }

        try
        {
            _serverWorlds.OpenFolder(_hostProfile, row.FolderName);
        }
        catch (Exception ex)
        {
            ShowToast("Папка мира", ex.Message, "#E5533D");
        }
    }

    private void OpenHostBackupsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _serverWorlds.OpenBackupsFolder(_hostProfile);
        }
        catch (Exception ex)
        {
            ShowToast("Папка бэкапов", ex.Message, "#E5533D");
        }
    }

    private async void RestoreHostBackup_Click(object sender, RoutedEventArgs e)
    {
        var row = GetHostWorldBackupRow(sender);
        if (row is null || !EnsureHostWorldsAccessible() || !TryReadHostSettings())
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Восстановить мир из бэкапа?\n\n{row.FileName}\n\n" +
            "Текущий активный мир будет сохранён в страховочный бэкап. " +
            "Папка мира с тем же именем будет заменена.",
            "Launcher — Бэкапы сервера",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var restored = false;

        await RunBusyAsync(
            "ВОССТАНОВЛЕНИЕ МИРА",
            $"Распаковка: {row.FileName}",
            async ct =>
            {
                await _serverWorlds.RestoreAsync(
                    _hostProfile,
                    row.Info,
                    CreateHostWorldProgress(),
                    ct);
                restored = true;
            });

        if (!restored)
        {
            return;
        }

        await RefreshHostPageAsync();
        ShowToast("Мир восстановлен", row.WorldName, "#7CD964");
    }

    private async void DeleteHostBackup_Click(object sender, RoutedEventArgs e)
    {
        var row = GetHostWorldBackupRow(sender);
        if (row is null || !EnsureHostWorldsAccessible())
        {
            return;
        }

        var answer = MessageBox.Show(
            this,
            $"Удалить архив бэкапа?\n\n{row.FileName}",
            "Launcher — Бэкапы сервера",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        var deleted = false;

        await RunBusyAsync(
            "УДАЛЕНИЕ БЭКАПА",
            $"Удаление: {row.FileName}",
            async ct =>
            {
                deleted = await _serverWorlds.DeleteBackupAsync(
                    _hostProfile,
                    row.Info,
                    ct);
            });

        if (!deleted)
        {
            ShowToast("Бэкап не найден", row.FileName, "#F5C542");
            return;
        }

        await RefreshHostPageAsync();
        ShowToast("Бэкап удалён", row.FileName, "#F5C542");
    }

    private async void RefreshHostWorlds_Click(object sender, RoutedEventArgs e)
    {
        await RefreshHostWorldsAsync();
        ShowToast(
            "Миры сервера",
            $"Миров: {_serverWorldRows.Count} · бэкапов: {_serverWorldBackupRows.Count}",
            "#58C7F0");
    }

    private void HostToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_hostSuppressSettings || HostFormError is null)
        {
            return;
        }

        HostFormError.Text = "";
    }

    private void PopulateHostFields()
    {
        _hostSuppressSettings = true;
        try
        {
            HostDirBox.Text = _hostProfile.ServerDir ?? "";
            HostPortBox.Text = _hostProfile.Port.ToString(CultureInfo.InvariantCulture);
            HostMemoryBox.Text = _hostProfile.MemoryMb.ToString(CultureInfo.InvariantCulture);
            HostMinMemoryBox.Text = _hostProfile.MinMemoryMb.ToString(CultureInfo.InvariantCulture);
            HostMaxPlayersBox.Text = _hostProfile.MaxPlayers.ToString(CultureInfo.InvariantCulture);
            HostMotdBox.Text = _hostProfile.Motd ?? "";
            HostJavaPathBox.Text = string.IsNullOrWhiteSpace(_hostProfile.JavaPath)
                ? "auto"
                : _hostProfile.JavaPath;
            HostPackUrlBox.Text = _hostProfile.PackUrl ?? "";
            HostOnlineModeToggle.IsChecked = _hostProfile.OnlineMode;
            HostWhitelistToggle.IsChecked = _hostProfile.Whitelist;

            if (string.IsNullOrWhiteSpace(HostWorldNameBox.Text))
            {
                HostWorldNameBox.Text = "new-world";
            }
        }
        finally
        {
            _hostSuppressSettings = false;
        }
    }

    private bool TryReadHostSettings()
    {
        var rawDir = HostDirBox.Text.Trim().Trim('"');

        if (rawDir.Length == 0)
        {
            HostFormError.Text = "Укажите папку сервера.";
            return false;
        }

        string serverDir;

        try
        {
            serverDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(rawDir));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            HostFormError.Text = $"Некорректный путь к папке сервера: {rawDir}";
            return false;
        }

        if (!int.TryParse(HostPortBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port)
            || port is < 1 or > 65535)
        {
            HostFormError.Text = "Порт должен быть числом от 1 до 65535.";
            return false;
        }

        if (!int.TryParse(HostMemoryBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var memory)
            || memory is < 1024 or > 32768)
        {
            HostFormError.Text = "Максимальная память должна быть от 1024 до 32768 МБ.";
            return false;
        }

        if (!int.TryParse(HostMinMemoryBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var minMemory)
            || minMemory is < 512 or > 32768)
        {
            HostFormError.Text = "Минимальная память должна быть от 512 до 32768 МБ.";
            return false;
        }

        if (minMemory > memory)
        {
            HostFormError.Text = "Минимальная память не может быть больше максимальной.";
            return false;
        }

        if (!int.TryParse(HostMaxPlayersBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var maxPlayers)
            || maxPlayers is < 1 or > 100)
        {
            HostFormError.Text = "Максимум игроков должен быть числом от 1 до 100.";
            return false;
        }

        var motd = HostMotdBox.Text.Trim();
        if (motd.Length == 0)
        {
            motd = "Сервер 1.20.1";
        }

        var javaPath = HostJavaPathBox.Text.Trim();
        if (javaPath.Length == 0)
        {
            javaPath = "auto";
        }

        var packUrl = HostPackUrlBox.Text.Trim();

        _hostProfile.ServerDir = serverDir;
        _hostProfile.Port = port;
        _hostProfile.MemoryMb = memory;
        _hostProfile.MinMemoryMb = minMemory;
        _hostProfile.MaxPlayers = maxPlayers;
        _hostProfile.Motd = motd;
        _hostProfile.JavaPath = javaPath;
        _hostProfile.PackUrl = packUrl.Length == 0 ? null : packUrl;
        _hostProfile.OnlineMode = HostOnlineModeToggle.IsChecked == true;
        _hostProfile.Whitelist = HostWhitelistToggle.IsChecked == true;

        HostFormError.Text = "";
        return true;
    }

    private async void RefreshHost_Click(object sender, RoutedEventArgs e)
    {
        await RefreshHostPageAsync();
    }

    private async void SaveHostSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_hostBusy || _hostProcess is { IsRunning: true } || !TryReadHostSettings())
        {
            return;
        }

        SetHostBusy(true);

        try
        {
            _serverHost.ApplySettings(_hostProfile);
            _config.LastHostedServerId = _hostProfile.Id;
            TrySaveConfig();

            var note = _hostProcess is { IsRunning: true }
                ? " Порт и память применятся после перезапуска сервера."
                : "";
            ShowToast("Настройки сервера сохранены", _hostProfile.Name + "." + note, "#7CD964");
        }
        catch (Exception ex)
        {
            HostFormError.Text = ex.Message;
            ShowToast("Настройки сервера", ex.Message, "#E5533D");
        }
        finally
        {
            SetHostBusy(false);
        }

        await RefreshHostPageAsync();
    }

    private void BrowseHostFolder_Click(object sender, RoutedEventArgs e)
    {
        var initial = HostDirBox.Text.Trim().Trim('"');

        if (!Directory.Exists(initial))
        {
            initial = !string.IsNullOrWhiteSpace(_instance.GameDir) && Directory.Exists(_instance.GameDir)
                ? _instance.GameDir
                : "";
        }

        var dialog = new OpenFolderDialog
        {
            Title = "Папка сервера",
            InitialDirectory = initial
        };

        if (dialog.ShowDialog(this) == true)
        {
            HostDirBox.Text = dialog.FolderName;
        }
    }

    private void FindHostServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var found = ServerHostService.FindExistingServer(_instance);

            if (found is null)
            {
                ShowToast(
                    "Сервер не найден",
                    "Укажите папку сервера вручную.",
                    "#F5C542");
                return;
            }

            HostDirBox.Text = found;
            ShowToast("Сервер найден", found, "#7CD964");
        }
        catch (Exception ex)
        {
            ShowToast("Поиск сервера", ex.Message, "#E5533D");
        }
    }

    private void OpenHostFolder_Click(object sender, RoutedEventArgs e)
    {
        var raw = HostDirBox.Text.Trim().Trim('"');

        if (raw.Length == 0)
        {
            ShowToast("Папка сервера", "Сначала укажите папку сервера.", "#F5C542");
            return;
        }

        string path;

        try
        {
            path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(raw));
        }
        catch (Exception ex) when (
            ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            ShowToast("Папка сервера", $"Некорректный путь: {raw}", "#E5533D");
            return;
        }

        if (!Directory.Exists(path))
        {
            ShowToast("Папка не найдена", path, "#E5533D");
            return;
        }

        OpenPath(path);
    }

    private async void InstallHostServer_Click(object sender, RoutedEventArgs e)
    {
        if (_hostBusy || _hostProcess is { IsRunning: true })
        {
            return;
        }

        if (HostEulaCheck.IsChecked != true)
        {
            HostFormError.Text = "Для установки нужно явно принять EULA Minecraft.";
            ShowToast("EULA", "Поставьте галочку согласия с EULA Minecraft.", "#F5C542");
            return;
        }

        if (!TryReadHostSettings())
        {
            return;
        }

        SetHostBusy(true);
        HostProgress.Value = 0;
        HostProgressText.Text = "Установка...";

        var cts = new CancellationTokenSource();
        var previous = _hostCts;
        _hostCts = cts;
        previous?.Cancel();
        var ct = cts.Token;

        try
        {
            var progress = CreateHostProgress();

            await _serverHost.InstallAsync(
                _hostProfile,
                acceptEula: true,
                updatePack: false,
                progress,
                ct);

            _config.LastHostedServerId = _hostProfile.Id;
            TrySaveConfig();
            HostProgressText.Text = "Сервер установлен.";
            ShowToast(
                "Сервер установлен",
                _hostProfile.Name + " готов к запуску. Моды обновляются кнопкой «ОБНОВИТЬ МОДЫ».",
                "#7CD964");
        }
        catch (OperationCanceledException)
        {
            HostProgressText.Text = "Установка отменена.";
        }
        catch (Exception ex)
        {
            HostProgressText.Text = ex.Message;
            HostFormError.Text = ex.Message;
            ShowToast("Ошибка установки", ex.Message, "#E5533D");
        }
        finally
        {
            if (ReferenceEquals(_hostCts, cts))
            {
                _hostCts = null;
            }

            cts.Dispose();
            SetHostBusy(false);
        }

        await RefreshHostPageAsync();
    }

    private async void UpdateHostPack_Click(object sender, RoutedEventArgs e)
    {
        if (_hostBusy || _hostProcess is { IsRunning: true })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(HostPackUrlBox.Text))
        {
            HostFormError.Text = "Укажите PackUrl — URL или путь к pack.toml.";
            ShowToast("Обновление модов", "У сервера не указан PackUrl.", "#F5C542");
            return;
        }

        if (!TryReadHostSettings())
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_hostProfile.PackUrl))
        {
            HostFormError.Text = "Укажите PackUrl — URL или путь к pack.toml.";
            return;
        }

        SetHostBusy(true);
        HostProgress.Value = 0;
        HostProgressText.Text = "Обновление модов...";

        var cts = new CancellationTokenSource();
        var previous = _hostCts;
        _hostCts = cts;
        previous?.Cancel();
        var ct = cts.Token;

        try
        {
            var progress = CreateHostProgress();
            await _serverHost.UpdatePackAsync(_hostProfile, progress, ct);

            _config.LastHostedServerId = _hostProfile.Id;
            TrySaveConfig();
            HostProgressText.Text = "Моды обновлены.";
            ShowToast("Моды обновлены", _hostProfile.Name, "#7CD964");
        }
        catch (OperationCanceledException)
        {
            HostProgressText.Text = "Обновление отменено.";
        }
        catch (Exception ex)
        {
            HostProgressText.Text = ex.Message;
            HostFormError.Text = ex.Message;
            ShowToast("Ошибка обновления модов", ex.Message, "#E5533D");
        }
        finally
        {
            if (ReferenceEquals(_hostCts, cts))
            {
                _hostCts = null;
            }

            cts.Dispose();
            SetHostBusy(false);
        }

        await RefreshHostPageAsync();
    }

    private async void StartHostServer_Click(object sender, RoutedEventArgs e)
    {
        if (_hostBusy || _hostProcess is { IsRunning: true })
        {
            return;
        }

        if (!TryReadHostSettings())
        {
            return;
        }

        SetHostBusy(true);
        HostProgress.Value = 0;
        HostProgressText.Text = "Поиск Java и запуск...";

        var cts = new CancellationTokenSource();
        var previous = _hostCts;
        _hostCts = cts;
        previous?.Cancel();
        var ct = cts.Token;

        try
        {
            var progress = CreateHostProgress();
            var java = await _serverHost.ResolveJavaAsync(_hostProfile, progress, ct);
            ct.ThrowIfCancellationRequested();

            var process = _serverHost.Start(_hostProfile, java);

            _hostProcess = process;
            _hostStartedAt = DateTime.Now;
            _hostReady = false;
            _hostPortInUse = false;
            _hostConsole.Clear();
            HostConsoleBox.Clear();

            process.LineReceived += line =>
                Dispatcher.BeginInvoke(new Action(() => OnHostLine(line)));
            process.Exited += code =>
                Dispatcher.BeginInvoke(new Action(() => OnHostExited(code)));

            StartHostTimers();
            UpdateHostRuntimeUi();

            _config.LastHostedServerId = _hostProfile.Id;
            TrySaveConfig();

            HostProgressText.Text = "Сервер запускается...";
            ShowToast(
                "Сервер запускается",
                $"{_hostProfile.Name} · {ServerAddressInfo.Format("127.0.0.1", _hostProfile.Port)}",
                "#58C7F0");
        }
        catch (OperationCanceledException)
        {
            HostProgressText.Text = "Запуск отменён.";
        }
        catch (Exception ex)
        {
            HostProgressText.Text = ex.Message;
            ShowToast("Не удалось запустить сервер", ex.Message, "#E5533D");
        }
        finally
        {
            if (ReferenceEquals(_hostCts, cts))
            {
                _hostCts = null;
            }

            cts.Dispose();
            SetHostBusy(false);
        }
    }

    private async void StopHostServer_Click(object sender, RoutedEventArgs e)
    {
        if (_hostProcess is not { IsRunning: true } || _hostBusy)
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            "Остановить сервер? Игроки будут отключены.",
            "Launcher — Мой сервер",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        await StopHostServerAsync();
    }

    private async Task StopHostServerAsync(bool silent = false)
    {
        if (_hostStopping)
        {
            return;
        }

        var process = _hostProcess;

        if (process is null)
        {
            return;
        }

        _hostStopping = true;
        SetHostBusy(true);
        HostProgressText.Text = "Остановка сервера...";

        var disposed = false;

        try
        {
            var graceful = await process.StopAsync(TimeSpan.FromSeconds(60));

            process.Dispose();
            disposed = true;

            if (ReferenceEquals(_hostProcess, process))
            {
                _hostProcess = null;
            }

            _hostReady = false;
            _hostPortInUse = false;
            StopHostTimers();
            UpdateHostRuntimeUi();
            TrySaveConfig();

            HostProgressText.Text = graceful
                ? "Сервер остановлен."
                : "Сервер остановлен принудительно.";

            if (!silent)
            {
                ShowToast(
                    graceful ? "Сервер остановлен" : "Сервер остановлен принудительно",
                    graceful ? "Все миры сохранены." : "Сервер не ответил на stop и был завершён.",
                    graceful ? "#7CD964" : "#F5C542");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            HostProgressText.Text = ex.Message;

            if (!silent)
            {
                ShowToast("Остановка сервера", ex.Message, "#E5533D");
            }
        }
        finally
        {
            // Если StopAsync упал, но процесс уже завершился, не оставляем
            // мёртвый ServerProcess в поле и освобождаем его один раз.
            if (!disposed && !process.IsRunning)
            {
                if (ReferenceEquals(_hostProcess, process))
                {
                    _hostProcess = null;
                }

                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                    // Процесс уже мог быть освобождён другим путём.
                }
            }

            _hostStopping = false;
            SetHostBusy(false);
        }
    }

    private Progress<LauncherProgress> CreateHostProgress() =>
        new(progress =>
        {
            if (!_hostBusy)
            {
                return;
            }

            if (progress.Percent is double percent)
            {
                HostProgress.Value = percent;
            }

            if (!string.IsNullOrWhiteSpace(progress.Message))
            {
                HostProgressText.Text = progress.Message;
            }
        });

    private void OnHostLine(LogLine line)
    {
        if (line is null)
        {
            return;
        }

        var prefix = line.IsError ? "! " : "";
        AppendHostConsole(
            $"[{line.Timestamp.ToString("HH:mm:ss", CultureInfo.InvariantCulture)}] {prefix}{line.Text}");

        if (line.Text.Contains("Done (", StringComparison.Ordinal))
        {
            _hostReady = true;
            UpdateHostRuntimeUi();
            HostProgressText.Text = "Сервер готов.";
        }
        else if (line.Text.Contains("joined the game", StringComparison.Ordinal)
                 || line.Text.Contains("left the game", StringComparison.Ordinal))
        {
            _ = PingHostServerAsync();
        }
    }

    private void AppendHostConsole(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        _hostConsole.AppendLine(text);

        if (_hostConsole.Length > 200_000)
        {
            var keep = _hostConsole.Length - 100_000;
            var tail = _hostConsole.ToString(keep, _hostConsole.Length - keep);
            _hostConsole.Clear();
            _hostConsole.Append(tail);
            HostConsoleBox.Text = _hostConsole.ToString();
        }
        else
        {
            HostConsoleBox.AppendText(text + Environment.NewLine);
        }

        HostConsoleBox.ScrollToEnd();
    }

    private void OnHostExited(int exitCode)
    {
        _hostReady = false;
        _hostPortInUse = false;
        StopHostTimers();

        var process = _hostProcess;

        // Во время StopHostServerAsync процессом владеет вызывающий метод:
        // он дождётся StopAsync и сам сделает Dispose. Здесь трогать процесс нельзя,
        // иначе StopAsync может получить ObjectDisposedException.
        if (!_hostStopping && process is not null && !process.IsRunning)
        {
            _hostProcess = null;
            process.Dispose();
        }

        UpdateHostRuntimeUi();
        HostProgressText.Text = $"Сервер остановлен (код {exitCode}).";

        if (exitCode != 0 && !_closingAfterServerStop)
        {
            ShowToast("Сервер завершился", $"Код {exitCode}. Смотрите консоль.", "#E5533D");
        }
    }

    private void StartHostTimers()
    {
        StopHostTimers();

        _hostUptimeTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _hostUptimeTimer.Tick += (_, _) => UpdateHostRuntimeUi();
        _hostUptimeTimer.Start();

        _hostPingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _hostPingTimer.Tick += async (_, _) => await PingHostServerAsync();
        _hostPingTimer.Start();

        _ = PingHostServerAsync();
    }

    private void StopHostTimers()
    {
        _hostUptimeTimer?.Stop();
        _hostUptimeTimer = null;
        _hostPingTimer?.Stop();
        _hostPingTimer = null;
    }

    private async Task PingHostServerAsync()
    {
        if (_hostProcess?.IsRunning != true || _hostPingBusy)
        {
            return;
        }

        _hostPingBusy = true;

        try
        {
            var ping = await _serverPinger.PingAsync(
                "127.0.0.1",
                _hostProfile.Port,
                TimeSpan.FromSeconds(3));

            if (_hostProcess?.IsRunning != true)
            {
                return;
            }

            HostPlayersText.Text = ping.Online
                ? $"{ping.PlayersOnline} / {ping.PlayersMax}"
                : "нет ответа";
        }
        catch (Exception)
        {
            if (_hostProcess?.IsRunning == true)
            {
                HostPlayersText.Text = "нет ответа";
            }
        }
        finally
        {
            _hostPingBusy = false;
        }
    }

    private void UpdateHostRuntimeUi()
    {
        var running = _hostProcess is { IsRunning: true };

        if (running)
        {
            HostStatusText.Text = _hostReady ? "РАБОТАЕТ" : "ЗАПУСК...";
            HostStatusText.Foreground = PixelPalette.Text;
            HostStatusDot.Fill = _hostReady ? PixelPalette.GreenHi : PixelPalette.Gold;
            HostStatusPill.BorderBrush = _hostReady ? PixelPalette.Green : PixelPalette.Gold;

            var uptime = DateTime.Now - _hostStartedAt;
            if (uptime < TimeSpan.Zero)
            {
                uptime = TimeSpan.Zero;
            }

            HostUptimeText.Text =
                $"{(int)uptime.TotalHours:00}:{uptime.Minutes:00}:{uptime.Seconds:00}";
        }
        else
        {
            if (_hostPortInUse)
            {
                HostStatusText.Text = "ПОРТ ЗАНЯТ";
                HostStatusText.Foreground = PixelPalette.Gold;
                HostStatusDot.Fill = PixelPalette.Gold;
                HostStatusPill.BorderBrush = PixelPalette.Gold;
            }
            else
            {
                HostStatusText.Text = "ОСТАНОВЛЕН";
                HostStatusText.Foreground = PixelPalette.Muted;
                HostStatusDot.Fill = PixelPalette.Red;
                HostStatusPill.BorderBrush = PixelPalette.Line;
            }

            HostUptimeText.Text = "—";
            HostPlayersText.Text = "—";
        }

        HostStartButton.IsEnabled = !running && !_hostBusy;
        HostStopButton.IsEnabled = running && !_hostBusy;
        HostSendButton.IsEnabled = running && !_hostBusy;
        HostCommandBox.IsEnabled = running && !_hostBusy;

        // Пока сервер работает, не даём перезаписывать server.properties,
        // ставить Forge/моды и менять EULA. Проверка остаётся доступной.
        HostSaveButton.IsEnabled = !running && !_hostBusy;
        HostCheckButton.IsEnabled = !_hostBusy;
        HostInstallButton.IsEnabled = !running && !_hostBusy;
        HostUpdatePackButton.IsEnabled = !running && !_hostBusy;
        HostEulaCheck.IsEnabled = !running && !_hostBusy;

        // Миры и бэкапы меняются только при остановленном сервере.
        var worldsEnabled = !running && !_hostBusy;
        HostWorldNameBox.IsEnabled = worldsEnabled;
        HostWorldSeedBox.IsEnabled = worldsEnabled;
        HostCreateWorldButton.IsEnabled = worldsEnabled;
        HostBackupActiveButton.IsEnabled = worldsEnabled;
        HostWorldsRefreshButton.IsEnabled = worldsEnabled;
        HostOpenBackupsButton.IsEnabled = worldsEnabled;
        HostWorldsList.IsEnabled = worldsEnabled;
        HostBackupsList.IsEnabled = worldsEnabled;
    }

    private void UpdateHostInspectionUi(ServerInspection inspection)
    {
        _hostPortInUse = inspection.PortInUse;

        HostDetailText.Text = inspection.ForgeInstalled
            ? $"Forge {inspection.InstalledForgeVersion ?? "1.20.1-47.4.28"} · " +
              $"модов: {inspection.ModCount} · миров: {inspection.Worlds.Count}"
            : "Forge не установлен";

        HostEulaStatusText.Text = inspection.EulaAccepted ? "EULA принята" : "EULA не принята";
        HostEulaStatusText.Foreground = inspection.EulaAccepted
            ? PixelPalette.GreenHi
            : PixelPalette.Red;

        HostPortStatusText.Text = inspection.PortInUse
            ? $"Порт {_hostProfile.Port} занят"
            : $"Порт {_hostProfile.Port} свободен";
        HostPortStatusText.Foreground = inspection.PortInUse
            ? PixelPalette.Gold
            : PixelPalette.GreenHi;

        HostModsText.Text = $"Модов: {inspection.ModCount}";

        HostWorldsText.Text = inspection.Worlds.Count == 0
            ? "Миров: нет"
            : "Миры: " + string.Join(", ", inspection.Worlds.Take(3)) +
              (inspection.Worlds.Count > 3 ? "…" : "");

        _hostSuppressSettings = true;
        try
        {
            HostEulaCheck.IsChecked = inspection.EulaAccepted;
        }
        finally
        {
            _hostSuppressSettings = false;
        }

        HostFormError.Text = inspection.Error ?? "";

        UpdateHostRuntimeUi();
    }

    private void UpdateHostAddressUi(ServerAddressInfo info)
    {
        var port = info.Port;
        HostLoopbackAddressText.Text = ServerAddressInfo.Format("127.0.0.1", port);
        HostLoopbackAddressText.Foreground = PixelPalette.Text;

        var localAddresses = info.LocalAddresses ?? Array.Empty<string>();
        var local = info.PrimaryLocal;
        _hostLanAddress = local is null ? null : ServerAddressInfo.Format(local, port);

        if (_hostLanAddress is null)
        {
            HostLanAddressText.Text = "не определён";
            HostLanAddressText.Foreground = PixelPalette.Muted;
        }
        else
        {
            var suffix = localAddresses.Count > 1 ? $" +{localAddresses.Count - 1}" : "";
            HostLanAddressText.Text = _hostLanAddress + suffix;
            HostLanAddressText.Foreground = PixelPalette.Text;
        }

        _hostPublicAddress = info.PrimaryPublic is null
            ? null
            : ServerAddressInfo.Format(info.PrimaryPublic, port);

        HostPublicAddressText.Text = _hostPublicAddress ?? "не определён";
        HostPublicAddressText.Foreground = _hostPublicAddress is null
            ? PixelPalette.Muted
            : PixelPalette.Text;

        HostCopyLoopbackButton.IsEnabled = true;
        HostCopyLanButton.IsEnabled = _hostLanAddress is not null;
        HostCopyPublicButton.IsEnabled = _hostPublicAddress is not null;

        HostAddressHintText.Text = _hostPublicAddress is null
            ? "Друг в той же сети подключается по LAN-адресу. Публичный IP не определился — " +
              "используйте Radmin VPN или проброс TCP-порта на роутере. " +
              "При первом запуске разрешите Java в брандмауэре Windows."
            : "Друг в той же сети подключается по LAN-адресу. Из интернета — публичный IP " +
              "и проброс TCP-порта на роутере, либо Radmin VPN. " +
              "При первом запуске разрешите Java в брандмауэре Windows.";
    }

    private void CopyHostLoopback_Click(object sender, RoutedEventArgs e)
    {
        CopyHostAddress(HostLoopbackAddressText.Text, "Локальный адрес");
    }

    private void CopyHostLan_Click(object sender, RoutedEventArgs e)
    {
        CopyHostAddress(_hostLanAddress, "LAN-адрес");
    }

    private void CopyHostPublic_Click(object sender, RoutedEventArgs e)
    {
        CopyHostAddress(_hostPublicAddress, "Публичный адрес");
    }

    private void CopyHostAddress(string? address, string title)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            ShowToast(title, "Адрес не определён.", "#F5C542");
            return;
        }

        try
        {
            Clipboard.SetText(address);
            ShowToast("Адрес скопирован", address, "#58C7F0");
        }
        catch (Exception ex)
        {
            ShowToast("Не удалось скопировать", ex.Message, "#E5533D");
        }
    }

    private void HostAddToList_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _serverList.AddOrUpdate(
                _instance,
                new ServerEntry
                {
                    Name = _hostProfile.Name,
                    Host = "127.0.0.1",
                    Port = _hostProfile.Port
                });

            ShowToast(
                "Добавлено в список серверов",
                $"{_hostProfile.Name} · {ServerAddressInfo.Format("127.0.0.1", _hostProfile.Port)}",
                "#7CD964");
        }
        catch (Exception ex)
        {
            ShowToast("Список серверов", ex.Message, "#E5533D");
        }
    }

    private async void SendHostCommand_Click(object sender, RoutedEventArgs e)
    {
        await SendHostCommandAsync();
    }

    private void HostCommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = SendHostCommandAsync();
        }
    }

    private async Task SendHostCommandAsync()
    {
        var process = _hostProcess;

        if (process is not { IsRunning: true })
        {
            ShowToast("Сервер не запущен", "Сначала запустите сервер.", "#F5C542");
            return;
        }

        var command = HostCommandBox.Text.Trim();

        if (command.Length == 0)
        {
            return;
        }

        try
        {
            await process.SendCommandAsync(command);
            HostCommandBox.Clear();
            AppendHostConsole("> " + command);
        }
        catch (Exception ex)
        {
            ShowToast("Команда серверу", ex.Message, "#E5533D");
        }
    }

    private void SetHostBusy(bool busy)
    {
        _hostBusy = busy;

        HostDirBox.IsEnabled = !busy;
        HostBrowseButton.IsEnabled = !busy;
        HostFindButton.IsEnabled = !busy;
        HostOpenFolderButton.IsEnabled = !busy;
        HostPortBox.IsEnabled = !busy;
        HostMemoryBox.IsEnabled = !busy;
        HostMinMemoryBox.IsEnabled = !busy;
        HostMaxPlayersBox.IsEnabled = !busy;
        HostMotdBox.IsEnabled = !busy;
        HostJavaPathBox.IsEnabled = !busy;
        HostPackUrlBox.IsEnabled = !busy;
        HostOnlineModeToggle.IsEnabled = !busy;
        HostWhitelistToggle.IsEnabled = !busy;
        HostAddToListButton.IsEnabled = !busy;

        // Install/update/save/EULA зависят ещё и от запущенного сервера,
        // поэтому их состояние выставляет UpdateHostRuntimeUi.
        UpdateHostRuntimeUi();
    }

    // ================= РЕСУРСЫ =================

    private string GetResourcesTab() =>
        ResourceShadersTab.IsChecked == true ? "shaders" : "resourcepacks";

    private async Task RefreshResourcesAsync(bool force = false)
    {
        if (PacksList is null || (_resourcesBusy && !force))
        {
            return;
        }

        var tab = GetResourcesTab();
        var page = _currentPage;
        var label = tab == "shaders" ? "Шейдерпаков" : "Ресурспаков";
        var instance = _instance;
        var version = ++_resourcesVersion;

        try
        {
            var both = await Task.Run(() => (Resources: _resourcePacks.List(instance, includeIcon: false), Shaders: _shaderPacks.List(instance, includeIcon: false)));
            var packs = tab == "shaders" ? both.Shaders : both.Resources;

            if (version != _resourcesVersion
                || !IsCurrentInstance(instance)
                || !string.Equals(_currentPage, page, StringComparison.Ordinal)
                || !string.Equals(GetResourcesTab(), tab, StringComparison.Ordinal))
            {
                return;
            }

            _installedPacks = both.Resources.Select(pack => new PackRow(pack, "resourcepacks")).Concat(both.Shaders.Select(pack => new PackRow(pack, "shaders"))).ToList();
            RefreshInstalledContent();
            _packRows.Clear();
            foreach (var pack in packs)
            {
                _packRows.Add(new PackRow(pack, tab));
            }

            var enabled = _packRows.Count(row => row.Enabled);
            PacksEmptyText.Text = "Пакетов нет. Нажми ИМПОРТ";
            PacksEmptyText.Visibility = _packRows.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ResourcesStatusText.Text = $"{label}: {_packRows.Count} · включено: {enabled}";
            _refreshBuildPacks?.Invoke();
        }
        catch (Exception ex)
        {
            if (version != _resourcesVersion
                || !IsCurrentInstance(instance)
                || !string.Equals(_currentPage, page, StringComparison.Ordinal))
            {
                return;
            }

            _packRows.Clear();
            PacksEmptyText.Text = "Не удалось прочитать пакеты";
            PacksEmptyText.Visibility = Visibility.Visible;
            ResourcesStatusText.Text = "Ошибка: " + ex.Message;
            _refreshBuildPacks?.Invoke();
        }
    }

    private void SetResourcesBusy(bool busy)
    {
        _resourcesBusy = busy;
        ResourcesImportButton.IsEnabled = !busy;
        ResourcesFolderButton.IsEnabled = !busy;
        ResourcesRefreshButton.IsEnabled = !busy;
        PacksList.IsEnabled = !busy;
    }

    private async void ResourcesTab_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || PacksList is null)
        {
            return;
        }

        try
        {
            await RefreshResourcesAsync(force: true);
        }
        catch (Exception ex)
        {
            ShowToast("Ресурсы", ex.Message, "#E5533D");
        }
    }

    private async void ImportResources_Click(object sender, RoutedEventArgs e)
    {
        if (_resourcesBusy)
        {
            return;
        }

        var tab = GetResourcesTab();
        var shaders = tab == "shaders";

        var dialog = new OpenFileDialog
        {
            Title = shaders ? "Импорт шейдерпаков" : "Импорт ресурспаков",
            Filter = "Архивы паков (*.zip)|*.zip|Все файлы (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var instance = _instance;
        var files = dialog.FileNames;

        try
        {
            SetResourcesBusy(true);
            ResourcesStatusText.Text = shaders ? "Импорт шейдерпаков..." : "Импорт ресурспаков...";

            var imported = await Task.Run<IReadOnlyList<PackInfo>>(() => shaders
                ? _shaderPacks.Import(instance, files)
                : _resourcePacks.Import(instance, files));

            ShowToast(
                "Импорт завершён",
                $"{(shaders ? "Шейдерпаков" : "Ресурспаков")}: {imported.Count}",
                "#7CD964");
        }
        catch (Exception ex)
        {
            ShowToast("Импорт паков", ex.Message, "#E5533D");
        }
        finally
        {
            SetResourcesBusy(false);
            await RefreshResourcesAsync();
        }
    }

    private void OpenResourcesFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_resourcesBusy)
        {
            return;
        }

        try
        {
            if (GetResourcesTab() == "shaders")
            {
                _shaderPacks.OpenFolder(_instance);
            }
            else
            {
                _resourcePacks.OpenFolder(_instance);
            }
        }
        catch (Exception ex)
        {
            ShowToast("Папка паков", ex.Message, "#E5533D");
        }
    }

    private async void RefreshResources_Click(object sender, RoutedEventArgs e)
    {
        if (_resourcesBusy)
        {
            return;
        }

        try
        {
            await RefreshResourcesAsync(force: true);
            ShowToast("Ресурсы обновлены", ResourcesStatusText.Text, "#58C7F0");
        }
        catch (Exception ex)
        {
            ShowToast("Ресурсы", ex.Message, "#E5533D");
        }
    }

    private async void TogglePack_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (_resourcesBusy || sender is not FrameworkElement { Tag: PackRow row })
        {
            return;
        }

        var instance = _instance;
        var enabled = !row.Enabled;

        try
        {
            SetResourcesBusy(true);
            await Task.Run(() =>
            {
                if (row.IsShader)
                {
                    _shaderPacks.SetEnabled(instance, row.FileName, enabled);
                }
                else
                {
                    _resourcePacks.SetEnabled(instance, row.FileName, enabled);
                }
            });

            row.Enabled = enabled;
            ShowToast(
                enabled ? "Пак включён" : "Пак выключен",
                row.Title,
                enabled ? "#7CD964" : "#94A0AC");
        }
        catch (Exception ex)
        {
            ShowToast("Ресурсы", ex.Message, "#E5533D");
        }
        finally
        {
            SetResourcesBusy(false);
            await RefreshResourcesAsync();
        }
    }

    private async void DeletePack_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (_resourcesBusy || sender is not FrameworkElement { Tag: PackRow row })
        {
            return;
        }

        var result = MessageBox.Show(
            this,
            $"Удалить пак «{row.Title}»?\nФайл будет перемещён в бэкап.",
            "Launcher — Ресурсы",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        var instance = _instance;

        try
        {
            SetResourcesBusy(true);
            ResourcesStatusText.Text = "Удаление пака...";

            await Task.Run(() =>
            {
                if (row.IsShader)
                {
                    _shaderPacks.Delete(row.Pack, moveToBackups: true);
                }
                else
                {
                    _resourcePacks.Delete(row.Pack, moveToBackups: true);
                }
            });

            ShowToast("Пак удалён", row.FileName, "#E5533D");
        }
        catch (Exception ex)
        {
            ShowToast("Ресурсы", ex.Message, "#E5533D");
        }
        finally
        {
            SetResourcesBusy(false);
            await RefreshResourcesAsync();
        }
    }

    // ================= ДИАГНОСТИКА =================

    private void PrepareDoctorPage()
    {
        if (_doctorReport is not null &&
            !string.Equals(_doctorReportInstanceId, _instance.Id, StringComparison.OrdinalIgnoreCase))
        {
            ResetDoctorReport();
        }

        DoctorEmptyText.Visibility = _doctorRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateDoctorActions();
    }

    private void ResetDoctorReport()
    {
        _doctorReport = null;
        _doctorReportInstanceId = null;
        _doctorRows.Clear();
        DoctorSummaryText.Text = "Ошибок: — · Предупреждений: — · Проверок: —";
        DoctorDurationText.Text = "время выполнения: —";
        DoctorCountText.Text = "—";
        DoctorProgress.Value = 0;
        DoctorProgressPercent.Text = "";
        DoctorProgressText.Text = "Готово к проверке.";
        DoctorStatusText.Text = "";
        DoctorSummaryAccent.Background = PixelPalette.Muted;
        DoctorEmptyText.Visibility = Visibility.Visible;
        UpdateDoctorActions();
    }

    private async Task RunDoctorAsync(bool fixFirst)
    {
        if (_doctorBusy)
        {
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = _doctorCts;
        _doctorCts = cts;
        previous?.Cancel();

        SetDoctorBusy(true);
        DoctorProgress.Value = 0;
        DoctorProgressPercent.Text = "0%";
        DoctorProgressText.Text = fixFirst ? "Автоисправление..." : "Подготовка...";
        DoctorStatusText.Text = DoctorProgressText.Text;
        DoctorEmptyText.Visibility = Visibility.Collapsed;

        if (fixFirst)
        {
            DoctorSummaryAccent.Background = PixelPalette.Gold;
        }

        try
        {
            IProgress<LauncherProgress> progress = new Progress<LauncherProgress>(OnDoctorProgress);
            var fixedCount = 0;

            if (fixFirst)
            {
                if (_doctorReport is null)
                {
                    return;
                }

                fixedCount = await _doctorFix.FixAsync(_instance, _doctorReport, progress, cts.Token);
                progress.Report(new LauncherProgress(
                    ProgressStage.Verify,
                    "Повторная проверка после исправлений..."));
            }

            var report = await _doctor.RunAsync(_instance, progress, cts.Token);
            cts.Token.ThrowIfCancellationRequested();

            _doctorReport = report;
            _doctorReportInstanceId = _instance.Id;
            RenderDoctorReport();
            DoctorProgress.Value = 100;
            DoctorProgressPercent.Text = "100%";
            DoctorProgressText.Text = "Готово.";

            if (fixFirst)
            {
                ShowToast(
                    "Автоисправление",
                    $"Исправлено пунктов: {fixedCount}. {report.Summary}",
                    fixedCount > 0 ? "#7CD964" : "#F5C542");
            }
            else
            {
                ShowToast(
                    "Диагностика завершена",
                    report.Summary,
                    report.Errors > 0 ? "#E5533D" : report.Warnings > 0 ? "#F5C542" : "#7CD964");
            }
        }
        catch (OperationCanceledException)
        {
            DoctorStatusText.Text = "Проверка отменена.";
            DoctorProgressText.Text = "Отменено.";
        }
        catch (Exception ex)
        {
            DoctorStatusText.Text = "Ошибка: " + ex.Message;
            DoctorProgressText.Text = "Ошибка.";
            ShowToast("Диагностика", ex.Message, "#E5533D");
        }
        finally
        {
            if (ReferenceEquals(_doctorCts, cts))
            {
                _doctorCts = null;
            }

            cts.Dispose();
            SetDoctorBusy(false);
        }
    }

    private void RenderDoctorReport()
    {
        var report = _doctorReport;
        if (report is null)
        {
            ResetDoctorReport();
            return;
        }

        _doctorRows.Clear();
        foreach (var check in report.Checks)
        {
            _doctorRows.Add(new DoctorCheckRow(check));
        }

        DoctorEmptyText.Visibility = report.Checks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        DoctorSummaryText.Text =
            $"Ошибок: {report.Errors} · Предупреждений: {report.Warnings} · Проверок: {report.Checks.Count}";
        DoctorDurationText.Text = "время выполнения: " + FormatDoctorDuration(report.Duration);
        DoctorCountText.Text =
            $"{report.Checks.Count} {Plural(report.Checks.Count, "ПРОВЕРКА", "ПРОВЕРКИ", "ПРОВЕРОК")}";
        DoctorStatusText.Text = report.Summary;
        DoctorSummaryAccent.Background = report.Errors > 0
            ? PixelPalette.Red
            : report.Warnings > 0
                ? PixelPalette.Gold
                : PixelPalette.GreenHi;
        UpdateDoctorActions();
    }

    private static string FormatDoctorDuration(TimeSpan duration) =>
        duration.TotalSeconds < 1
            ? duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " мс"
            : duration.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " с";

    private void OnDoctorProgress(LauncherProgress progress)
    {
        if (!_doctorBusy)
        {
            return;
        }

        var percent = progress.Percent ?? 0;
        DoctorProgress.Value = percent;
        DoctorProgressPercent.Text = progress.Percent is null ? "" : $"{percent:0}%";
        DoctorProgressText.Text = progress.Message;
        DoctorStatusText.Text = progress.Message;
    }

    private void SetDoctorBusy(bool busy)
    {
        _doctorBusy = busy;
        DoctorCheckList.IsEnabled = !busy;
        UpdateDoctorActions();
    }

    private void UpdateDoctorActions()
    {
        var canFix = _doctorReport is not null &&
                     _doctorReport.Checks.Any(check =>
                         check.CanAutoFix &&
                         check.Status is DoctorStatus.Error or DoctorStatus.Warning);

        DoctorRunButton.IsEnabled = !_doctorBusy;
        DoctorFixButton.IsEnabled = !_doctorBusy && canFix;
        DoctorExportButton.IsEnabled = !_doctorBusy;
    }

    private async void DoctorRun_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunDoctorAsync(fixFirst: false);
        }
        catch (Exception ex)
        {
            DoctorStatusText.Text = "Ошибка: " + ex.Message;
            ShowToast("Диагностика", ex.Message, "#E5533D");
        }
    }

    private async void DoctorFix_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RunDoctorAsync(fixFirst: true);
        }
        catch (Exception ex)
        {
            DoctorStatusText.Text = "Ошибка: " + ex.Message;
            ShowToast("Диагностика", ex.Message, "#E5533D");
        }
    }

    private async void DoctorExport_Click(object sender, RoutedEventArgs e)
    {
        if (_doctorBusy)
        {
            return;
        }

        var report = _doctorReport;
        if (report is null)
        {
            DoctorStatusText.Text = "Сначала выполни проверку — отчёта пока нет.";
            ShowToast("Экспорт JSON", "Сначала выполни проверку.", "#F5C542");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Экспорт диагностики",
            Filter = "JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"doctor-{SanitizeFileName(_instance.Name)}.json"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(dialog.FileName, report.ToJson());
            DoctorStatusText.Text = "Отчёт сохранён: " + dialog.FileName;
            ShowToast("Отчёт сохранён", Path.GetFileName(dialog.FileName), "#58C7F0");
        }
        catch (Exception ex)
        {
            DoctorStatusText.Text = "Экспорт: " + ex.Message;
            ShowToast("Экспорт JSON", ex.Message, "#E5533D");
        }
    }

    // ================= НАСТРОЙКИ =================

    private void PopulateSettingsFields()
    {
        _settingsLoading = true;

        NicknameBox.Text = _instance.Username;
        SettingsAvatar.Username = _instance.Username;
        MemorySlider.Value = Math.Clamp(_instance.MemoryMb, 1024, 16384);
        MemoryValueText.Text = $"{_instance.MemoryMb / 1024.0:0.#} ГБ";
        JavaPathBox.Text = string.IsNullOrWhiteSpace(_instance.JavaPath) ? "auto" : _instance.JavaPath;
        GameDirBox.Text = _instance.GameDir;
        PackUrlBox.Text = _instance.PackUrl;
        BackupRetentionBox.Text = _config.BackupRetention.ToString(CultureInfo.InvariantCulture);
        LauncherUpdateUrlBox.Text = _config.LauncherUpdateUrl;
        ParticlesToggle.IsChecked = _config.ParticlesEnabled;
        CloseOnLaunchToggle.IsChecked = _config.CloseOnLaunch;

        ThemeDay.IsChecked = _config.Theme.Equals("day", StringComparison.OrdinalIgnoreCase);
        ThemeNight.IsChecked = _config.Theme.Equals("night", StringComparison.OrdinalIgnoreCase);
        ThemeAuto.IsChecked = _config.Theme.Equals("auto", StringComparison.OrdinalIgnoreCase);

        PopulateWorldBackupFields();
        UpdateLauncherUpdateStatus();
        _settingsLoading = false;
    }

    private void PopulateWorldBackupFields()
    {
        _worldBackupOptions = _backupScheduler.LoadOptions();
        AutoBackupToggle.IsChecked = _worldBackupOptions.Enabled;
        AutoBackupIntervalBox.Text =
            _worldBackupOptions.IntervalHours.ToString(CultureInfo.InvariantCulture);
        AutoBackupRetentionBox.Text =
            _worldBackupOptions.Retention.ToString(CultureInfo.InvariantCulture);
        AutoBackupDirBox.Text = _worldBackupOptions.DestinationDirectory ?? "";
        UpdateAutoBackupStatus(_worldBackupOptions);
    }

    private void UpdateAutoBackupStatus(WorldBackupOptions options)
    {
        if (AutoBackupStatusText is null)
        {
            return;
        }

        var state = options.Enabled ? "включены" : "выключены";
        var lastRun = options.LastRunUtc is { } last
            ? $"последний запуск: {last.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "запусков ещё не было";
        var destination = string.IsNullOrWhiteSpace(options.DestinationDirectory)
            ? _worlds.BackupsRoot
            : options.DestinationDirectory!;

        AutoBackupStatusText.Text = $"Автобэкапы {state} · {lastRun}\n{destination}";
        AutoBackupStatusText.ToolTip = destination;
    }

    private WorldBackupOptions BuildWorldBackupOptionsFromUi()
    {
        var interval = ParseBoundedInt(
            AutoBackupIntervalBox.Text,
            WorldBackupOptions.DefaultIntervalHours,
            1,
            24 * 365);
        var retention = ParseBoundedInt(
            AutoBackupRetentionBox.Text,
            WorldBackupOptions.DefaultRetention,
            1,
            1000);

        var destination = (AutoBackupDirBox.Text ?? "").Trim();

        return new WorldBackupOptions
        {
            Enabled = AutoBackupToggle.IsChecked == true,
            IntervalHours = interval,
            Retention = retention,
            DestinationDirectory = string.IsNullOrWhiteSpace(destination)
                ? null
                : Path.GetFullPath(Environment.ExpandEnvironmentVariables(destination)),
            LastRunUtc = _worldBackupOptions.LastRunUtc
        };
    }

    private static int ParseBoundedInt(string? text, int fallback, int min, int max)
    {
        return int.TryParse((text ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, min, max)
            : fallback;
    }

    private void BrowseAutoBackupDir_Click(object sender, RoutedEventArgs e)
    {
        var current = (AutoBackupDirBox.Text ?? "").Trim();
        var initial = Directory.Exists(current) ? current : _worlds.BackupsRoot;

        var dialog = new OpenFolderDialog
        {
            Title = "Папка автобэкапов миров",
            InitialDirectory = Directory.Exists(initial) ? initial : ""
        };

        if (dialog.ShowDialog(this) == true)
        {
            AutoBackupDirBox.Text = dialog.FolderName;
        }
    }

    private async void AutoBackupNow_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var options = BuildWorldBackupOptionsFromUi();
            _backupScheduler.SaveOptions(options);
            _worldBackupOptions = options;

            var createdCount = 0;
            var success = false;

            await RunBusyAsync(
                "АВТОБЭКАП МИРОВ",
                "Подготовка автобэкапа...",
                async ct =>
                {
                    var created = await _backupScheduler.RunNowAsync(
                        _instance,
                        options,
                        new Progress<LauncherProgress>(OnProgress),
                        ct);

                    createdCount = created.Count;
                    success = true;
                });

            PopulateWorldBackupFields();
            await RefreshWorldsAsync(force: true);

            if (success)
            {
                ShowToast(
                    "Автобэкап выполнен",
                    createdCount == 0
                        ? "Валидных миров не найдено."
                        : $"Создано архивов: {createdCount}",
                    createdCount == 0 ? "#F5C542" : "#7CD964");
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or LauncherException)
        {
            ShowError("Автобэкап миров", ex.Message);
        }
    }

    private async Task RunStartupAutoBackupAsync()
    {
        try
        {
            var options = WorldBackupOptions.LoadOrDefault(_paths);
            if (!options.Enabled)
            {
                return;
            }

            var instance = _instance;
            var created = await Task.Run(() => _backupScheduler.RunIfDueAsync(instance, options));

            var saved = WorldBackupOptions.LoadOrDefault(_paths);
            _worldBackupOptions = saved;
            if (AutoBackupToggle is not null)
            {
                AutoBackupToggle.IsChecked = saved.Enabled;
                UpdateAutoBackupStatus(saved);
            }

            if (created.Count > 0)
            {
                await RefreshWorldsAsync(force: true);
                ShowToast(
                    "Автобэкап миров",
                    $"Создано архивов: {created.Count}",
                    "#7CD964");
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or LauncherException)
        {
            Debug.WriteLine("[МИРЫ] автобэкап при старте: " + ex.Message);
            Dispatcher.Invoke(() =>
            {
                if (AutoBackupStatusText is not null)
                {
                    AutoBackupStatusText.Text = "Автобэкап не выполнен: " + ex.Message;
                }
            });
        }
    }

    private void NicknameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_settingsLoading && NicknameBox.Text.Length > 0)
        {
            SettingsAvatar.Username = NicknameBox.Text.Trim();
        }
    }

    private void MemorySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (MemoryValueText is not null)
        {
            MemoryValueText.Text = $"{e.NewValue / 1024.0:0.#} ГБ";
        }
    }

    private void BrowseGameDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Папка игры (gameDir)",
            InitialDirectory = Directory.Exists(_instance.GameDir)
                ? _instance.GameDir
                : Path.GetDirectoryName(_instance.GameDir) ?? ""
        };

        if (dialog.ShowDialog(this) == true)
        {
            GameDirBox.Text = dialog.FolderName;
        }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_configLoadFailed)
            {
                ShowError("Настройки", "Конфиг не был загружен. Изменения не сохраняются.");
                return;
            }

            var nick = NicknameBox.Text.Trim();
            if (nick.Length < 3)
            {
                throw new LauncherException("Ник должен содержать минимум 3 символа.");
            }

            if (nick.Length > 16)
            {
                throw new LauncherException("Ник должен быть не длиннее 16 символов.");
            }

            if (!int.TryParse(BackupRetentionBox.Text.Trim(), out var retention))
            {
                retention = 5;
            }

            var gameDir = GameDirBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(gameDir))
            {
                throw new LauncherException("Укажи папку игры.");
            }

            gameDir = Path.GetFullPath(Environment.ExpandEnvironmentVariables(gameDir));

            var packUrl = PackUrlBox.Text.Trim();
            var previousJavaKey = JavaCacheKey(_instance);
            var previousJavaPath = _instance.JavaPath;
            var previousGameDir = _instance.GameDir;
            var previousPackUrl = _instance.PackUrl;

            _instance.Username = nick;
            _instance.Uuid = OfflineUuid.FromUsername(nick);
            _instance.MemoryMb = (int)Math.Round(MemorySlider.Value / 512) * 512;
            _instance.MinMemoryMb = Math.Min(2048, Math.Max(1024, _instance.MemoryMb - 1024));
            _instance.JavaPath = string.IsNullOrWhiteSpace(JavaPathBox.Text) ? "auto" : JavaPathBox.Text.Trim();
            _instance.GameDir = gameDir;
            _instance.PackUrl = packUrl;

            var javaPathChanged = !string.Equals(
                previousJavaPath, _instance.JavaPath, StringComparison.OrdinalIgnoreCase);
            var gameDirChanged = !string.Equals(
                previousGameDir, gameDir, StringComparison.OrdinalIgnoreCase);
            var packChanged = !string.Equals(
                previousPackUrl, packUrl, StringComparison.OrdinalIgnoreCase);

            if (javaPathChanged || gameDirChanged)
            {
                _javaInfoCache.Remove(previousJavaKey);
                _javaInfoCache.Remove(JavaCacheKey(_instance));
                _javaInfo = null;
            }

            if (gameDirChanged)
            {
                _modsDirty = true;
                _modsLoading = false;
                _modsFingerprint = null;
                _modsCountHint = null;
                _modsScanVersion++;
                _modAnalysis = null;
                _allMods.Clear();
                _modRows.Clear();
                _modUpdates.Clear();
                _modUpdateRows.Clear();
                NavModsCount.Text = "…";
                _worldsDirty = true;
                _worldsScanVersion++;
                _worldRows.Clear();
                _logsScanVersion++;
                _logsCts?.Cancel();
                _ = UpdateModsCountHintAsync();
            }

            if (packChanged && !gameDirChanged)
            {
                _modsDirty = true;
                _modsLoading = false;
                _modsFingerprint = null;
                _modsScanVersion++;
                _modUpdates.Clear();
                _modUpdateRows.Clear();
                _ = UpdateModsCountHintAsync();
            }

            _config.BackupRetention = Math.Clamp(retention, 1, 50);
            _config.LauncherUpdateUrl = LauncherUpdateUrlBox.Text.Trim();
            _config.Theme = ThemeNight.IsChecked == true
                ? "night"
                : ThemeDay.IsChecked == true
                    ? "day"
                    : "auto";
            _config.ParticlesEnabled = ParticlesToggle.IsChecked == true;
            _config.CloseOnLaunch = CloseOnLaunchToggle.IsChecked == true;
            _config.LastInstanceId = _instance.Id;

            var backupOptions = BuildWorldBackupOptionsFromUi();
            _backupScheduler.SaveOptions(backupOptions);
            _worldBackupOptions = backupOptions;

            CreatePackwizService();
            _store.Save(_config);
            ApplyTheme();

            if (javaPathChanged || gameDirChanged)
            {
                _ = EnsureJavaInfoAsync();
            }

            UpdateDashboardText();
            PopulateSettingsFields();
            PageScroll.ScrollToTop();
            ShowToast("Настройки сохранены", "Профиль и сборка обновлены.", "#7CD964");
        }
        catch (Exception ex)
        {
            ShowError("Настройки", ex.Message);
        }
    }

    private void CheckLauncherUpdate_Click(object sender, RoutedEventArgs e)
    {
        var url = LauncherUpdateUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            ShowError("Обновление лаунчера", "Укажи URL latest.json.");
            return;
        }

        _ = RunBusyAsync("ПРОВЕРКА ОБНОВЛЕНИЯ", "Запрос latest.json...", async ct =>
        {
            var info = await _updates.CheckAsync(url, AppVersion, ct);
            _pendingUpdate = info;
            UpdateHomeUpdateButton();

            if (info is null)
            {
                LauncherUpdateStatus.Text = "Обновлений нет. Установлена последняя версия.";
                InstallUpdateButton.IsEnabled = false;
                ShowToast("Обновлений нет", $"Версия {AppVersion} актуальна.", "#58C7F0");
                return;
            }

            LauncherUpdateStatus.Text = $"Доступно обновление {info.Version}. {info.Notes}".Trim();
            InstallUpdateButton.IsEnabled = true;
            TitleChannelText.Text = $"Основной канал · доступно v{info.Version}";
            ShowToast("Доступно обновление", $"Launcher {info.Version}", "#F5C542");
        });
    }

    private void InstallLauncherUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is null)
        {
            ShowError("Обновление лаунчера", "Сначала проверь обновления.");
            return;
        }

        var pending = _pendingUpdate;
        _ = RunBusyAsync("УСТАНОВКА ОБНОВЛЕНИЯ", "Скачивание...", async ct =>
        {
            var path = await _updates.DownloadAsync(
                pending.Manifest,
                new Progress<LauncherProgress>(OnProgress),
                ct);

            _updates.ApplyAndRestart(path, restart: true);
            Application.Current.Shutdown();
        });
    }

    private async Task CheckUpdatesSilentlyAsync()
    {
        var url = _config.LauncherUpdateUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            await Task.Delay(1600);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var info = await _updates.CheckAsync(url, AppVersion, timeout.Token);
            _pendingUpdate = info;
            if (info is null)
            {
                UpdateLauncherUpdateStatus();
                return;
            }

            _pendingUpdate = info;
            UpdateHomeUpdateButton();
            TitleChannelText.Text = $"Основной канал · доступно v{info.Version}";
            LauncherUpdateStatus.Text = $"Доступно обновление {info.Version}. {info.Notes}".Trim();
            InstallUpdateButton.IsEnabled = true;
            ShowToast("Доступно обновление", $"Launcher {info.Version}", "#F5C542");
        }
        catch (Exception)
        {
            UpdateLauncherUpdateStatus();
        }
    }

    private void UpdateLauncherUpdateStatus()
    {
        UpdateHomeUpdateButton();
        if (_pendingUpdate is not null)
        {
            LauncherUpdateStatus.Text = $"Доступно обновление {_pendingUpdate.Version}. {_pendingUpdate.Notes}".Trim();
            InstallUpdateButton.IsEnabled = true;
            return;
        }

        LauncherUpdateStatus.Text = string.IsNullOrWhiteSpace(_config.LauncherUpdateUrl)
            ? "URL не задан. Автообновление выключено."
            : "Проверка не выполнялась.";
        InstallUpdateButton.IsEnabled = false;
    }

    private void OpenLauncherFolder_Click(object sender, RoutedEventArgs e) => OpenPath(AppContext.BaseDirectory);

    private void UpdateHomeUpdateButton()
    {
        if (_homeUpdateButton is null) return;
        _homeUpdateButton.Visibility = _pendingUpdate is null ? Visibility.Collapsed : Visibility.Visible;
        _homeUpdateButton.ToolTip = _pendingUpdate is null
            ? "Обновлений нет"
            : $"Скачать и установить Launcher {_pendingUpdate.Version}";
    }

    // ================= СИСТЕМА =================

    private JavaInfo? ProbeJava(bool force = false, InstanceProfile? instance = null)
    {
        var target = instance ?? _instance;
        var key = JavaCacheKey(target);

        if (!force && _javaInfoCache.TryGetValue(key, out var cached))
        {
            if (string.Equals(target.Id, _instance.Id, StringComparison.OrdinalIgnoreCase))
            {
                _javaInfo = cached;
            }

            return cached;
        }

        var info = ProbeJavaCore(target);
        _javaInfoCache[key] = info;

        if (string.Equals(target.Id, _instance.Id, StringComparison.OrdinalIgnoreCase))
        {
            _javaInfo = info;
        }

        return info;
    }

    private JavaInfo? ProbeJavaCore(InstanceProfile instance)
    {
        try
        {
            if (GameLauncher.IsLegacyForgeProfile(instance))
            {
                // Старый Forge 1.20.1 47.x по-прежнему требует Java 17+.
                return _javaLocator.Find(instance);
            }

            var requiredMajor = VanillaLaunchCommandBuilder.GetRequiredJavaMajor(instance);
            return _javaLocator.Find(instance, requiredMajor, allowNewer: false);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private async Task<JavaInfo?> EnsureJavaInfoAsync(bool force = false)
    {
        var instance = _instance;
        var key = JavaCacheKey(instance);

        if (!force && _javaInfoCache.TryGetValue(key, out var cached))
        {
            _javaInfo = cached;
            return cached;
        }

        if (!force &&
            _javaProbeTask is not null &&
            string.Equals(_javaProbeKey, key, StringComparison.OrdinalIgnoreCase))
        {
            var shared = await _javaProbeTask;
            if (IsCurrentInstance(instance))
            {
                _javaInfoCache[key] = shared;
                _javaInfo = shared;
            }

            return shared;
        }

        var task = Task.Run(() => ProbeJavaCore(instance));
        _javaProbeKey = key;
        _javaProbeTask = task;

        try
        {
            var info = await task;
            if (!IsCurrentInstance(instance) ||
                !string.Equals(key, JavaCacheKey(_instance), StringComparison.OrdinalIgnoreCase))
            {
                return info;
            }

            _javaInfoCache[key] = info;
            _javaInfo = info;
            UpdateDashboardText();
            return info;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[JAVA] " + ex.Message);
            return null;
        }
        finally
        {
            if (ReferenceEquals(_javaProbeTask, task))
            {
                _javaProbeTask = null;
                _javaProbeKey = null;
            }
        }
    }

    private void InvalidateInstanceData()
    {
        _modsDirty = true;
        _modsLoading = false;
        _modsFingerprint = null;
        _modsCountHint = null;
        _modsScanVersion++;
        _modAnalysis = null;
        _allMods.Clear();
        _modRows.Clear();
        _modUpdates.Clear();
        _modUpdateRows.Clear();
        NavModsCount.Text = "…";
        ModsList.IsEnabled = true; ModsList.Visibility = Visibility.Visible;
        _worldsDirty = true;
        _worldsScanVersion++;
        _worldRows.Clear();
        _logsScanVersion++;
        _logsCts?.Cancel();
    }

    private void TrySaveConfig()
    {
        if (_configLoadFailed)
        {
            return;
        }

        try
        {
            if (_instance is not null)
            {
                _config.LastInstanceId = _instance.Id;
            }

            _store.Save(_config);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or LauncherException)
        {
            Debug.WriteLine("[СИСТЕМА] не удалось сохранить конфиг: " + ex.Message);
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_hostStopping)
        {
            // Остановка уже идёт: не закрываем окно, пока сервер не сохранит миры.
            e.Cancel = true;
            return;
        }

        if (_game is { IsRunning: true })
        {
            var result = MessageBox.Show(
                this,
                "Игра запущена. Закрыть лаунчер?\n\nИгра продолжит работать отдельно.",
                "Launcher",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        if (_hostProcess is { IsRunning: true } && !_closingAfterServerStop)
        {
            var result = MessageBox.Show(
                this,
                "Сервер запущен. Остановить сервер и закрыть лаунчер?",
                "Launcher — Мой сервер",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }

            e.Cancel = true;
            _ = StopServerAndCloseAsync();
            return;
        }

        _operationCts?.Cancel();
        _catalogRequest?.Cancel();
        _serversCts?.Cancel();
        _doctorCts?.Cancel();
        _logsCts?.Cancel();
        _hostCts?.Cancel();
        TrySaveConfig();
    }

    private async Task StopServerAndCloseAsync()
    {
        try
        {
            await StopHostServerAsync(silent: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[СЕРВЕР] остановка при закрытии: " + ex.Message);
        }
        finally
        {
            _closingAfterServerStop = true;
            Close();
        }
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _serversCts?.Cancel();
        _serversCts?.Dispose();
        _serversCts = null;
        _doctorCts?.Cancel();
        _doctorCts?.Dispose();
        _doctorCts = null;
        _logsCts?.Cancel();
        _logsCts?.Dispose();
        _logsCts = null;
        StopHostTimers();
        _hostCts?.Cancel();
        _hostCts?.Dispose();
        _hostCts = null;
        _hostProcess?.Dispose();
        _hostProcess = null;
        _modFilterTimer?.Stop();
        _modFilterTimer = null;
        _logService.Dispose();

        if (_game is { IsRunning: false })
        {
            _game.Dispose();
        }
    }

    private static bool IsRemotePack(string packUrl) =>
        packUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        packUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string? TryFindLocalPack(string gameDir)
    {
        try
        {
            var candidate = Path.GetFullPath(Path.Combine(gameDir, "..", "packwiz", "pack.toml"));
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long GetDirectorySize(string directory)
    {
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // Файл занят — пропускаем.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Размер не критичен.
        }

        return total;
    }

    private static string FormatDate(DateTime value)
    {
        var local = value.Date;
        var today = DateTime.Today;

        if (local == today)
        {
            return "сегодня " + value.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        if (local == today.AddDays(-1))
        {
            return "вчера " + value.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        return value.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(result) ? "world" : result;
    }

    private static string Trim(string value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max)
        {
            return value;
        }

        return value[..max] + "…";
    }

    private static string Plural(int count, string one, string few, string many)
    {
        var mod100 = Math.Abs(count) % 100;
        var mod10 = mod100 % 10;

        if (mod100 is >= 11 and <= 14)
        {
            return many;
        }

        return mod10 switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось открыть:\n" + path + "\n\n" + ex.Message,
                "Launcher",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void ShowError(string title, string message)
    {
        ShowDarkMessage(title, message, confirm: false);
    }

    private async void ShowToast(string title, string message, string accent = "#5FBE4A")
    {
        var border = new Border
        {
            Background = Brush("#F21B2027"),
            BorderBrush = Brush(accent),
            BorderThickness = new Thickness(2),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 8),
            MaxWidth = 360,
            HorizontalAlignment = HorizontalAlignment.Right,
            Opacity = 0
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(),
            FontFamily = new FontFamily("Bahnschrift"),
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = Brush(accent)
        });

        stack.Children.Add(new TextBlock
        {
            Text = message,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            Foreground = Brush("#E9EDF2"),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 120,
            Margin = new Thickness(0, 3, 0, 0)
        });

        border.Child = stack;
        ToastHost.Children.Insert(0, border);

        border.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        await Task.Delay(5200);
        border.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)));
        await Task.Delay(240);
        ToastHost.Children.Remove(border);
    }

    private static Brush Brush(string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private sealed record ModsSnapshot(
        string Fingerprint,
        IReadOnlyList<ModEntry> Entries);

    private sealed record CachedServerPing(
        DateTimeOffset CachedAt,
        ServerPingResult Result);

    private sealed record LogSnapshot(
        IReadOnlyList<LogLine> Lines,
        string Path,
        bool Exists,
        CrashSummary? Crash);
}



