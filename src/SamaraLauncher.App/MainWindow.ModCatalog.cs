using System.Diagnostics;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SamaraLauncher.App.ViewModels;
using SamaraLauncher.Core.Mods;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.ServerHost;

namespace SamaraLauncher.App;

public partial class MainWindow
{
    private readonly ModrinthClient _catalogClient = new();
    private CancellationTokenSource? _catalogRequest;
    private int _catalogOffset;
    private int _catalogTotal;
    private readonly ObservableCollection<ModrinthSearchHit> _catalogHits = new();
    private bool _selectCreatedFromMods;
    private bool _categoriesLoaded;
    private bool _categoriesLoading;
    private bool _updatingCatalogInstance;
    private sealed record CatalogBuildChoice(string Name, InstanceProfile? Instance, bool IsCreate = false);

    private static void FadeContentIn(UIElement element)
    {
        if (!element.IsVisible) return;
        var fade = new DoubleAnimation(0.72, 1, TimeSpan.FromMilliseconds(160))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
        Timeline.SetDesiredFrameRate(fade, 60);
        element.BeginAnimation(OpacityProperty, fade);
    }

    private static void PrepareDarkDialog(Window dialog)
    {
        dialog.WindowStyle = WindowStyle.None;
        dialog.ResizeMode = ResizeMode.NoResize;
        dialog.AllowsTransparency = true;
        dialog.Background = Brushes.Transparent;
        dialog.ShowInTaskbar = false;
        if (dialog.Content is UIElement content)
        {
            dialog.Content = null;
            dialog.Content = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(20, 25, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(82, 99, 110)),
                BorderThickness = new Thickness(2), Child = content
            };
        }
        dialog.Opacity = 0;
        dialog.Loaded += (_, _) =>
        {
            var animation = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop };
            Timeline.SetDesiredFrameRate(animation, 60);
            dialog.Opacity = 1;
            dialog.BeginAnimation(OpacityProperty, animation);
        };
    }

    private bool ShowDarkMessage(string title, string message, bool confirm)
    {
        var dialog = new Window { Owner = this, Width = 520, Height = 310, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Foreground = Brushes.White, Title = title };
        var layout = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); layout.Children.Add(heading);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var accept = new Button { Content = confirm ? "Продолжить" : "Понятно", Padding = new Thickness(16, 6, 16, 6),
            Background = new SolidColorBrush(Color.FromRgb(32, 44, 53)), Foreground = Brushes.White,
            Style = (Style)FindResource("HomeSelectorButton"), IsDefault = true };
        accept.Click += (_, _) => dialog.DialogResult = true;
        actions.Children.Add(accept);
        if (confirm)
        {
            var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0),
                Background = new SolidColorBrush(Color.FromRgb(32, 44, 53)), Foreground = Brushes.White,
                Style = (Style)FindResource("HomeSelectorButton"), IsCancel = true };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            actions.Children.Add(cancel);
        }
        DockPanel.SetDock(actions, Dock.Bottom); layout.Children.Add(actions);
        layout.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = new TextBlock { Text = message, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap } });
        dialog.Content = layout;
        PrepareDarkDialog(dialog);
        return dialog.ShowDialog() == true;
    }
    private bool _modWarningsDismissed;
    private bool _updatingModSide;
    private string _modsSide = "client";

    private InstanceProfile GetModsTarget()
    {
        if (_modsSide != "server") return _instance;
        if (!string.IsNullOrWhiteSpace(_hostProfile.InstanceId) &&
            !string.Equals(_hostProfile.InstanceId, _instance.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Настроенный сервер относится к другой сборке.");
        var directory = ServerHostService.GetServerDirectory(_hostProfile);
        if (!Directory.Exists(directory)) throw new InvalidOperationException("Папка сервера не найдена. Проверьте страницу «Мой сервер».");
        return new InstanceProfile { Id = "server:" + _instance.Id, Name = _hostProfile.Name,
            GameDir = directory, MinecraftVersion = GetCatalogMinecraft(_instance), VersionId = _instance.VersionId, ForgeVersion = _instance.ForgeVersion };
    }

    private bool IsCurrentModsTarget(InstanceProfile instance)
    {
        try
        {
            var current = GetModsTarget();
            return string.Equals(current.Id, instance.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(current.GameDir, instance.GameDir, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    private void ModsSide_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingModSide || !IsLoaded || sender is not ComboBox { SelectedItem: ComboBoxItem item }) return;
        var side = item.Tag?.ToString() == "server" ? "server" : "client";
        if (_busy || _hostBusy) { ShowError("Моды", "Дождитесь завершения текущей операции."); SyncModsSide(); return; }
        var oldSide = _modsSide;
        _modsSide = side;
        try { _ = GetModsTarget(); }
        catch (Exception ex) { _modsSide = oldSide; SyncModsSide(); ShowError("Моды сервера", ex.Message); return; }
        SyncModsSide();
        if (oldSide == side) return;
        _modsScanVersion++;
        _modsLoading = false;
        _modsDirty = true;
        _modsFingerprint = null;
        _modAnalysis = null;
        _allMods.Clear(); _modRows.Clear(); _modUpdates.Clear(); _modUpdateRows.Clear();
        _ = RefreshModsAsync(force: true);
        if (CatalogTab?.IsChecked == true) ScheduleCatalogSearch();
    }

    private void SyncModsSide()
    {
        _updatingModSide = true;
        try
        {
            foreach (var selector in new[] { InstalledSideFilter }.Where(selector => selector is not null))
                selector.SelectedIndex = _modsSide == "server" ? 1 : 0;
        }
        finally { _updatingModSide = false; }
    }

    private void DismissModAnalysis_Click(object sender, RoutedEventArgs e)
    {
        _modWarningsDismissed = true;
        ModAnalysisBanner.Visibility = Visibility.Collapsed;
    }

    private void UpdateCatalogInstanceSelector()
    {
        if (_instances is null || _config is null) return;
        _updatingCatalogInstance = true;
        try
        {
            var instances = _instances.List(_config);
            var active = instances.FirstOrDefault(item => string.Equals(item.Id, _instance.Id, StringComparison.OrdinalIgnoreCase));
            var choices = new List<CatalogBuildChoice>();
            foreach (var item in instances)
            {
                choices.Add(new CatalogBuildChoice(item.Name, item));
                if (ReferenceEquals(item, active)) choices.Add(new CatalogBuildChoice("+ Создать сборку…", null, true));
            }
            if (active is null) choices.Add(new CatalogBuildChoice("+ Создать сборку…", null, true));
            foreach (var selector in new[] { CatalogInstanceSelector, InstalledInstanceSelector }.Where(selector => selector is not null))
            {
                if (selector.ItemsSource is not IReadOnlyList<CatalogBuildChoice> current || !current.SequenceEqual(choices))
                    selector.ItemsSource = choices;
                var activeChoice = (selector.ItemsSource as IReadOnlyList<CatalogBuildChoice>)?.FirstOrDefault(choice => ReferenceEquals(choice.Instance, active));
                if (!ReferenceEquals(selector.SelectedItem, activeChoice)) selector.SelectedItem = activeChoice;
            }
        }
        finally { _updatingCatalogInstance = false; }
    }

    private void CatalogInstance_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingCatalogInstance || sender is not ComboBox { SelectedItem: CatalogBuildChoice choice }) return;
        if (choice.IsCreate)
        {
            UpdateCatalogInstanceSelector();
            if (_busy || _game is { IsRunning: true }) { ShowError("Создание сборки", "Закройте игру и дождитесь завершения текущей операции."); return; }
            OpenInstancesOverlay();
            _selectCreatedFromMods = true;
            ShowCreateForm();
            return;
        }
        if (choice.Instance is not { } selected || string.Equals(selected.Id, _instance.Id, StringComparison.OrdinalIgnoreCase)) return;
        if (_busy || _game is { IsRunning: true })
        {
            ShowError("Выбор сборки", "Закройте игру и дождитесь завершения текущей операции.");
            UpdateCatalogInstanceSelector();
            return;
        }
        if (!SwitchActiveInstance(selected)) { UpdateCatalogInstanceSelector(); return; }
        try { _ = GetModsTarget(); }
        catch (Exception) { _modsSide = "client"; SyncModsSide(); if (CatalogTab?.IsChecked == true) ScheduleCatalogSearch(); }
        _ = EnsureModsLoadedAsync();
    }

    private static IReadOnlyDictionary<string, CatalogModRecord> ReadVerifiedCatalogRecords(
        InstanceProfile instance, IReadOnlyList<ModEntry> entries)
    {
        var verified = new Dictionary<string, CatalogModRecord>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var path = System.IO.Path.Combine(instance.GameDir, ".samara-mods.json");
            if (!System.IO.File.Exists(path)) return verified;
            var records = System.Text.Json.JsonSerializer.Deserialize<List<CatalogModRecord>>(System.IO.File.ReadAllText(path)) ?? new();
            foreach (var entry in entries)
            {
                var name = entry.FileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? entry.FileName[..^".disabled".Length] : entry.FileName;
                var matches = records.Where(record => record is not null && record.Source == "modrinth" &&
                    string.Equals(record.FileName, name, StringComparison.OrdinalIgnoreCase) && record.Sha512?.Length == 128).ToList();
                if (matches.Count == 0) continue;
                using var stream = System.IO.File.OpenRead(entry.FullPath);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA512.HashData(stream));
                var match = matches.FirstOrDefault(record => string.Equals(record.Sha512, hash, StringComparison.OrdinalIgnoreCase));
                if (match is not null) verified[entry.FileName] = match;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException) { }
        return verified;
    }

    private void ModsCreateInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _game is { IsRunning: true }) return;
        OpenInstancesOverlay();
        _selectCreatedFromMods = true;
        ShowCreateForm();
    }

    private void ModsImportInstance_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _game is { IsRunning: true }) return;
        OpenInstancesOverlay();
        InstanceImport_Click(sender, e);
    }

    private void ModsDragOver(object sender, DragEventArgs e)
    {
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        e.Effects = files is { Length: > 0 } && files.All(path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            && (_modsSide == "server" ? _hostProcess is not { IsRunning: true } && !_hostBusy : _game is not { IsRunning: true })
            && !_busy ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ModsDrop(object sender, DragEventArgs e)
    {
        if (!CanChangeMods() || _busy || e.Data.GetData(DataFormats.FileDrop) is not string[] files ||
            files.Length == 0 || files.Any(path => !path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))) return;
        try
        {
            _mods.Import(GetModsTarget(), files);
            _modUpdates.Clear();
            await RefreshModsAsync(force: true);
            ShowToast("Моды добавлены", $"Импортировано: {files.Length}", "#7CD964");
        }
        catch (Exception ex) { ShowError("Импорт модов", ex.Message); }
        e.Handled = true;
    }

    private void CatalogTab_Checked(object sender, RoutedEventArgs e)
    {
        if (CatalogView is null || InstalledModsView is null || CatalogTab is null) return;
        var showCatalog = CatalogTab.IsChecked == true;
        CatalogContainer.Visibility = showCatalog ? Visibility.Visible : Visibility.Collapsed;
        InstalledModsView.Visibility = showCatalog ? Visibility.Collapsed : Visibility.Visible;
        if (IsLoaded) FadeContentIn(showCatalog ? CatalogContainer : InstalledModsView);
        _catalogRequest?.Cancel();
        if (showCatalog) { UpdateCatalogInstanceSelector(); _ = EnsureCatalogCategoriesAsync(); UpdateCatalogKind(); }
        else if (IsLoaded) _ = RefreshResourcesAsync(force: true);
    }

    private async Task EnsureCatalogCategoriesAsync()
    {
        if (_categoriesLoaded || _categoriesLoading || CatalogCategoryFilter is null) return;
        _categoriesLoading = true;
        try
        {
            var categories = await _catalogClient.GetModCategoriesAsync();
            foreach (var category in categories)
                CatalogCategoryFilter.Items.Add(new ComboBoxItem { Content = CategoryLabel(category.Name), Tag = category.Name });
            _categoriesLoaded = true;
        }
        catch (Exception ex) { CatalogCategoryFilter.ToolTip = "Не удалось загрузить категории: " + ex.Message; }
        finally { _categoriesLoading = false; }
    }

    private static string CategoryLabel(string name) => name switch
    {
        "adventure" => "Приключения", "cursed" => "Необычное", "decoration" => "Декор",
        "economy" => "Экономика", "equipment" => "Снаряжение", "food" => "Еда",
        "game-mechanics" => "Игровые механики", "library" => "Библиотеки", "magic" => "Магия",
        "management" => "Управление", "minigame" => "Мини-игры", "mobs" => "Мобы",
        "optimization" => "Оптимизация", "social" => "Общение", "storage" => "Хранение",
        "technology" => "Технологии", "transportation" => "Транспорт", "utility" => "Утилиты",
        "worldgen" => "Генерация мира", _ => name
    };

    private void CatalogSearch_TextChanged(object sender, TextChangedEventArgs e) => ScheduleCatalogSearch();

    private void ModFind_Click(object sender, RoutedEventArgs e) => ApplyModFilter();

    private void InstalledFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) ApplyModFilter();
    }

    private void ModsBulkMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.IsOpen = true;
        }
    }

    private async void ModsBulkEnable_Click(object sender, RoutedEventArgs e) => await SetSelectedModsEnabledAsync(true);
    private async void ModsBulkDisable_Click(object sender, RoutedEventArgs e) => await SetSelectedModsEnabledAsync(false);

    private async Task SetSelectedModsEnabledAsync(bool enabled)
    {
        if (!CanChangeMods() || _busy) return;
        var selected = ModsList.SelectedItems.OfType<ModRow>().ToList();
        if (selected.Count == 0) { ShowError("Моды", "Выберите моды в списке."); return; }
        try
        {
            foreach (var row in selected) _mods.SetEnabled(row.Entry, enabled);
        }
        catch (Exception ex) { ShowError("Моды", ex.Message); }
        finally { _modUpdates.Clear(); await RefreshModsAsync(force: true); }
    }

    private async void ModsBulkDelete_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeMods() || _busy) return;
        var selected = ModsList.SelectedItems.OfType<ModRow>().ToList();
        if (selected.Count == 0) { ShowError("Моды", "Выберите моды в списке."); return; }
        var selectedNames = selected.Select(row => row.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids = _modAnalysis?.Mods.Where(mod => selectedNames.Contains(mod.FileName))
            .Select(mod => mod.ModId).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependents = _modAnalysis?.Mods.Where(mod => mod.Enabled && !selectedNames.Contains(mod.FileName) &&
            mod.Dependencies.Any(d => d.Mandatory && ids.Contains(d.ModId)))
            .Select(mod => string.IsNullOrWhiteSpace(mod.Name) ? mod.FileName : mod.Name).Distinct().ToList() ?? new List<string>();
        var warning = dependents.Count == 0 ? "" : "\n\nОт выбранных модов зависят:\n" + string.Join("\n", dependents.Select(name => "• " + name));
        if (!ShowDarkMessage("Удаление модов", $"Переместить выбранные моды в резервную папку: {selected.Count}?{warning}", confirm: true) || !CanChangeMods()) return;
        try { foreach (var row in selected) _mods.Delete(row.Entry); }
        catch (Exception ex) { ShowError("Удаление модов", ex.Message); }
        finally { _modUpdates.Clear(); await RefreshModsAsync(force: true); }
    }
    private void CatalogSort_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) UpdateCatalogKind(); }
    private void CatalogRefresh_Click(object sender, RoutedEventArgs e) => UpdateCatalogKind();

    private void ScheduleCatalogSearch()
    {
        if (_busy || CatalogTab?.IsChecked != true || (CatalogKindFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "mod" || CatalogSearch is null || CatalogSort is null || CatalogStatus is null) return;
        _catalogOffset = 0;
        _ = LoadCatalogAsync(true);
    }

    private async Task LoadCatalogAsync(bool debounce, bool append = false)
    {
        _catalogRequest?.Cancel();
        var request = new CancellationTokenSource();
        _catalogRequest = request;
        var instance = _instance;
        var instanceId = instance.Id;
        var version = GetCatalogMinecraft(instance);
        var loader = GetCatalogLoader(instance);
        var category = (CatalogCategoryFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
        var environment = "";
        var query = CatalogSearch.Text;
        var sort = (CatalogSort.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "relevance";
        var offset = append ? _catalogHits.Count : 0;
        _catalogOffset = offset;
        if (!append)
        {
            _catalogHits.Clear();
            _catalogTotal = 0;
            CatalogList.ItemsSource = _catalogHits;
        }
        CatalogStatus.Text = append ? "Загрузка следующих модов…" : "Загрузка каталога…";
        try
        {
            if (string.IsNullOrWhiteSpace(version))
            {
                CatalogStatus.Text = "Не удалось определить версию Minecraft. Проверьте настройки сборки.";
                return;
            }
            if (string.IsNullOrWhiteSpace(loader))
            {
                CatalogStatus.Text = $"{instance.Name} · Minecraft {version}: для модов нужна сборка с Forge, NeoForge или Fabric. Выберите другую сборку или создайте новую с загрузчиком.";
                return;
            }
            if (debounce) await Task.Delay(350, request.Token);
            var result = await _catalogClient.SearchModsAsync(query, version, loader, sort, offset, category, environment, request.Token);
            if (request.IsCancellationRequested || !ReferenceEquals(request, _catalogRequest) ||
                !string.Equals(instanceId, _instance.Id, StringComparison.Ordinal)) return;
            _catalogTotal = result.TotalHits;
            foreach (var hit in result.Hits)
            {
                var installed = _allMods.FirstOrDefault(row => string.Equals(row.CatalogRecord?.ProjectId, hit.Id, StringComparison.OrdinalIgnoreCase));
                hit.InstallationState = installed is null ? "" : installed.HasUpdate ? "Есть обновление" : "Установлен";
            }
            foreach (var hit in result.Hits) _catalogHits.Add(hit);
            if (!append) FadeContentIn(CatalogList);
            CatalogStatus.Text = $"{instance.Name} · поиск: Minecraft {(version.Length == 0 ? "все" : version)} · {(loader.Length == 0 ? "все загрузчики" : loader)} · " +
                (result.TotalHits == 0 ? "Ничего не найдено" : $"Показано {_catalogHits.Count} из {result.TotalHits}") +
                (GetCatalogLoader(instance).Length == 0 ? " · Для установки нужен совместимый загрузчик." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(request, _catalogRequest) && !request.IsCancellationRequested)
            {
                if (!append) _catalogHits.Clear();
                CatalogStatus.Text = (append ? "Не удалось загрузить следующие моды. Прокрутите вниз для повтора: " : "Каталог недоступен: ") + ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(request, _catalogRequest)) _catalogRequest = null;
            request.Dispose();
        }
    }

    private void CatalogScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_busy || CatalogTab?.IsChecked != true || _catalogRequest is not null || _catalogHits.Count == 0 ||
            _catalogHits.Count >= _catalogTotal || e.OriginalSource is not ScrollViewer viewer ||
            viewer.ScrollableHeight - viewer.VerticalOffset > 120) return;
        _ = LoadCatalogAsync(false, append: true);
    }

    private void CatalogOpen_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is not ModrinthSearchHit hit || string.IsNullOrWhiteSpace(hit.Slug)) return;
        Process.Start(new ProcessStartInfo("https://modrinth.com/mod/" + Uri.EscapeDataString(hit.Slug)) { UseShellExecute = true });
    }

    private async void CatalogDetails_Click(object sender, RoutedEventArgs e)
    {
        if (CatalogList.SelectedItem is ModrinthSearchHit hit) await ShowCatalogDetailsAsync(hit);
    }

    private async void CatalogRowInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ModrinthSearchHit hit })
        {
            if (hit.IsInstalling) _operationCts?.Cancel();
            else if (hit.IsInstalled) { e.Handled = true; return; }
            else
            {
                var instance = _instance;
                var side = (InstalledSideFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "client";
                await InstallCatalogVersionAsync(hit, null, instance, side, GetCatalogMinecraft(instance), GetCatalogLoader(instance), confirm: false);
            }
        }
        e.Handled = true;
    }

    private async void InstalledModVersions_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModRow row } || row.CatalogRecord is null)
        {
            ShowError("Моды", "Источник этого локального файла не подтверждён.");
            return;
        }
        await ShowCatalogDetailsAsync(new ModrinthSearchHit { Id = row.CatalogRecord.ProjectId, Slug = row.CatalogRecord.ProjectId, Title = row.DisplayName, Description = row.Subtitle }, _modsSide);
    }

    private void InstalledModPage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: ModRow row } || row.CatalogRecord is null)
        {
            ShowError("Моды", "Источник этого локального файла не подтверждён.");
            return;
        }
        Process.Start(new ProcessStartInfo("https://modrinth.com/mod/" + Uri.EscapeDataString(row.CatalogRecord.ProjectId)) { UseShellExecute = true });
    }

    private async void InstalledModRollback_Click(object sender, RoutedEventArgs e)
    {
        if (!CanChangeMods() || _busy) return;
        if (sender is not FrameworkElement { Tag: ModRow row } || row.CatalogRecord is null)
        {
            ShowError("Откат мода", "Для этого файла нет подтверждённой записи каталога.");
            return;
        }
        if (!ShowDarkMessage("Откат мода", $"Откатить последнее обновление «{row.DisplayName}»? Вместе с ним будут восстановлены компоненты, обновлённые в той же операции.", confirm: true)) return;
        var instance = GetModsTarget();
        var serverTarget = _modsSide == "server";
        var completed = false;
        if (serverTarget) SetHostBusy(true);
        try { await RunBusyAsync("ОТКАТ МОДА", "Проверка резервной копии…", async ct =>
        {
            if ((serverTarget ? _hostProcess is { IsRunning: true } : _game is { IsRunning: true }) || !IsCurrentModsTarget(instance))
                throw new InvalidOperationException("Сборка изменилась или игра запущена.");
            await new ModCatalogInstaller(_catalogClient).RollbackAsync(instance, row.CatalogRecord.ProjectId, ct);
            completed = true;
        }); }
        finally { if (serverTarget) SetHostBusy(false); }
        if (completed) { _modUpdates.Clear(); await RefreshModsAsync(force: true); ShowToast("Откат завершён", row.DisplayName, "#7CD964"); }
        else if (_homeError) ShowError("Откат мода", StatusText.Text);
    }

    private async Task ShowCatalogDetailsAsync(ModrinthSearchHit hit, string? targetSide = null)
    {
        if (_busy) return;
        var instance = _instance;
        var side = targetSide ?? (InstalledSideFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "client";
        IReadOnlyList<ModrinthVersion>? versions = null;
        ModrinthProject? project = null;
        await RunBusyAsync("ВЕРСИИ МОДА", "Загрузка версий…", async ct =>
        {
            versions = await _catalogClient.GetProjectVersionsAsync(hit.Id, ct);
            project = await _catalogClient.GetProjectAsync(hit.Id, ct);
        });
        if (versions is null || !ReferenceEquals(instance, _instance))
        {
            if (_homeError) ShowError("Каталог модов", StatusText.Text);
            return;
        }

        var loader = GetCatalogLoader(instance);
        var minecraft = GetCatalogMinecraft(instance);
        bool Compatible(ModrinthVersion v) => loader.Length > 0 &&
            v.GameVersions.Contains(minecraft, StringComparer.OrdinalIgnoreCase) &&
            v.Loaders.Contains(loader, StringComparer.OrdinalIgnoreCase);
        var ordered = versions.OrderByDescending(v => v.DatePublished).ToList();
        var dialog = new Window
        {
            Owner = this, Title = hit.Title, Width = 620, Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = new SolidColorBrush(Color.FromRgb(20, 25, 30)), Foreground = Brushes.White
        };
        foreach (var controlType in new[] { typeof(Button), typeof(ComboBox), typeof(TextBox), typeof(ListBox), typeof(ListBoxItem) })
            if (ModsView.TryFindResource(controlType) is Style style) dialog.Resources[controlType] = style;
        var root = new DockPanel { Margin = new Thickness(18) };
        var heading = new TextBlock { Text = $"{hit.Title} · {hit.Author} · Modrinth\n{hit.Description}\n\nMinecraft {minecraft} · {(loader.Length == 0 ? "Vanilla" : loader)} · {(side == "server" ? "сервер" : "клиент")}",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var page = new Button { Content = "Страница автора", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        page.Click += (_, _) => Process.Start(new ProcessStartInfo("https://modrinth.com/mod/" + Uri.EscapeDataString(hit.Slug)) { UseShellExecute = true });
        buttons.Children.Add(page);
        if (loader.Length == 0)
        {
            var setup = new Button { Content = "Настройки сборки", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
            setup.Click += (_, _) => { dialog.Close(); OpenInstancesOverlay(); };
            buttons.Children.Add(setup);
        }
        var description = new Button { Content = "Описание", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        description.Click += (_, _) =>
        {
            var descriptionWindow = new Window { Owner = dialog, Title = hit.Title, Width = 600, Height = 480,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = dialog.Background, Foreground = Brushes.White };
            var content = new StackPanel();
            content.Children.Add(new TextBlock { Text = string.IsNullOrWhiteSpace(project?.Body) ? hit.Description : project.Body,
                TextWrapping = TextWrapping.Wrap });
            foreach (var address in project?.Gallery ?? new List<string>())
            {
                if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                    !uri.Host.Equals("cdn.modrinth.com", StringComparison.OrdinalIgnoreCase)) continue;
                content.Children.Add(new Image { Source = new System.Windows.Media.Imaging.BitmapImage(uri),
                    Stretch = Stretch.Uniform, MaxHeight = 300, Margin = new Thickness(0, 12, 0, 0) });
            }
            var descriptionLayout = new DockPanel();
            var closeDescription = new Button { Content = "Закрыть", Margin = new Thickness(18, 0, 18, 18),
                Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Right,
                Style = (Style)FindResource("HomeSelectorButton"), Background = new SolidColorBrush(Color.FromRgb(32, 44, 53)), Foreground = Brushes.White };
            closeDescription.Click += (_, _) => descriptionWindow.Close();
            DockPanel.SetDock(closeDescription, Dock.Bottom); descriptionLayout.Children.Add(closeDescription);
            descriptionLayout.Children.Add(new ScrollViewer { Margin = new Thickness(18), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = content });
            descriptionWindow.Content = descriptionLayout;
            PrepareDarkDialog(descriptionWindow);
            descriptionWindow.ShowDialog();
        };
        var install = new Button { Content = "Установить", Padding = new Thickness(16, 6, 16, 6), IsEnabled = false };
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(8, 0, 0, 0) };
        buttons.Children.Add(description); buttons.Children.Add(install); buttons.Children.Add(cancel); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var list = new ListBox { DisplayMemberPath = nameof(CatalogVersionRow.Display) };
        var releaseFilter = new ComboBox { Margin = new Thickness(0, 0, 0, 8), MinWidth = 160, HorizontalAlignment = HorizontalAlignment.Left };
        releaseFilter.Items.Add("Все выпуски"); releaseFilter.Items.Add("release"); releaseFilter.Items.Add("beta"); releaseFilter.Items.Add("alpha");
        DockPanel.SetDock(releaseFilter, Dock.Top); root.Children.Add(releaseFilter);
        void PopulateVersions()
        {
            var kind = releaseFilter.SelectedIndex <= 0 ? "" : releaseFilter.SelectedItem?.ToString();
            var rows = new List<CatalogVersionRow>();
            foreach (var version in ordered.Where(v => string.IsNullOrEmpty(kind) || v.VersionType == kind))
            {
                var deps = version.Dependencies.Where(d => d.Type == "required" || d.Type == "optional")
                    .Select(d => $"{(d.Type == "required" ? "обязательная" : "необязательная")}: {d.ProjectId ?? d.VersionId ?? d.FileName ?? "неизвестно"}");
                rows.Add(new CatalogVersionRow(version, Compatible(version), $"{version.VersionNumber} · {version.VersionType} · {version.DatePublished:dd.MM.yyyy} · {(Compatible(version) ? "совместима" : "несовместима")}\nMinecraft: {string.Join(", ", version.GameVersions)} · {string.Join(", ", version.Loaders)}\n{string.Join("; ", deps)}"));
            }
            list.ItemsSource = rows;
            var stable = rows.FirstOrDefault(item => item.Version.VersionType == "release" && item.Compatible);
            list.SelectedItem = stable;
            install.IsEnabled = stable is not null;
        }
        list.SelectionChanged += (_, _) => install.IsEnabled = list.SelectedItem is CatalogVersionRow { Compatible: true };
        releaseFilter.SelectionChanged += (_, _) => PopulateVersions();
        releaseFilter.SelectedIndex = 0;
        install.Click += (_, _) => { if (list.SelectedItem is CatalogVersionRow { Compatible: true }) dialog.DialogResult = true; };
        cancel.Click += (_, _) => dialog.DialogResult = false;
        root.Children.Add(list); dialog.Content = root;
        PrepareDarkDialog(dialog);
        if (dialog.ShowDialog() != true || list.SelectedItem is not CatalogVersionRow { Compatible: true } selectedRow) return;
        await InstallCatalogVersionAsync(hit, selectedRow.Version.Id, instance, side, minecraft, loader, confirm: true);
    }

    private async Task InstallCatalogVersionAsync(ModrinthSearchHit hit, string? versionId, InstanceProfile instance,
        string side, string minecraft, string loader, bool confirm, bool reinstall = false)
    {
        if (_busy) return;
        if (!ReferenceEquals(instance, _instance)) return;
        InstanceProfile target = instance;
        if (side == "server")
        {
            if (_hostProcess is { IsRunning: true } || _hostBusy) { ShowError("Установка на сервер", "Остановите сервер и дождитесь завершения его операций."); return; }
            var inspection = _serverHost.Inspect(_hostProfile);
            if (!inspection.ForgeInstalled || inspection.InstalledForgeVersion != ServerHostService.ForgeVersion ||
                minecraft != ServerHostService.MinecraftVersion || loader != "forge" ||
                (!string.IsNullOrWhiteSpace(_hostProfile.InstanceId) && _hostProfile.InstanceId != instance.Id))
            { ShowError("Установка на сервер", "Выбранная сборка не совпадает с настроенным Forge-сервером. Проверьте настройки на странице «Мой сервер»."); return; }
            target = new InstanceProfile { Id = "server:" + _hostProfile.Id, Name = _hostProfile.Name,
                GameDir = ServerHostService.GetServerDirectory(_hostProfile), MinecraftVersion = minecraft, VersionId = instance.VersionId, ForgeVersion = instance.ForgeVersion };
        }
        else if (!CanChangeMods()) return;
        var installer = new ModCatalogInstaller(_catalogClient);
        var completed = false;
        var showInlineProgress = CatalogTab.IsChecked == true && _catalogHits.Contains(hit);
        var previousCatalogStatus = CatalogStatus.Text;
        if (showInlineProgress)
        {
            hit.InstallProgress = 0;
            hit.IsInstalling = true;
            SetCatalogFiltersEnabled(false);
            CatalogStatus.Text = "Проверка совместимости и зависимостей…";
        }
        if (side == "server") SetHostBusy(true);
        try { await RunBusyAsync("УСТАНОВКА МОДА", "Проверка зависимостей…", async ct =>
        {
            var plan = await installer.PrepareAsync(hit.Id, versionId, minecraft, loader, ct, side);
            if (confirm)
            {
                var names = string.Join("\n", plan.Select(v => $"• {v.Name} ({v.VersionNumber})"));
                if (!ShowDarkMessage("Установка модов", $"Установить {(side == "server" ? "на сервер" : "в сборку")} {target.Name}:\n\n{names}", confirm: true)) return;
            }
            if (!ReferenceEquals(instance, _instance) || (side == "server" ? _hostProcess is { IsRunning: true } : _game is { IsRunning: true }))
                throw new InvalidOperationException("Сборка изменилась или игра запущена.");
            var progress = new Progress<string>(message =>
            {
                StatusText.Text = message;
                BootStatusText.Text = message;
                if (showInlineProgress && hit.IsInstalling) CatalogStatus.Text = message;
            });
            var overallProgress = new Progress<double>(value => { if (hit.IsInstalling) hit.InstallProgress = value; });
            await installer.InstallAsync(target, plan, progress, ct, overallProgress, forceProjectId: reinstall ? hit.Id : null);
            hit.InstallProgress = 1;
            hit.InstallationState = "Установлен";
            completed = true;
        }); }
        finally
        {
            if (side == "server") SetHostBusy(false);
            if (showInlineProgress)
            {
                hit.IsInstalling = false;
                SetCatalogFiltersEnabled(true);
                CatalogStatus.Text = previousCatalogStatus;
            }
        }
        if (completed && ReferenceEquals(instance, _instance))
        {
            if (side == "client") { _modsDirty = true; await RefreshModsAsync(force: true); }
            else { _modsDirty = true; await RefreshModsAsync(force: true); await RefreshHostPageAsync(); }
            ShowToast(side == "server" ? "Мод установлен на сервер" : "Мод установлен", hit.Title, "#7CD964");
        }
        else if (_homeError) ShowError("Установка модов", StatusText.Text);
    }

    private static string GetCatalogLoader(InstanceProfile instance)
    {
        var configured = instance.Loader?.Trim() ?? "";
        if (configured.Equals("vanilla", StringComparison.OrdinalIgnoreCase)) return "";
        if (configured.Equals("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        if (configured.Equals("fabric", StringComparison.OrdinalIgnoreCase)) return "fabric";
        if (configured.Equals("forge", StringComparison.OrdinalIgnoreCase) || configured.Equals("optifine", StringComparison.OrdinalIgnoreCase)) return "forge";
        var versionId = instance.VersionId ?? "";
        if (versionId.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        if (versionId.Contains("fabric", StringComparison.OrdinalIgnoreCase)) return "fabric";
        if (versionId.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "forge";
        return string.IsNullOrWhiteSpace(versionId) && !string.IsNullOrWhiteSpace(instance.ForgeVersion) ? "forge" : "";
    }

    private void SetCatalogFiltersEnabled(bool enabled)
    {
        CatalogSearch.IsEnabled = enabled;
        CatalogSort.IsEnabled = enabled;
        CatalogCategoryFilter.IsEnabled = enabled;
        CatalogInstanceSelector.IsEnabled = enabled;

        CatalogTab.IsEnabled = enabled;
        InstalledTab.IsEnabled = enabled;
    }

    private static string GetCatalogMinecraft(InstanceProfile instance)
    {
        return instance.BaseMinecraftVersion;
    }

    private sealed record CatalogVersionRow(ModrinthVersion Version, bool Compatible, string Display);
}

