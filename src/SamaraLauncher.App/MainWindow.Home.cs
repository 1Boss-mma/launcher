using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using SamaraLauncher.Core.Minecraft;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.App;

public partial class MainWindow
{
    private bool _homeError;
    private bool _quickBuildOpen;
    private int _quickBuildTransition;
    private MinecraftVersionCatalog? _versionCatalog;
    private IReadOnlyList<MinecraftCatalogVersion> _catalogVersions = Array.Empty<MinecraftCatalogVersion>();
    private MinecraftCatalogVersion? _pendingVersion;
    private string? _pendingInstallProfileId;
    private bool _catalogLoading;
    private CancellationTokenSource? _catalogCts;
    private string? _catalogError;
    private string _pendingLoader = MinecraftLoader.Vanilla;

    private static readonly string[] LoaderOrder =
    {
        MinecraftLoader.Vanilla,
        MinecraftLoader.Fabric,
        MinecraftLoader.OptiFine,
        MinecraftLoader.Forge
    };

    private MinecraftVersionCatalog VersionCatalog =>
        _versionCatalog ??= new MinecraftVersionCatalog(_paths);

    private void HomeBuild_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _game is { IsRunning: true }) return;
        SetQuickBuildOpen(!_quickBuildOpen);
    }

    private void SetQuickBuildOpen(bool open)
    {
        if (_quickBuildOpen == open) return;
        _quickBuildOpen = open;
        int transition = ++_quickBuildTransition;
        double from = HomeBuildPopup.IsOpen ? HomeBuildPopupSurface.Opacity : 0d;
        HomeBuildPopupSurface.BeginAnimation(UIElement.OpacityProperty, null);
        HomeBuildPopupSurface.Opacity = from;
        HomeBuildArrow.Text = open ? "▴" : "▾";
        HomeBuildButton.Tag = open ? "Open" : null;
        HomeBuildPopupSurface.IsHitTestVisible = open;
        if (open)
        {
            VersionCatalogList.SelectedItem = null;
            _ = RefreshCatalogAsync();
            PopulateQuickBuilds();
            HomeBuildPopup.IsOpen = true;
        }

        var animation = new DoubleAnimation(from, open ? 1d : 0d, TimeSpan.FromMilliseconds(120));
        animation.Completed += (_, _) =>
        {
            if (transition != _quickBuildTransition) return;
            HomeBuildPopupSurface.BeginAnimation(UIElement.OpacityProperty, null);
            HomeBuildPopupSurface.Opacity = open ? 1d : 0d;
            if (!open) HomeBuildPopup.IsOpen = false;
        };
        HomeBuildPopupSurface.BeginAnimation(UIElement.OpacityProperty, animation);
        if (open)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!_quickBuildOpen) return;
                VersionCatalogList.Focus();
            });
        }
    }

    private async Task RefreshCatalogAsync()
    {
        _catalogCts?.Cancel();
        var cts = new CancellationTokenSource();
        _catalogCts = cts;
        _catalogLoading = true;

        try
        {
            var versions = await VersionCatalog.GetVersionsAsync(forceRefresh: false, cts.Token);
            if (cts.IsCancellationRequested) return;
            _catalogVersions = versions;
            _catalogError = null;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            Debug.WriteLine("[КАТАЛОГ ВЕРСИЙ] " + ex.Message);
            _catalogError = _catalogVersions.Count == 0
                ? "Не удалось загрузить каталог. Проверьте подключение."
                : "Не удалось обновить каталог.";
        }
        finally
        {
            if (ReferenceEquals(_catalogCts, cts))
            {
                _catalogCts = null;
                _catalogLoading = false;
                if (_quickBuildOpen && HomeBuildPopup.IsOpen) PopulateQuickBuilds();
            }

            cts.Dispose();
        }
    }

    private void PopulateQuickBuilds()
    {
        var focusedCatalog = VersionCatalogList.IsKeyboardFocusWithin;
        var selectedKey = (VersionCatalogList.SelectedItem as MinecraftVersionRow)?.Key;

        IReadOnlyList<InstanceProfile> profiles;
        try
        {
            profiles = _instances.List(_config);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[СБОРКИ] не удалось получить список: " + ex.Message);
            profiles = Array.Empty<InstanceProfile>();
        }

        var profilesByKey = new Dictionary<string, InstanceProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            var baseVersion = profile.BaseMinecraftVersion?.Trim() ?? "";
            if (baseVersion.Length == 0) continue;

            var key = profile.LoaderKey + "|" + baseVersion;
            if (!profilesByKey.TryGetValue(key, out var existing))
            {
                profilesByKey[key] = profile;
                continue;
            }

            if (IsActiveProfile(existing)) continue;
            if (IsActiveProfile(profile))
            {
                profilesByKey[key] = profile;
                continue;
            }

            if (!IsVersionInstalled(existing) && IsVersionInstalled(profile))
            {
                profilesByKey[key] = profile;
            }
        }

        var rows = new List<MinecraftVersionRow>(_catalogVersions.Count * LoaderOrder.Length + profiles.Count);
        var usedProfileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var version in _catalogVersions)
        {
            if (!version.IsRelease) continue;

            foreach (var loader in LoaderOrder)
            {
                profilesByKey.TryGetValue(loader + "|" + version.Id, out var profile);

                var installed = profile is not null && IsVersionInstalled(profile);
                var isPending = _pendingVersion is not null &&
                                string.Equals(_pendingLoader, loader, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(_pendingVersion.Id, version.Id, StringComparison.OrdinalIgnoreCase);
                var isActive = profile is not null &&
                               _pendingVersion is null &&
                               IsActiveProfile(profile);

                rows.Add(new MinecraftVersionRow(
                    version,
                    profile,
                    loader,
                    installed && !string.IsNullOrWhiteSpace(profile!.Name) ? profile.Name :
                    string.Equals(loader, MinecraftLoader.Vanilla, StringComparison.OrdinalIgnoreCase)
                        ? version.Id
                        : $"{version.Id} {loader}",
                    installed ? FormatInstalledMeta(loader, profile!) : "не установлена",
                    isActive || isPending ? "✓" : "",
                    installed));

                if (profile is not null) usedProfileIds.Add(profile.Id);
            }
        }

        foreach (var profile in profiles)
        {
            if (usedProfileIds.Contains(profile.Id)) continue;

            var baseVersion = profile.BaseMinecraftVersion?.Trim() ?? "";
            var title = profile.Name?.Trim() ?? "";
            if (title.Length == 0)
            {
                title = baseVersion.Length > 0 ? $"{baseVersion} {profile.LoaderKey}" : "Сборка";
            }

            var installed = IsVersionInstalled(profile);
            var meta = FormatInstanceVersion(profile);
            if (!installed) meta += " · не установлена";

            var isActive = _pendingVersion is null && IsActiveProfile(profile);

            rows.Add(new MinecraftVersionRow(
                null,
                profile,
                profile.LoaderKey,
                title,
                meta,
                isActive ? "✓" : "",
                installed,
                isExtra: true));
        }

        rows = rows.OrderByDescending(row => row.InstalledProfile is not null && IsActiveProfile(row.InstalledProfile))
            .ThenByDescending(row => row.IsInstalled)
            .ToList();
        var firstAvailable = rows.FindIndex(row => !row.IsInstalled);
        if (firstAvailable > 0) rows[firstAvailable].StartsAvailableGroup = true;
        VersionCatalogList.ItemsSource = rows;
        VersionCatalogList.SelectedItem = selectedKey is null
            ? null
            : rows.FirstOrDefault(row => string.Equals(row.Key, selectedKey, StringComparison.OrdinalIgnoreCase));
        UpdateCatalogStatus(rows.Count);

        if (_quickBuildOpen && focusedCatalog)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_quickBuildOpen) VersionCatalogList.Focus();
            });
        }
    }

    private bool IsActiveProfile(InstanceProfile profile) =>
        string.Equals(profile.Id, _instance.Id, StringComparison.OrdinalIgnoreCase);

    private static string FormatInstalledMeta(string loader, InstanceProfile profile)
    {
        switch (MinecraftLoader.Normalize(loader))
        {
            case MinecraftLoader.Forge:
            {
                var forgeVersion = profile.ForgeVersion?.Trim() ?? "";
                return forgeVersion.Length == 0 ? "Forge" : $"Forge {forgeVersion}";
            }
            case MinecraftLoader.Fabric:
            {
                var fabricVersion = profile.FabricVersion?.Trim() ?? "";
                return fabricVersion.Length == 0 ? "Fabric" : $"Fabric {fabricVersion}";
            }
            case MinecraftLoader.OptiFine:
                return "OptiFine";
            default:
                return "установлена";
        }
    }

    private void UpdateCatalogStatus(int visibleRows)
    {
        string? text = null;
        var isError = false;

        if (_catalogLoading && _catalogVersions.Count == 0)
        {
            text = "Загрузка каталога…";
        }
        else if (!_catalogLoading && _catalogError is not null && _catalogVersions.Count == 0)
        {
            text = _catalogError;
            isError = true;
        }
        else if (!_catalogLoading && visibleRows == 0)
        {
            text = "Список версий пуст";
        }
        else if (!_catalogLoading && visibleRows > 0 &&
                 (_catalogError is not null || VersionCatalog.LastRefreshUsedStaleCache))
        {
            text = "Каталог из кэша";
        }

        if (string.IsNullOrEmpty(text))
        {
            CatalogStatusText.Text = "";
            CatalogStatusText.Visibility = Visibility.Collapsed;
            return;
        }

        CatalogStatusText.Text = text;
        CatalogStatusText.Foreground = (Brush)FindResource(isError ? "RedBrush" : "MutedBrush");
        CatalogStatusText.Visibility = Visibility.Visible;
    }

    private void VersionCatalogItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem item || item.DataContext is not MinecraftVersionRow row) return;
        if (_busy || _game is { IsRunning: true }) return;

        e.Handled = true;
        ActivateCatalogRow(row);
    }

    private void ActivateCatalogRow(MinecraftVersionRow row)
    {
        if (_busy || _game is { IsRunning: true }) return;

        if (row.IsAdd)
        {
            SetQuickBuildOpen(false);
            OpenInstancesOverlay();
            return;
        }

        if (row.InstalledProfile is { } profile && (row.IsInstalled || row.IsExtra))
        {
            if (!SwitchActiveInstance(profile)) return;

            _pendingVersion = null;
            _pendingLoader = MinecraftLoader.Vanilla;
            if (string.Equals(profile.Id, _pendingInstallProfileId, StringComparison.OrdinalIgnoreCase))
            {
                _pendingInstallProfileId = null;
            }

            _homeError = false;
            HomeOperationStrip.Visibility = Visibility.Collapsed;
            SetQuickBuildOpen(false);
            UpdateDashboardText();
            return;
        }

        if (row.Version is { } version)
        {
            SelectPendingVersion(version, row.Loader);
        }
    }

    private void SelectPendingVersion(MinecraftCatalogVersion version, string loader)
    {
        if (_busy || _game is { IsRunning: true }) return;

        loader = MinecraftLoader.Normalize(loader);
        if (loader.Length == 0) loader = MinecraftLoader.Vanilla;

        _pendingVersion = version;
        _pendingLoader = loader;
        _homeError = false;
        HomeOperationStrip.Visibility = Visibility.Collapsed;
        SetQuickBuildOpen(false);
        ApplyPendingSelection();
    }

    private void ApplyPendingSelection()
    {
        if (_pendingVersion is not { } version) return;

        SelectedInstanceName.Text = version.Id.ToUpperInvariant();
        SelectedInstanceName.ToolTip = version.Id;
        SelectedInstanceMeta.Text =
            $"Minecraft {version.Id} · {MinecraftLoader.Label(_pendingLoader)} · не установлена";
        UpdatePlayButton();
    }

    private async Task InstallPendingVersionAsync()
    {
        if (_pendingVersion is not { } version) return;
        if (_busy || _game is { IsRunning: true } || _configLoadFailed) return;

        var loader = MinecraftLoader.Normalize(_pendingLoader);
        if (loader.Length == 0) loader = MinecraftLoader.Vanilla;
        var loaderLabel = MinecraftLoader.Label(loader);

        var installed = FindInstalledProfile(version.Id, loader);
        if (installed is not null)
        {
            if (SwitchActiveInstance(installed))
            {
                _pendingVersion = null;
                _pendingLoader = MinecraftLoader.Vanilla;
                _pendingInstallProfileId = null;
                _homeError = false;
                HomeOperationStrip.Visibility = Visibility.Collapsed;
                SetQuickBuildOpen(false);
                UpdateDashboardText();
            }
            return;
        }

        string? optifineJar = null;
        if (string.Equals(loader, MinecraftLoader.OptiFine, StringComparison.OrdinalIgnoreCase))
        {
            var dialog = new OpenFileDialog
            {
                Title = "Выберите OptiFine .jar",
                Filter = "OptiFine (*.jar)|*.jar|Все файлы (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            optifineJar = dialog.FileName;
        }

        _busy = true;
        _operationCts = new CancellationTokenSource();
        var ct = _operationCts.Token;
        SetBusy(true);
        ResetProgress();
        ShowBoot($"УСТАНОВКА MINECRAFT {version.Id} · {loaderLabel}", "Подготовка…");
        StatusText.Text = $"Установка Minecraft {version.Id} · {loaderLabel}…";

        try
        {
            var profileName = string.Equals(loader, MinecraftLoader.Vanilla, StringComparison.OrdinalIgnoreCase)
                ? version.Id
                : $"{version.Id} {loaderLabel}";

            var created = FindReusableProfile(version.Id, loader) ?? _instances.Create(_config, profileName);
            _pendingInstallProfileId = created.Id;
            created.Loader = loader;
            created.MinecraftVersion = version.Id;
            created.VersionId = version.Id;
            created.ForgeVersion = "";
            created.FabricVersion = "";
            created.OptiFineVersion = "";
            created.JavaPath = "auto";
            created.Username = _instance.Username;
            TrySaveConfig();

            var progress = new Progress<LauncherProgress>(OnProgress);

            switch (loader)
            {
                case MinecraftLoader.Forge:
                    await _installer.EnsureForgeInstalledAsync(created, version.Id, null, progress, ct);
                    break;
                case MinecraftLoader.Fabric:
                    await _installer.EnsureFabricInstalledAsync(created, version.Id, progress, ct);
                    break;
                case MinecraftLoader.OptiFine:
                    await _installer.EnsureOptiFineInstalledAsync(created, version.Id, optifineJar!, progress, ct);
                    break;
                default:
                    await _installer.EnsureVanillaInstalledAsync(created, version.Id, progress, ct);
                    break;
            }

            _pendingVersion = null;
            _pendingLoader = MinecraftLoader.Vanilla;
            _pendingInstallProfileId = null;
            TrySaveConfig();

            var switched = SwitchActiveInstance(created);
            StatusText.Text = switched
                ? $"Версия установлена · {loaderLabel}"
                : "Версия установлена, но не удалось выбрать сборку";
            ShowToast("Версия установлена", $"Minecraft {version.Id} · {loaderLabel}", "#7CD964");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Установка отменена";
            ShowToast("Установка отменена", $"Minecraft {version.Id} · {loaderLabel}", "#F5C542");
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

    private InstanceProfile? FindReusableProfile(string versionId, string loader)
    {
        var profiles = _instances.List(_config);

        if (!string.IsNullOrWhiteSpace(_pendingInstallProfileId))
        {
            foreach (var instance in profiles)
            {
                if (!string.Equals(instance.Id, _pendingInstallProfileId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(instance.LoaderKey, loader, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(instance.BaseMinecraftVersion, versionId, StringComparison.OrdinalIgnoreCase)) continue;
                return instance;
            }
        }

        foreach (var instance in profiles)
        {
            if (!string.Equals(instance.LoaderKey, loader, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(instance.BaseMinecraftVersion, versionId, StringComparison.OrdinalIgnoreCase)) continue;
            if (IsVersionInstalled(instance)) continue;
            return instance;
        }

        return null;
    }

    private InstanceProfile? FindInstalledProfile(string versionId, string loader)
    {
        foreach (var instance in _instances.List(_config))
        {
            if (!string.Equals(instance.LoaderKey, loader, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.Equals(instance.BaseMinecraftVersion, versionId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!IsVersionInstalled(instance)) continue;
            return instance;
        }

        return null;
    }

    private static bool IsVersionInstalled(InstanceProfile instance)
    {
        if (!instance.IsInstalled) return false;

        var versionId = EffectiveVersionId(instance);
        if (versionId.Length == 0 || !VersionJsonExists(instance, versionId)) return false;

        // Не считаем профиль установленным, если установщик успел создать только
        // ванильные файлы, но не заполнил поля выбранного загрузчика.
        if (instance.IsFabric)
        {
            return !string.IsNullOrWhiteSpace(instance.FabricVersion) ||
                   versionId.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase);
        }

        if (instance.IsOptiFine)
        {
            return !string.IsNullOrWhiteSpace(instance.OptiFineVersion) ||
                   versionId.Contains("optifine", StringComparison.OrdinalIgnoreCase);
        }

        if (instance.IsForge)
        {
            return !string.IsNullOrWhiteSpace(instance.ForgeVersion) ||
                   versionId.Contains("-forge-", StringComparison.OrdinalIgnoreCase);
        }

        return true;
    }

    private static string EffectiveVersionId(InstanceProfile instance) =>
        !string.IsNullOrWhiteSpace(instance.VersionId)
            ? instance.VersionId.Trim()
            : instance.MinecraftVersion?.Trim() ?? "";

    private static bool VersionJsonExists(InstanceProfile instance, string versionId)
    {
        if (string.IsNullOrWhiteSpace(instance.GameDir) || string.IsNullOrWhiteSpace(versionId)) return false;

        try
        {
            return File.Exists(new VersionPaths(instance.GameDir).VersionJson(versionId));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private void HomeWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_quickBuildOpen) return;
        if (HomeBuildPopupSurface.IsMouseOver) return;
        DependencyObject? element = e.OriginalSource as DependencyObject;
        while (element != null)
        {
            if (element == HomeBuildButton || element == HomeBuildPopupSurface) return;
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        SetQuickBuildOpen(false);
    }

    private void HomeBuildPopup_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SetQuickBuildOpen(false);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && VersionCatalogList.IsKeyboardFocusWithin)
        {
            if (VersionCatalogList.SelectedItem is MinecraftVersionRow row)
            {
                ActivateCatalogRow(row);
                e.Handled = true;
            }
            return;
        }

        if (e.OriginalSource is not TextBox &&
            e.Key is Key.Down or Key.Up &&
            !VersionCatalogList.IsKeyboardFocusWithin)
        {
            (Keyboard.FocusedElement as UIElement)?.MoveFocus(
                new TraversalRequest(e.Key == Key.Down ? FocusNavigationDirection.Next : FocusNavigationDirection.Previous));
            e.Handled = true;
        }
    }

    private void HomeBuildPopup_Closed(object? sender, EventArgs e)
    {
        _quickBuildOpen = false;
        HomeBuildPopupSurface.IsHitTestVisible = false;
        HomeBuildArrow.Text = "▾";
        HomeBuildButton.Tag = null;
        if (IsActive) HomeBuildButton.Focus();
    }

    private void HomeLogs_Click(object sender, RoutedEventArgs e)
    {
        NavLogs.IsChecked = true;
        ShowPage("logs");
    }

    private void HomeOpenFolder_Click(object sender, RoutedEventArgs e) => OpenPath(_instance.GameDir);
}

/// <summary>Строка плоского списка версий и загрузчиков в выпадающем меню на главной.</summary>
public sealed class MinecraftVersionRow
{
    public MinecraftVersionRow(
        MinecraftCatalogVersion? version,
        InstanceProfile? installedProfile,
        string loader,
        string title,
        string meta,
        string checkMark,
        bool isInstalled,
        bool isExtra = false,
        bool isAdd = false)
    {
        Version = version;
        InstalledProfile = installedProfile;
        Loader = loader;
        Title = title;
        Meta = meta;
        CheckMark = checkMark;
        IsInstalled = isInstalled;
        IsExtra = isExtra;
        IsAdd = isAdd;
    }

    public MinecraftCatalogVersion? Version { get; }

    public InstanceProfile? InstalledProfile { get; }

    public string Loader { get; }

    public string Title { get; }

    public string Meta { get; }

    public string CheckMark { get; }

    public bool IsInstalled { get; }

    public bool StartsAvailableGroup { get; set; }

    public bool IsExtra { get; }

    public bool IsAdd { get; }

    public string Key => IsAdd ? "add" : IsExtra
        ? "profile:" + (InstalledProfile?.Id ?? "")
        : $"{Loader}|{Version?.Id}";
}

