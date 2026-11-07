using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using SamaraLauncher.App.Controls;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.App;

// The workspace composes the existing operation views. The services and their
// target/profile checks remain the single implementation of each operation.
public partial class MainWindow
{
    private bool _workspaceReady;
    private string _workspaceSection = "home";
    private string _packTab = "Обзор";
    private readonly StackPanel _workspace = new();
    private readonly StackPanel _workspaceHeader = new();
    private readonly Dictionary<string, RadioButton> _workspaceNav = new();
    private readonly Dictionary<string, FrameworkElement> _settingsCards = new();
    private Action? _workspaceBack;
    private readonly TextBlock _workspaceTitle = new();

    private void InitializeWorkspace()
    {
        Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("Controls/BuildStyles.xaml", UriKind.Relative) });
        var scene = (Grid)PageScroll.Parent;
        var rail = (Border)((Grid)scene.Parent).Children[0];
        var navigation = new DockPanel { Margin = new Thickness(8, 16, 8, 16) };
        var bottom = new StackPanel();
        DockPanel.SetDock(bottom, Dock.Bottom);
        navigation.Children.Add(bottom);
        var top = new StackPanel();
        navigation.Children.Add(top);
        foreach (var (key, label, icon) in new[] {
            ("home", "Играть", PixelIconKind.Home), ("instances", "Мои сборки", PixelIconKind.Mods),
            ("worlds", "Миры", PixelIconKind.Worlds), ("servers", "Серверы", PixelIconKind.Server),
            ("workshop", "Мастерская", PixelIconKind.Folder),
            ("doctor", "Диагностика", PixelIconKind.Shield), ("settings", "Настройки", PixelIconKind.Settings) })
        {
            var radio = new RadioButton { GroupName = "WorkspaceNav", Style = (Style)FindResource("NavRadio"), MinHeight = 44 };
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            var glyph = new PixelIcon { Kind = icon, Width = 20, Height = 20 };
            glyph.SetResourceReference(PixelIcon.BrushProperty, "MutedBrush");
            line.Children.Add(glyph);
            line.Children.Add(new TextBlock { Text = label, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            radio.Content = line;
            AutomationProperties.SetName(radio, label);
            radio.Click += (_, _) => NavigateWorkspace(key);
            _workspaceNav[key] = radio;
            (key is "doctor" or "settings" ? bottom : top).Children.Add(radio);
        }
        rail.Child = navigation;
        rail.SetResourceReference(Border.BackgroundProperty, "BgBrush");
        scene.Children.Remove(PageScroll);
        var area = new Grid();
        area.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        area.RowDefinitions.Add(new RowDefinition());
        var heading = new DockPanel { Margin = new Thickness(24, 16, 24, 0) };
        var avatar = new PixelAvatar { Username = _instance.Username, Width = 32, Height = 32 };
        var profileContent = new StackPanel { Orientation = Orientation.Horizontal };
        profileContent.Children.Add(avatar);
        profileContent.Children.Add(new TextBlock { Text = "Профиль", Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        // PixelButton draws text itself; use a regular templated button for avatar content.
        var profileButton = new Button { Content = profileContent, Style = (Style)FindResource("HomeSelectorButton"), Padding = new Thickness(8, 4, 8, 4) };
        AutomationProperties.SetName(profileButton, "Открыть профиль");
        profileButton.Click += (_, _) => NavigateWorkspace("profile");
        DockPanel.SetDock(profileButton, Dock.Right); heading.Children.Add(profileButton);
        _workspaceTitle.FontFamily = new FontFamily("Bahnschrift"); _workspaceTitle.FontSize = 24;
        _workspaceTitle.FontWeight = FontWeights.SemiBold;
        _workspaceTitle.TextWrapping = TextWrapping.Wrap;
        _workspaceHeader.Children.Add(_workspaceTitle);
        heading.Children.Add(_workspaceHeader);
        area.Children.Add(heading);
        Grid.SetRow(PageScroll, 1); area.Children.Add(PageScroll); scene.Children.Add(area);
        var pages = (Grid)PageScroll.Content;
        pages.Margin = new Thickness(24, 16, 24, 24);
        pages.Children.Add(_workspace);
        // Move settings groups intact, retaining controls and validation handlers.
        foreach (var pair in new (string Key, FrameworkElement Card)[]
        {
            ("profile", ProfileSettingsCard), ("instance", InstanceSettingsCard),
            ("pack", PackUpdatesSettingsCard), ("backups", BackupsSettingsCard),
            ("updates", LauncherUpdatesSettingsCard), ("appearance", AppearanceSettingsCard),
            ("about", AboutSettingsCard)
        })
        {
            var card = pair.Card;
            ((Panel)card.Parent).Children.Remove(card);
            card.Margin = new Thickness(0, 0, 0, 12);
            _settingsCards[pair.Key] = card;
        }
        // Existing inline catalog selectors are retained as context-aware operation controls.
        ((FrameworkElement)CatalogTab.Parent).Visibility = Visibility.Collapsed;
        ((FrameworkElement)WorldMineTab.Parent).Visibility = Visibility.Collapsed;
        ((FrameworkElement)ResourcesInstalledMode.Parent).Visibility = Visibility.Collapsed;
        NormalizeWorkspaceView(WorldsView);
        NormalizeWorkspaceView(ServersView);
        NormalizeWorkspaceView(HostView);
        NormalizeWorkspaceView(DoctorView);
        NormalizeWorkspaceView(LogsView);
        NormalizeWorkspaceView(ResourcesView);
        HomeLogsButton.Content = "Разобрать ошибку";
        HomeView.ClearValue(MinHeightProperty);
        PageScroll.SizeChanged += (_, _) => HomeView.MinHeight = Math.Max(300, PageScroll.ActualHeight - 40);
        WorldCatalogHost.Margin = new Thickness(0);
        ResourceCatalogHost.Margin = new Thickness(0);
        _workspaceNav["home"].TabIndex = 0;
        _workspaceNav["instances"].TabIndex = 1;
        _workspaceNav["worlds"].TabIndex = 2;
        _workspaceNav["servers"].TabIndex = 3;
        _workspaceNav["workshop"].TabIndex = 4;
        _workspaceNav["doctor"].TabIndex = 5;
        _workspaceNav["settings"].TabIndex = 6;
        PreviewKeyDown += (_, e) => {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (ModDetailsOverlay.Visibility == Visibility.Visible) { ModDetailsClose_Click(this, new RoutedEventArgs()); e.Handled = true; return; }
            if (_workspaceBack is not null && InstancesOverlay.Visibility != Visibility.Visible) { var back = _workspaceBack; back(); e.Handled = true; }
        };
        ApplyReferenceLayout();
        _workspaceReady = true;
        NavigateWorkspace("home");
    }

    private static void NormalizeWorkspaceView(FrameworkElement view)
    {
        view.Margin = new Thickness(0);
        if (view is Grid grid && grid.Children.Count > 0 && grid.Children[0] is Grid header)
        {
            // Keep original toolbar actions; redundant large title is removed.
            if (header.Children.Count > 0 && header.Children[0] is StackPanel title) title.Visibility = Visibility.Collapsed;
            if (header.Children.Count > 1 && header.Children[1] is StackPanel actions) actions.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private PixelButton WorkspaceButton(string text, Action action, bool primary = false)
    {
        var button = new PixelButton { Content = text, Flat = true, Variant = primary ? PixelButtonVariant.Primary : PixelButtonVariant.Secondary,
            Height = 40, MinHeight = 36, Margin = new Thickness(0, 0, 8, 8), FontSize = 14 };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private TextBlock WorkspaceText(string text, bool heading = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = heading ? 18 : 14,
            FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal, Margin = new Thickness(0, 0, 0, 12) };
        block.SetResourceReference(TextBlock.ForegroundProperty, heading ? "TextBrush" : "MutedBrush");
        return block;
    }

    private void WorkspaceActions(params (string Label, Action Action)[] actions)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var (label, action) in actions) row.Children.Add(WorkspaceButton(label, action));
        _workspaceHeader.Children.Add(row);
    }

    private void WorkspaceTabs(string selected, params (string Label, Action Action)[] tabs)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var (label, action) in tabs)
        {
            var radio = new RadioButton { Content = label, GroupName = "WorkspaceTabs", Style = (Style)FindResource("FilterRadio"),
                IsChecked = label == selected, MinHeight = 36, Margin = new Thickness(0, 0, 8, 8) };
            radio.Click += (_, _) => action(); row.Children.Add(radio);
        }
        _workspaceHeader.Children.Add(row);
    }

    private void BeginWorkspace(string section, string title, string? legacy = null, Action? back = null)
    {
        if (ModDetailsOverlay.Visibility == Visibility.Visible)
        {
            ModDetailsOverlay.Visibility = Visibility.Collapsed;
            PageScroll.Visibility = Visibility.Visible;
            UpdatePanoramaPause();
        }
        RestoreBuildContent();
        PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        ((FrameworkElement)_workspaceHeader.Parent).Visibility = Visibility.Visible;
        _workspaceSection = section;
        _workspaceBack = back;
        SetQuickBuildOpen(false);
        foreach (var card in _settingsCards.Values)
        {
            if (card.Parent is Panel p) p.Children.Remove(card);
            else if (card.Parent is Decorator d) d.Child = null;
            else if (card.Parent is ContentControl c) c.Content = null;
        }
        _workspace.Children.Clear();
        _workspaceHeader.Children.Clear();
        _workspaceHeader.Children.Add(_workspaceTitle);
        _workspaceTitle.Text = title;
        _workspaceHeader.Visibility = legacy == "home" ? Visibility.Collapsed : Visibility.Visible;
        if (back is not null) WorkspaceActions(("← Назад", back));
        _currentPage = "";
        _suppressNavigation = true;
        try { ShowLegacyPage(legacy ?? "workspace"); }
        finally { _suppressNavigation = false; }
        if (legacy is null) HomeView.Visibility = Visibility.Collapsed;
        _workspace.Visibility = legacy is null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var entry in _workspaceNav) entry.Value.IsChecked = entry.Key == section;

        PageScroll.Background = Brushes.Transparent;
        PageScroll.ScrollToTop();
    }

    private void ShowPage(string page)
    {
        if (!_workspaceReady) { ShowLegacyPage(page); return; }
        switch (page)
        {
            case "mods": OpenPackTab("Моды"); break;
            case "resources": OpenPackTab("Ресурспаки"); break;
            case "logs": OpenDiagnostics("Логи"); break;
            case "host": OpenServers("Мой сервер"); break;
            default: NavigateWorkspace(page); break;
        }
    }

    private void NavigateWorkspace(string section)
    {
        if (section is "worlds" or "servers" or "workshop" or "profile" or "settings")
        {
            foreach (var entry in _workspaceNav) entry.Value.IsChecked = entry.Key == _workspaceSection;
            var title = section switch {
                "worlds" => "Миры", "servers" => "Серверы", "workshop" => "Мастерская",
                "profile" => "Профиль", _ => "Настройки"
            };
            ShowToast(title, "В разработке", "#A9BAC3");
            return;
        }
        switch (section)
        {
            case "instances": OpenBuilds(); break;
            case "worlds": OpenWorlds(); break;
            case "servers": OpenServers("Список серверов"); break;
            case "workshop": OpenWorkshop("Обзор"); break;
            case "doctor": OpenDiagnostics("Проверки"); break;
            case "settings": OpenSettings("Общие"); break;
            case "profile": OpenProfile(); break;
            default:
                BeginWorkspace("home", "Играть", "home");
                WorkspaceActions(("Открыть сборку", () => OpenPackTab("Обзор")), ("Параметры сборки", () => OpenPackTab("Параметры")));
                break;
        }
    }

    private void OpenBuilds()
    {
        OpenPackTab("Моды");
    }

    private bool SelectWorkspaceInstance(InstanceProfile instance)
    {
        if (_busy || _game is { IsRunning: true }) { ShowError("Выбор сборки", "Заверши текущую операцию и закрой игру перед сменой сборки."); return false; }
        if (_instance.Id != instance.Id && !SwitchActiveInstance(instance)) return false;
        _selectedInstanceId = instance.Id;
        PopulateSettingsFields();
        return true;
    }

    private void ManageWorkspaceInstance(Action action)
    {
        var id = _instance.Id;
        OpenInstancesOverlay(); _selectedInstanceId = id; RefreshInstanceList(); action();
    }

    private void OpenPackTab(string tab)
    {
        if (tab == "Обзор") tab = "Моды";
        _packTab = tab;
        string? legacy = tab == "Моды" ? "mods" : tab is "Ресурспаки" or "Шейдеры" ? "resources" : null;
        BeginWorkspace("instances", _instance.Name, legacy, OpenBuilds);
        WorkspaceTabs(tab, new[] { "Обзор", "Моды", "Ресурспаки", "Шейдеры", "Параметры" }.Select(name => (name, (Action)(() => OpenPackTab(name)))).ToArray());
        if (tab == "Обзор")
        {
            _workspace.Children.Add(WorkspaceText($"Minecraft {_instance.BaseMinecraftVersion} · {_instance.LoaderKey}", true));
            _workspace.Children.Add(WorkspaceText(_instance.GameDir));
            _workspace.Children.Add(WorkspaceButton(_instance.IsInstalled ? "Играть" : "Установить", () => { NavigateWorkspace("home"); Play_Click(PlayButton, new RoutedEventArgs()); }, true));
            var actions = new WrapPanel();
            foreach (var (name, action) in new (string, Action)[] {
                ("Экспортировать", () => ManageWorkspaceInstance(() => InstanceExport_Click(this, new RoutedEventArgs()))),
                ("Опубликовать сборку", () => OpenPublication("Сборка", _instance.Name, () => OpenPackTab("Обзор"))),
                ("Переименовать", () => ManageWorkspaceInstance(ShowRenameForm)),
                ("Дублировать", () => ManageWorkspaceInstance(ShowDuplicateForm)),
                ("Открыть папку", () => OpenPath(_instance.GameDir)),
                ("Удалить", () => ManageWorkspaceInstance(ShowDeleteForm)) }) actions.Children.Add(WorkspaceButton(name, action));
            _workspace.Children.Add(actions);
        }
        else if (tab == "Параметры")
        {
            PopulateSettingsFields();
            WorkspaceActions(("Сохранить", () => SaveSettings_Click(this, new RoutedEventArgs())));
            AddSettingsCard("instance");
            AddAdvancedCard("Источник обновления и копии перед обновлением", "pack");
            AddSettingsCard("backups");
        }
        else if (tab == "Моды")
        {
            InstalledTab.IsChecked = true;
            RefreshInstalledContent(); UpdateCatalogInstanceSelector();
            WorkspaceActions(("Найти моды", () => OpenPackCatalog("mod", tab)),
                ("Проверить обновления", () => CheckModUpdates_Click(this, new RoutedEventArgs())),
                ("Открыть папку", () => OpenModsFolder_Click(this, new RoutedEventArgs())));
        }
        else
        {
            ResourceShadersTab.IsChecked = tab == "Шейдеры";
            ResourcePacksTab.IsChecked = tab != "Шейдеры";
            ResourcesInstalledMode.IsChecked = true; ResourceMode_Checked(this, new RoutedEventArgs());
            WorkspaceActions((tab == "Шейдеры" ? "Найти шейдеры" : "Найти ресурспаки", () => OpenPackCatalog(tab == "Шейдеры" ? "shader" : "resourcepack", tab)));
        }
        ComposeBuildLayout(tab);
    }

    private void OpenPackCatalog(string kind, string returnTab)
    {
        var targetId = _instance.Id;
        BeginWorkspace("instances", $"Поиск дополнений · {_instance.Name}", "mods", () => {
            var target = _config.Find(targetId); if (target is not null && SelectWorkspaceInstance(target)) OpenPackTab(returnTab);
        });
        CatalogKindFilter.SelectedItem = CatalogKindFilter.Items.Cast<ComboBoxItem>().FirstOrDefault(i => i.Tag?.ToString() == kind);
        CatalogTab.IsChecked = true; CatalogTab_Checked(this, new RoutedEventArgs());
    }

    private void WorkspaceInstanceSelector()
    {
        var selector = new ComboBox { ItemsSource = _instances.List(_config), DisplayMemberPath = "Name", SelectedItem = _instance,
            MinHeight = 36, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 12) };
        AutomationProperties.SetName(selector, "Целевая сборка");
        selector.SelectionChanged += (_, _) => { if (selector.SelectedItem is InstanceProfile target && SelectWorkspaceInstance(target)) OpenWorlds(); };
        _workspaceHeader.Children.Add(selector);
    }

    private void OpenWorlds(bool catalog = false)
    {
        BeginWorkspace("worlds", $"Миры · {_instance.Name}", "worlds", catalog ? () => OpenWorlds() : null);
        WorkspaceInstanceSelector();
        WorkspaceActions((catalog ? "Мои миры" : "Найти карту", () => OpenWorlds(!catalog)),
            ("Автобэкапы", () => { BeginWorkspace("worlds", $"Автобэкапы · {_instance.Name}", back: () => OpenWorlds()); PopulateSettingsFields(); AddSettingsCard("backups"); _workspace.Children.Add(WorkspaceButton("Сохранить", () => SaveSettings_Click(this, new RoutedEventArgs()), true)); }));
        WorldCatalogTab.IsChecked = catalog; WorldMineTab.IsChecked = !catalog; WorldMode_Checked(this, new RoutedEventArgs());
    }

    private void OpenServers(string tab)
    {
        BeginWorkspace("servers", "Серверы", tab == "Мой сервер" ? "host" : "servers");
        WorkspaceTabs(tab, ("Список серверов", () => OpenServers("Список серверов")), ("Мой сервер", () => OpenServers("Мой сервер")));
        if (tab == "Мой сервер") WorkspaceActions(("Серверные моды", () => { OpenPackTab("Моды"); _modsSide = "server"; SyncModsSide(); _ = RefreshModsAsync(force: true); }));
    }

    private void OpenDiagnostics(string tab)
    {
        BeginWorkspace("doctor", $"Диагностика · {_instance.Name}", tab == "Проверки" ? "doctor" : "logs");
        WorkspaceTabs(tab, ("Проверки", () => OpenDiagnostics("Проверки")), ("Логи", () => OpenDiagnostics("Логи")), ("Краш-репорты", () => OpenDiagnostics("Краш-репорты")));
        if (tab == "Краш-репорты") ShowCrashTab();
        else if (tab == "Логи") ShowLogTab();
        if (tab != "Проверки") WorkspaceActions(("Экспортировать", () => {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Экспорт диагностики", FileName = tab == "Логи" ? "latest.log" : "crash-report.txt", Filter = "Текст|*.txt;*.log" };
            if (dialog.ShowDialog(this) != true) return;
            try { File.WriteAllText(dialog.FileName, tab == "Логи" ? LogTextBox.Text : CrashRawText.Text); }
            catch (Exception ex) { ShowError("Экспорт", ex.Message); }
        }));
    }

    private void AddSettingsCard(string key) => _workspace.Children.Add(_settingsCards[key]);
    private void AddAdvancedCard(string label, string key)
    {
        var expander = new Expander { Header = "Дополнительно · " + label, Content = _settingsCards[key], Margin = new Thickness(0, 0, 0, 16) };
        expander.SetResourceReference(ForegroundProperty, "TextBrush"); _workspace.Children.Add(expander);
    }

    private void OpenSettings(string tab)
    {
        BeginWorkspace("settings", "Настройки"); PopulateSettingsFields();
        WorkspaceTabs(tab, new[] { "Общие", "Оформление", "Обновления", "О программе" }.Select(name => (name, (Action)(() => OpenSettings(name)))).ToArray());
        if (tab == "Общие")
        {
            _workspace.Children.Add(WorkspaceText("Поведение окна и внешний вид меняются сразу. Параметры игры доступны внутри выбранной сборки."));
            _workspace.Children.Add(WorkspaceButton("Параметры выбранной сборки", () => OpenPackTab("Параметры")));
            _workspace.Children.Add(WorkspaceButton("Оформление и поведение окна", () => OpenSettings("Оформление")));
        }
        if (tab == "Оформление") { AddSettingsCard("appearance"); AddCoverSettings(); }
        if (tab == "Обновления") { AddSettingsCard("updates"); _workspace.Children.Add(WorkspaceButton("Сохранить", () => SaveSettings_Click(this, new RoutedEventArgs()), true)); }
        if (tab == "О программе") AddSettingsCard("about");
    }

    private void OpenProfile()
    {
        BeginWorkspace("profile", "Профиль", back: () => NavigateWorkspace("home")); PopulateSettingsFields();
        _workspace.Children.Add(WorkspaceText($"Локальный профиль · {_instance.Name}"));
        AddSettingsCard("profile");
        _workspace.Children.Add(WorkspaceButton("Сохранить ник", () => SaveSettings_Click(this, new RoutedEventArgs()), true));
        _workspace.Children.Add(WorkspaceButton("Мои скины", () => OpenFuture("profile", "Мои скины", "Выбор и применение скинов — в разработке", "Примерить скин", OpenProfile)));
        _workspace.Children.Add(WorkspaceButton("Мои публикации", () => OpenMyPublications(OpenProfile)));
        _workspace.Children.Add(WorkspaceButton("Публичный профиль автора", () => OpenFuture("profile", _instance.Username, "Публичный профиль автора — в разработке. Рейтинг: оценок пока нет.", "Открыть публикации автора", OpenProfile)));
    }

    private void OpenWorkshop(string tab, string price = "Бесплатно")
    {
        BeginWorkspace("workshop", "Мастерская");
        WorkspaceActions(("Опубликовать", () => OpenPublication("Сборка", _instance.Name, () => OpenWorkshop(tab, price))), ("Мои публикации", () => OpenMyPublications(() => OpenWorkshop(tab, price))));
        WorkspaceTabs(tab, new[] { "Обзор", "Сборки", "Карты", "Скины", "Обложки" }.Select(name => (name, (Action)(() => OpenWorkshop(name, price)))).ToArray());
        _workspace.Children.Add(WorkspaceText("Публикации сообщества — в разработке", true));
        _workspace.Children.Add(WorkspaceText("Здесь будут работы игроков. Публикация, получение работ, платежи, отзывы и рейтинг пока недоступны."));
        var prices = new WrapPanel();
        foreach (var name in new[] { "Бесплатно", "Платно" })
        {
            var filter = new RadioButton { Content = name, IsChecked = price == name, GroupName = "WorkshopPrice", Style = (Style)FindResource("FilterRadio"), Margin = new Thickness(0, 0, 8, 16) };
            filter.Click += (_, _) => OpenWorkshop(tab, name); prices.Children.Add(filter);
        }
        _workspace.Children.Add(prices);
        _workspace.Children.Add(WorkspaceText($"{price} · {tab}. Каталог публикаций ещё не подключён."));
        if (tab != "Обзор")
        {
            var (action, hint) = tab switch {
                "Сборки" => ("Установить сборку", "Работа будет установлена как отдельная сборка."),
                "Карты" => ("Добавить карту в сборку", $"Целевая сборка: {_instance.Name}. Карта создаст отдельный мир."),
                "Скины" => ("Примерить скин", "Предпросмотр скина будет доступен в профиле."),
                _ => ("Примерить обложку", "Предпросмотр обложки будет показан на главной.") };
            _workspace.Children.Add(WorkspaceText(hint));
            var button = WorkspaceButton(action, () => { }); button.IsEnabled = false; _workspace.Children.Add(button);
            _workspace.Children.Add(WorkspaceText("В разработке · действие станет доступно после подключения публикаций. Рейтинг: оценок пока нет."));
        }
        _workspace.Children.Add(WorkspaceText("Рабочие каталоги", true));
        _workspace.Children.Add(WorkspaceText("Поиск дополнений Modrinth и карт MapMC работает отдельно от будущих публикаций сообщества."));
        _workspace.Children.Add(WorkspaceButton("Дополнения выбранной сборки", () => OpenPackTab("Моды")));
        _workspace.Children.Add(WorkspaceButton("Найти карту", () => OpenWorlds(true)));
    }

    private void OpenFuture(string section, string title, string description, string action, Action back)
    {
        BeginWorkspace(section, title, back: back);
        _workspace.Children.Add(WorkspaceText("В разработке", true));
        _workspace.Children.Add(WorkspaceText(description));
        var button = WorkspaceButton(action, () => { }); button.IsEnabled = false; _workspace.Children.Add(button);
        _workspace.Children.Add(WorkspaceText("Команда пока недоступна. Изменения не применяются."));
    }

    private void OpenPublication(string type, string name, Action back)
    {
        var parent = _workspaceSection;
        BeginWorkspace(parent, "Опубликовать сборку", back: back);
        _workspace.Children.Add(WorkspaceText(name, true));
        _workspace.Children.Add(WorkspaceText($"Minecraft {_instance.BaseMinecraftVersion} · {_instance.LoaderKey}"));
        _workspace.Children.Add(WorkspaceText("Публикация сборок — в разработке", true));
        _workspace.Children.Add(WorkspaceText("Будут опубликованы состав дополнений и выбранные конфигурации. Личные миры, логи и данные профиля не входят в публикацию по умолчанию."));
        _workspace.Children.Add(WorkspaceText("Описание и обложка · Бесплатно / Платно · Рейтинг и отзывы — в разработке."));
        var publish = WorkspaceButton("Опубликовать", () => { }); publish.IsEnabled = false; _workspace.Children.Add(publish);
        _workspace.Children.Add(WorkspaceText("Публикация недоступна: сервис сообщества ещё не подключён."));
    }

    private void OpenMyPublications(Action back) => OpenFuture("workshop", "Мои публикации", "Публикации и управление авторскими версиями — в разработке", "Создать публикацию", back);

    private void AddCoverSettings()
    {
        _workspace.Children.Add(WorkspaceText("Общий фон страниц", true));
        _workspace.Children.Add(WorkspaceText("Нейтральная панорама входит в приложение. Можно выбрать собственное изображение."));
        _workspace.Children.Add(WorkspaceButton("Выбрать изображение", () => {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Общий фон страниц", Filter = "Изображения|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog(this) != true) return;
            try {
                var image = new System.Windows.Media.Imaging.BitmapImage(new Uri(dialog.FileName));
                var target = Path.Combine(_paths.AppDataDir, "home-cover" + Path.GetExtension(dialog.FileName));
                File.Copy(dialog.FileName, target, true); _config.HomeCoverPath = target; TrySaveConfig(); ApplyHomeCover();
            } catch (Exception ex) { ShowError("Обложка", ex.Message); }
        }));
        _workspace.Children.Add(WorkspaceButton("Вернуть панораму", () => { _config.HomeCoverPath = null; TrySaveConfig(); ApplyHomeCover(); }));
    }

    private void ApplyHomeCover()
    {
        SceneCover.Background = null;
        if (!string.IsNullOrWhiteSpace(_config.HomeCoverPath) && File.Exists(_config.HomeCoverPath))
        {
            try { SceneCover.Background = new ImageBrush(new System.Windows.Media.Imaging.BitmapImage(new Uri(_config.HomeCoverPath))) { Stretch = Stretch.UniformToFill }; }
            catch (Exception) { /* A removed or invalid personal cover falls back to panorama. */ }
        }
    }
}


