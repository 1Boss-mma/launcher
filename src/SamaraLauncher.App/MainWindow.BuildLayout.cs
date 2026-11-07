using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using SamaraLauncher.App.Controls;
using SamaraLauncher.App.ViewModels;

namespace SamaraLauncher.App;

public partial class MainWindow
{
    private const double BuildSectionInset = 12;
    private FrameworkElement? _borrowedBuildView;
    private Panel? _buildViewParent;
    private int _buildViewIndex;
    private Thickness _buildViewMargin;
    private int _buildViewRow;
    private Grid? _buildDetails;
    private readonly Dictionary<string, Task<SamaraLauncher.Core.Mods.ModrinthProject?>> _buildProjectInfo = new(StringComparer.OrdinalIgnoreCase);
    private Action? _refreshBuildPacks;
    private Grid? _buildToolbar;
    private Grid? _buildToolbarParent;
    private int _buildToolbarIndex;
    private Panel? _buildAnalysisParent;
    private int _buildAnalysisIndex;

    private void RestoreBuildContent()
    {
        if (_buildAnalysisParent is { } analysisParent)
        {
            if (ModAnalysisBanner.Parent is Panel host) host.Children.Remove(ModAnalysisBanner);
            Grid.SetRow(ModAnalysisBanner, 3); Grid.SetColumnSpan(ModAnalysisBanner, 1);
            analysisParent.Children.Insert(Math.Min(_buildAnalysisIndex, analysisParent.Children.Count), ModAnalysisBanner);
            _buildAnalysisParent = null;
        }
        if (_buildToolbar is { } toolbar && _buildToolbarParent is { } original)
        {
            if (toolbar.Parent is Panel host) host.Children.Remove(toolbar);
            Grid.SetRow(toolbar, 1); Grid.SetColumn(toolbar, 0); Grid.SetColumnSpan(toolbar, 1);
            original.Children.Insert(Math.Min(_buildToolbarIndex, original.Children.Count), toolbar);
        }
        _buildToolbar = null; _buildToolbarParent = null;
        _refreshBuildPacks = null;
        if (_borrowedBuildView is { } view && _buildViewParent is { } parent)
        {
            if (view.Parent is Panel host) host.Children.Remove(view);
            view.Margin = _buildViewMargin;
            Grid.SetRow(view, _buildViewRow);
            parent.Children.Insert(Math.Min(_buildViewIndex, parent.Children.Count), view);
        }
        _borrowedBuildView = null;
        _buildViewParent = null;
        _buildDetails = null;
    }

    private Border BuildSurface(UIElement content, Thickness padding)
    {
        var frame = new Border { Child = content, Padding = padding, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) };
        frame.SetResourceReference(Border.BackgroundProperty, "BuildSurfaceBrush");
        frame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        return frame;
    }

    private void ComposeBuildLayout(string tab)
    {
        var settings = _workspace.Children.Cast<UIElement>().ToArray();
        _workspace.Children.Clear();
        _workspace.Visibility = Visibility.Visible;
        _workspaceHeader.Children.Clear();
        _workspaceHeader.Visibility = Visibility.Collapsed;
        ((FrameworkElement)_workspaceHeader.Parent).Visibility = Visibility.Collapsed;
        PageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        PageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        PageScroll.Background = Brushes.Transparent;
        var layout = new Grid { ClipToBounds = true };
        layout.SetBinding(HeightProperty, new Binding("ActualHeight") { Source = PageScroll, Converter = new BuildHeightConverter() });
        layout.Resources[typeof(ScrollBar)] = FindResource("BuildScrollBar");
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        layout.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new DockPanel();
        var sideHeader = new DockPanel { Margin = new Thickness(0,0,0,12) };
        var add = BuildCommand("+", () => InstanceImport_Click(this, new RoutedEventArgs()));
        add.Width = 34; add.Height = 34; add.MinHeight = 34; add.Padding = new Thickness(0); add.Margin = new Thickness(0); add.ToolTip = "Импортировать сборку из архива";
        var plus = new Grid { Width = 16, Height = 16 };
        var horizontal = new Border { Height = 2, VerticalAlignment = VerticalAlignment.Center }; horizontal.SetResourceReference(Border.BackgroundProperty, "TextBrush"); plus.Children.Add(horizontal);
        var vertical = new Border { Width = 2, HorizontalAlignment = HorizontalAlignment.Center }; vertical.SetResourceReference(Border.BackgroundProperty, "TextBrush"); plus.Children.Add(vertical); add.Content = plus;
        AutomationProperties.SetName(add, "Импортировать сборку");
        DockPanel.SetDock(add, Dock.Right); sideHeader.Children.Add(add);
        var label = WorkspaceText("Мои сборки", true); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0);
        sideHeader.Children.Add(label); DockPanel.SetDock(sideHeader, Dock.Top); sidebar.Children.Add(sideHeader);
        var builds = new StackPanel();
        foreach (var instance in _instances.List(_config))
        {
            var row = new Grid { Margin = new Thickness(0,8,0,8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new PixelBlock { Width = 36, Height = 36, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left });
            var text = new StackPanel { Margin = new Thickness(8,0,0,0) };
            var name = WorkspaceText(instance.Name, true); name.FontSize = 16; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.Margin = new Thickness(0,0,0,4); name.ToolTip = instance.Name;
            text.Children.Add(name);
            var meta = WorkspaceText($"{instance.BaseMinecraftVersion} · … модов"); meta.FontSize = 12; meta.Margin = new Thickness(0);
            _ = UpdateBuildCountAsync(meta, instance.GameDir, $"{instance.BaseMinecraftVersion} · ");
            text.Children.Add(meta); Grid.SetColumn(text, 1); row.Children.Add(text);
            if (instance.Id == _instance.Id)
            {
                var play = new LauncherIcon { Kind = LauncherGlyph.Play, Width = 16, Height = 16, Margin = new Thickness(8,0,0,0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(play, 2); row.Children.Add(play);
            }
            var button = new Button { Content = row, Style = (Style)FindResource("BuildSidebarItem"), Height = 68, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Margin = new Thickness(0,0,0,2), Tag = instance.Id == _instance.Id ? "Selected" : null };
            AutomationProperties.SetName(button, $"Открыть сборку {instance.Name}");
            button.Click += (_, _) => { if (SelectWorkspaceInstance(instance)) OpenPackTab(tab); };
            builds.Children.Add(button);
        }
        if (builds.Children.Count == 0) builds.Children.Add(WorkspaceText("Сборок пока нет. Нажми +, чтобы импортировать архив."));
        sidebar.Children.Add(new ScrollViewer { Content = builds, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        layout.Children.Add(BuildSurface(sidebar, new Thickness(BuildSectionInset)));

        var main = new Grid();
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        var top = new DockPanel { Margin = new Thickness(0,0,0,BuildSectionInset) };
        var commands = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var launch = new PixelButton { Content = _instance.IsInstalled ? "Играть" : "Установить", ReferenceChrome = true, Flat = true, IconKind = PixelIconKind.Play, IconSize = 20, FontFamily = new FontFamily("Segoe UI"), FontSize = 17, Height = 48, MinWidth = 140, Margin = new Thickness(0,0,8,0) };
        launch.Click += (_, _) => { NavigateWorkspace("home"); Play_Click(PlayButton, new RoutedEventArgs()); };
        commands.Children.Add(launch);
        var gear = BuildCommand("", () => { });
        var gearContent = new StackPanel { Orientation = Orientation.Horizontal };
        gearContent.Children.Add(new LauncherIcon { Kind = LauncherGlyph.Settings, Width = 22, Height = 22 });
        gear.Content = gearContent; gear.Width = 48; gear.Height = 48; gear.Margin = new Thickness(0); gear.Padding = new Thickness(8);
        var menu = new ContextMenu();
        foreach (var (title, action) in new (string, Action)[] {
            ("Параметры сборки", () => OpenPackTab("Параметры")),
            ("Переименовать", () => ManageWorkspaceInstance(ShowRenameForm)),
            ("Дублировать", () => ManageWorkspaceInstance(ShowDuplicateForm)),
            ("Экспортировать", () => ManageWorkspaceInstance(() => InstanceExport_Click(this, new RoutedEventArgs()))),
            ("Опубликовать сборку", () => OpenPublication("Сборка", _instance.Name, () => OpenPackTab(tab))),
            ("Открыть папку", () => OpenPath(_instance.GameDir)),
            (tab is "Ресурспаки" or "Шейдеры" ? "Импортировать из файлов" : "Добавить моды из файлов", () => { if (tab is "Ресурспаки" or "Шейдеры") ImportResources_Click(this, new RoutedEventArgs()); else ImportMods_Click(this, new RoutedEventArgs()); }),
            (tab == "Шейдеры" ? "Найти шейдеры" : tab == "Ресурспаки" ? "Найти ресурспаки" : "Найти моды", () => OpenPackCatalog(tab == "Шейдеры" ? "shader" : tab == "Ресурспаки" ? "resourcepack" : "mod", tab)),
            ("Проверить обновления модов", () => CheckModUpdates_Click(this, new RoutedEventArgs())),
            ("Клиентские моды", () => { InstalledSideFilter.SelectedIndex = 0; OpenPackTab("Моды"); }),
            ("Серверные моды", () => { InstalledSideFilter.SelectedIndex = 1; OpenPackTab("Моды"); }),
            ("Удалить сборку", () => ManageWorkspaceInstance(ShowDeleteForm)) })
        { var item = new MenuItem { Header = title }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        gear.ContextMenu = menu; gear.Click += (_, _) => { menu.PlacementTarget = gear; menu.IsOpen = true; };
        AutomationProperties.SetName(gear, "Действия со сборкой"); commands.Children.Add(gear);
        DockPanel.SetDock(commands, Dock.Right); top.Children.Add(commands);
        var heading = new Grid { Margin = new Thickness(0,0,12,0) };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) }); heading.ColumnDefinitions.Add(new ColumnDefinition());
        heading.Children.Add(new PixelBlock { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center });
        var summary = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = WorkspaceText(_instance.Name, true); titleText.FontSize = 24; titleText.Margin = new Thickness(0,0,0,4); titleText.TextTrimming = TextTrimming.CharacterEllipsis; titleText.TextWrapping = TextWrapping.NoWrap; titleText.ToolTip = _instance.Name;
        summary.Children.Add(titleText);
        var loaderVersion = _instance.LoaderKey.ToLowerInvariant() switch { "forge" => _instance.ForgeVersion, "fabric" => _instance.FabricVersion, _ => "" };
        var prefix = $"Minecraft {_instance.BaseMinecraftVersion}  |  {_instance.LoaderKey} {loaderVersion}  |  ";
        var version = WorkspaceText(prefix + "… модов"); version.Margin = new Thickness(0); version.FontSize = 12; summary.Children.Add(version);
        _ = UpdateBuildCountAsync(version, _instance.GameDir, prefix);
        Grid.SetColumn(summary, 1); heading.Children.Add(summary); top.Children.Add(heading); main.Children.Add(top);
        var tabs = new UniformGrid { Rows = 1, Margin = new Thickness(0,0,0,BuildSectionInset) };
        foreach (var name in new[] { "Моды", "Ресурспаки", "Шейдеры" })
        {
            var tabContent = new StackPanel { Orientation = Orientation.Horizontal };
            tabContent.Children.Add(new LauncherIcon { Kind = name switch { "Моды" => LauncherGlyph.Mods, "Ресурспаки" => LauncherGlyph.Image, "Шейдеры" => LauncherGlyph.Sun, _ => LauncherGlyph.Settings }, Width = 20, Height = 20, Margin = new Thickness(0,0,8,0) });
            tabContent.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            var radio = new RadioButton { Content = tabContent, GroupName = "BuildContentTabs", IsChecked = name == tab, Style = BuildContentTabStyle(), MinHeight = 44, HorizontalContentAlignment = HorizontalAlignment.Center };
            ((LauncherIcon)tabContent.Children[0]).SetBinding(LauncherIcon.StrokeProperty, new Binding("Foreground") { Source = radio });
            AutomationProperties.SetName(radio, name);
            radio.Click += (_, _) => OpenPackTab(name); tabs.Children.Add(radio);
        }
        Grid.SetRow(tabs, 1); main.Children.Add(tabs);
        var body = new Grid();
        if (tab == "Параметры")
        {
            var form = new StackPanel(); form.Children.Add(BuildCommand("Сохранить", () => SaveSettings_Click(this, new RoutedEventArgs()), true));
            foreach (var item in settings) form.Children.Add(item);
            body.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }
        else if (tab is "Ресурспаки" or "Шейдеры")
        {
            ResourcesView.Visibility = Visibility.Collapsed;
            ComposeBuildPacks(body, layout, tab);
        }
        else
        {
            var view = tab == "Моды" ? (FrameworkElement)ModsView : ResourcesView;
            _borrowedBuildView = view; _buildViewParent = (Panel)view.Parent; _buildViewIndex = _buildViewParent.Children.IndexOf(view); _buildViewMargin = view.Margin;
            _buildViewRow = Grid.GetRow(view);
            _buildViewParent.Children.Remove(view); view.Margin = new Thickness(0); view.Visibility = Visibility.Visible;
            body.Children.Add(view);
            if (tab != "Моды")
            {
                ResourcesInstalledView.Margin = new Thickness(0);
                ((FrameworkElement)ResourcePacksTab.Parent).Visibility = Visibility.Collapsed;
                body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                body.RowDefinitions.Add(new RowDefinition());
                var find = BuildCommand(tab == "Шейдеры" ? "Найти шейдеры" : "Найти ресурспаки", () => OpenPackCatalog(tab == "Шейдеры" ? "shader" : "resourcepack", tab));
                body.Children.Add(find); Grid.SetRow(view, 1);
            }
            if (tab == "Моды")
            {
                body.ColumnDefinitions.Add(new ColumnDefinition());
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                _buildDetails = new Grid();
                var detailsFrame = BuildSurface(_buildDetails, new Thickness(BuildSectionInset));
                detailsFrame.SetResourceReference(Border.BackgroundProperty, "BuildDetailsBrush");
                Grid.SetColumn(detailsFrame, 2); body.Children.Add(detailsFrame);
                UpdateBuildModDetails();
                ModsList.SelectionChanged -= BuildModSelectionChanged;
                ModsList.SelectionChanged += BuildModSelectionChanged;
                InstalledInstanceSelector.Visibility = Visibility.Collapsed;
                var filterGrid = (Grid)InstalledInstanceSelector.Parent;
                filterGrid.ColumnDefinitions[1].Width = new GridLength(0);
                ModsList.Height = double.NaN;
                InstalledModsView.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);
                ModsSubtitle.Visibility = Visibility.Collapsed;
                ModsList.Background = Brushes.Transparent;
                ModsList.Template = (ControlTemplate)FindResource("BuildModsListTemplate");
                VirtualizingPanel.SetIsVirtualizing(ModsList, true);
                VirtualizingPanel.SetVirtualizationMode(ModsList, VirtualizationMode.Recycling);
                ScrollViewer.SetCanContentScroll(ModsList, true);
                ModsList.BorderThickness = new Thickness(0);
                ModSearchPlaceholder.Text = "Найти моды…"; ModSearchPlaceholder.Opacity = 1;
                if (InstalledModsView.Children[1] is Grid toolbar)
                {
                    _buildToolbar = toolbar; _buildToolbarParent = InstalledModsView; _buildToolbarIndex = InstalledModsView.Children.IndexOf(toolbar);
                    InstalledModsView.Children.Remove(toolbar);
                    body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    body.RowDefinitions.Add(new RowDefinition());
                    Grid.SetRow(view, 2); Grid.SetRow(detailsFrame, 2);
                    Grid.SetRow(toolbar, 0); Grid.SetColumnSpan(toolbar, 3); body.Children.Add(toolbar);
                    _buildAnalysisParent = (Panel)ModAnalysisBanner.Parent;
                    _buildAnalysisIndex = _buildAnalysisParent.Children.IndexOf(ModAnalysisBanner);
                    _buildAnalysisParent.Children.Remove(ModAnalysisBanner);
                    Grid.SetRow(ModAnalysisBanner, 1); Grid.SetColumnSpan(ModAnalysisBanner, 3); body.Children.Add(ModAnalysisBanner);
                    UpdateModAnalysisBanner();
                    ConfigureBuildToolbar(toolbar, filterGrid);
                    var find = toolbar.Children.OfType<Button>().FirstOrDefault();
                    if (find is not null) {
                        find.Content = BuildLabel("Найти моды", LauncherGlyph.Download); find.MinHeight = 36;
                        find.Click -= ModFind_Click;
                        find.Click -= BuildFindMods_Click;
                        find.Click += BuildFindMods_Click;
                    }
                }
                layout.SizeChanged += (_, _) => {
                    bool compact = false;
                    body.ColumnDefinitions[2].Width = new GridLength(layout.ActualWidth < 1000 ? 230 : 260);
                    body.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 12);
                    Grid.SetRow(detailsFrame, 2); Grid.SetColumn(detailsFrame, compact ? 0 : 2);
                    Grid.SetColumnSpan(detailsFrame, compact ? 3 : 1);
                    if (body.RowDefinitions.Count < 3) body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    detailsFrame.MaxHeight = compact ? 160 : double.PositiveInfinity;
                    detailsFrame.Margin = compact ? new Thickness(0,12,0,0) : new Thickness(0);
                    layout.ColumnDefinitions[0].Width = new GridLength(layout.ActualWidth < 850 ? 170 : 210);
                };
            }
        }
        Grid.SetRow(body, 2); main.Children.Add(body);
        var mainFrame = BuildSurface(main, new Thickness(BuildSectionInset)); Grid.SetColumn(mainFrame, 2); layout.Children.Add(mainFrame);
        _workspace.Children.Add(layout);
    }

    private void BuildModSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBuildModDetails();

    private void BuildFindMods_Click(object sender, RoutedEventArgs e) => OpenPackCatalog("mod", "Моды");

    private Button BuildCommand(string text, Action action, bool primary = false)
    {
        var button = new Button { Content = text, Style = (Style)FindResource(primary ? "BuildPrimaryButton" : "BuildButton"), Margin = new Thickness(0,0,8,8) };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => action();
        return button;
    }

    private static StackPanel BuildLabel(string text, LauncherGlyph glyph)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new LauncherIcon { Kind = glyph, Width = 16, Height = 16, Margin = new Thickness(0,0,6,0), VerticalAlignment = VerticalAlignment.Center };
        icon.SetBinding(LauncherIcon.StrokeProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
        row.Children.Add(icon); row.Children.Add(new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center });
        return row;
    }

    private static Style BuildContentTabStyle() => (Style)System.Windows.Markup.XamlReader.Parse("""
        <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
          <Setter Property="Foreground" Value="{DynamicResource TextBrush}"/><Setter Property="FontSize" Value="14"/><Setter Property="FontWeight" Value="SemiBold"/><Setter Property="Cursor" Value="Hand"/><Setter Property="Margin" Value="0"/>
          <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton"><Border x:Name="Surface" Background="Transparent" BorderBrush="Transparent" BorderThickness="1" CornerRadius="4" Padding="8,8"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
            <ControlTemplate.Triggers>
              <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource Panel2Brush}"/></Trigger>
              <Trigger Property="IsChecked" Value="True"><Setter TargetName="Surface" Property="Background" Value="{DynamicResource GreenLoBrush}"/><Setter TargetName="Surface" Property="BorderBrush" Value="{DynamicResource GreenBrush}"/><Setter Property="Foreground" Value="{DynamicResource AccentTextBrush}"/></Trigger>
              
            </ControlTemplate.Triggers>
          </ControlTemplate></Setter.Value></Setter>
        </Style>
        """);

    private static int CountBuildMods(string directory)
    {
        try { var path = Path.Combine(directory, "mods"); return Directory.Exists(path) ? Directory.EnumerateFiles(path).Count(p => p.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)) : 0; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static async Task UpdateBuildCountAsync(TextBlock label, string directory, string prefix)
    {
        var count = await Task.Run(() => CountBuildMods(directory));
        label.Text = prefix + count + " модов";
    }

    private void ConfigureBuildToolbar(Grid toolbar, Grid filters)
    {
        var search = toolbar.Children.OfType<Grid>().First(g => g != filters);
        foreach (var button in toolbar.Children.OfType<Button>()) button.Visibility = Visibility.Collapsed;
        toolbar.RowDefinitions.Clear(); toolbar.ColumnDefinitions.Clear();
        toolbar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        Grid.SetRow(search, 0); Grid.SetColumn(search, 0); Grid.SetColumnSpan(search, 1);
        Grid.SetRow(filters, 0); Grid.SetColumn(filters, 1); Grid.SetColumnSpan(filters, 1);
        toolbar.Margin = new Thickness(0,0,0,BuildSectionInset);
        search.Margin = new Thickness(0,0,8,0);
        filters.Margin = new Thickness(0);
        InstalledFilter.Height = 36; InstalledFilter.Margin = new Thickness(0); InstalledFilter.Style = (Style)FindResource("BuildCombo");
        filters.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        foreach (var column in filters.ColumnDefinitions.Cast<ColumnDefinition>().Skip(1)) column.Width = new GridLength(0);
        InstalledSideFilter.Visibility = Visibility.Collapsed;
        foreach (var button in filters.Children.OfType<Button>()) button.Visibility = Visibility.Collapsed;
        ModSearch.Height = 36; ModSearch.Style = (Style)FindResource("BuildSearch");
        ModSearchPlaceholder.Margin = new Thickness(40,0,0,0); ModSearchPlaceholder.FontSize = 14; ModSearchPlaceholder.FontFamily = new FontFamily("Segoe UI");
    }
    private void BuildModMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModRow mod } button) return;
        var menu = new ContextMenu();
        foreach (var (label, action) in new (string, Action)[] {
            (mod.Enabled ? "Выключить" : "Включить", () => InstalledContentToggle_Click(new Button { Tag = mod }, new RoutedEventArgs())),
            ("Версии и установка", () => InstalledModVersions_Click(new Button { Tag = mod }, new RoutedEventArgs())),
            ("Открыть страницу", () => InstalledModPage_Click(new Button { Tag = mod }, new RoutedEventArgs())),
            ("Откатить обновление", () => InstalledModRollback_Click(new Button { Tag = mod }, new RoutedEventArgs())),
            ("Удалить", () => DeleteMod_Click(new Button { Tag = mod }, new RoutedEventArgs())) })
        {
            var item = new MenuItem { Header = label, IsEnabled = label is "Включить" or "Выключить" or "Удалить" || mod.CatalogRecord is not null };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        menu.PlacementTarget = button; menu.IsOpen = true; e.Handled = true;
    }

    private TextBlock BuildModFact(Panel parent, LauncherGlyph glyph, string text)
    {
        var row = new Grid { Margin = new Thickness(0,0,0,6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) }); row.ColumnDefinitions.Add(new ColumnDefinition());
        var icon = new LauncherIcon { Kind = glyph, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        icon.SetResourceReference(LauncherIcon.StrokeProperty, "MutedBrush"); row.Children.Add(icon);
        var label = WorkspaceText(text); label.FontSize = 12; label.Margin = new Thickness(0); label.TextWrapping = TextWrapping.NoWrap; label.TextTrimming = TextTrimming.CharacterEllipsis; label.ToolTip = text;
        Grid.SetColumn(label, 1); row.Children.Add(label); parent.Children.Add(row); return label;
    }

    private async Task PopulateBuildModFactsAsync(string projectId, TextBlock downloads, TextBlock updated)
    {
        try
        {
            if (!_buildProjectInfo.TryGetValue(projectId, out var pending))
            {
                pending = _catalogClient.GetProjectAsync(projectId);
                _buildProjectInfo[projectId] = pending;
            }
            var project = await pending;
            if (project is null) return;
            downloads.Text = project.Downloads is { } count ? $"Загрузки: {count:N0}" : "Загрузки: —";
            updated.Text = project.Updated is { } date ? $"Обновлён: {date:dd MMM yyyy}" : "Обновлён: —";
            downloads.ToolTip = downloads.Text; updated.ToolTip = updated.Text;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _buildProjectInfo.Remove(projectId);
            downloads.ToolTip = "Сведения Modrinth недоступны. При следующем выборе мода загрузка повторится.";
            updated.ToolTip = downloads.ToolTip;
        }
    }

    private async void ReinstallBuildMod(ModRow mod)
    {
        if (mod.CatalogRecord is not { } record || _busy || !mod.Enabled) return;
        await InstallCatalogVersionAsync(new SamaraLauncher.Core.Mods.ModrinthSearchHit { Id = record.ProjectId, Title = mod.DisplayName }, record.VersionId, _instance, _modsSide, GetCatalogMinecraft(_instance), GetCatalogLoader(_instance), confirm: false, reinstall: true);
    }

    private void UpdateBuildModDetails()
    {
        if (_buildDetails is not { } details) return;
        details.Children.Clear(); details.RowDefinitions.Clear();
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition());
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (ModsList.SelectedItem is not ModRow mod)
        {
            var empty = WorkspaceText("Выбери мод в списке, чтобы увидеть сведения.");
            Grid.SetRowSpan(empty, 4); details.Children.Add(empty); return;
        }
        var header = new Grid { Margin = new Thickness(0,0,0,12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var external = BuildCommand("↗", () => InstalledModPage_Click(new Button { Tag = mod }, new RoutedEventArgs())); external.Width = 24; external.Height = 24; external.MinHeight = 24; external.Padding = new Thickness(0); external.Margin = new Thickness(4,0,0,0); external.Background = Brushes.Transparent; external.BorderThickness = new Thickness(0); external.VerticalAlignment = VerticalAlignment.Top; external.IsEnabled = mod.CatalogRecord is not null; external.ToolTip = "Открыть страницу мода"; Grid.SetColumn(external, 2); header.Children.Add(external);
        UIElement icon = BuildContentIcon(mod, LauncherGlyph.Mods);
        header.Children.Add(icon);
        var titles = new StackPanel();
        var name = WorkspaceText(mod.DisplayName, true); name.FontSize = 20; name.Margin = new Thickness(0,0,0,4); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = mod.DisplayName; titles.Children.Add(name);
        var version = WorkspaceText($"Версия: {mod.VersionText}"); version.FontSize = 12; version.Margin = new Thickness(0,0,0,4); version.TextWrapping = TextWrapping.NoWrap; version.TextTrimming = TextTrimming.CharacterEllipsis; version.ToolTip = mod.VersionText; titles.Children.Add(version);
        if (mod.Metadata?.Authors is { Count: > 0 } authors)
        {
            var authorText = string.Join(", ", authors);
            var author = WorkspaceText("Автор: " + authorText); author.FontSize = 12; author.Margin = new Thickness(0,0,0,4); author.TextWrapping = TextWrapping.NoWrap; author.TextTrimming = TextTrimming.CharacterEllipsis; author.ToolTip = authorText; titles.Children.Add(author);
        }
        var badge = WorkspaceText("✓ Установлен"); badge.FontSize = 12; badge.Margin = new Thickness(0); badge.SetResourceReference(TextBlock.ForegroundProperty, "SuccessBrush"); badge.Foreground = Brushes.White;
        var badgeFrame = new Border { Child = badge, CornerRadius = new CornerRadius(4), Padding = new Thickness(5,2,5,2), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left }; badgeFrame.SetResourceReference(Border.BackgroundProperty, "GreenLoBrush"); badgeFrame.SetResourceReference(Border.BorderBrushProperty, "GreenBrush"); titles.Children.Add(badgeFrame);
        Grid.SetColumn(titles, 1); header.Children.Add(titles); details.Children.Add(header);
        var descriptionText = string.IsNullOrWhiteSpace(mod.Metadata?.Description) ? "Описание в файле мода не указано." : mod.Metadata.Description;
        var description = WorkspaceText(descriptionText); description.TextTrimming = TextTrimming.CharacterEllipsis; description.ToolTip = descriptionText; description.Margin = new Thickness(0,0,0,8); description.ClipToBounds = true;
        Grid.SetRow(description, 1); details.Children.Add(description);
        var info = new StackPanel { Margin = new Thickness(0,4,0,8) };
        var line = new Border { Height = 1, Margin = new Thickness(0,0,0,8) }; line.SetResourceReference(Border.BackgroundProperty, "LineBrush"); info.Children.Add(line);
        var downloads = BuildModFact(info, LauncherGlyph.Download, "Загрузки: —");
        var updated = BuildModFact(info, LauncherGlyph.Refresh, "Обновлён: —");
        var dependencies = mod.Metadata?.Dependencies is { Count: > 0 } deps ? string.Join(", ", deps.Where(d => d.ModId != "minecraft" && d.ModId != "forge" && d.ModId != "fabricloader").Select(d => d.ModId)) : "";
        BuildModFact(info, LauncherGlyph.Builds, "Зависимости: " + (string.IsNullOrWhiteSpace(dependencies) ? "нет" : dependencies));
        BuildModFact(info, LauncherGlyph.Mods, $"Совместим: {mod.LoaderText}, {_instance.BaseMinecraftVersion}");
        if (mod.CatalogRecord is { } record) _ = PopulateBuildModFactsAsync(record.ProjectId, downloads, updated);
        else { downloads.ToolTip = "Для локального файла источник не указан"; updated.ToolTip = "Дата обновления проекта неизвестна"; }
        Grid.SetRow(info, 2); details.Children.Add(info);
        var footer = new Grid(); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.8, GridUnitType.Star) }); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.2, GridUnitType.Star) });
        var delete = BuildCommand("Удалить", () => DeleteMod_Click(new Button { Tag = mod }, new RoutedEventArgs())); delete.Padding = new Thickness(4,8,4,8); delete.FontSize = 12; delete.Content = BuildLabel("Удалить", LauncherGlyph.Trash); delete.Margin = new Thickness(0,0,8,0); footer.Children.Add(delete);
        var versions = BuildCommand("Переустановить", () => ReinstallBuildMod(mod)); versions.Content = BuildLabel("Переустановить", LauncherGlyph.Refresh); versions.Padding = new Thickness(4,8,4,8); versions.FontSize = 12; versions.Margin = new Thickness(0); versions.IsEnabled = mod.CatalogRecord is not null && mod.Enabled; versions.ToolTip = mod.CatalogRecord is null ? "Источник локального файла не указан" : "Заново скачать установленную версию мода"; Grid.SetColumn(versions, 1); footer.Children.Add(versions);
        var footerFrame = new Border { Child = footer, BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(0,12,0,0) }; footerFrame.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        Grid.SetRow(footerFrame, 3); details.Children.Add(footerFrame);
    }

    private static Grid BuildContentIcon(object row, LauncherGlyph glyph)
    {
        var host = new Grid { Width = 56, Height = 56, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
        var fallback = new LauncherIcon { Kind = glyph, Width = 40, Height = 40 };
        fallback.SetResourceReference(LauncherIcon.StrokeProperty, "MutedBrush");
        var style = new Style(typeof(LauncherIcon));
        style.Setters.Add(new Setter(VisibilityProperty, Visibility.Collapsed));
        var missing = new DataTrigger { Binding = new Binding("IconImage") { Source = row }, Value = null };
        missing.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible)); style.Triggers.Add(missing); fallback.Style = style;
        host.Children.Add(fallback);
        var image = new Image { Stretch = Stretch.Uniform };
        image.SetBinding(Image.SourceProperty, new Binding("IconImage") { Source = row });
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        host.Children.Add(image);
        return host;
    }
}
internal sealed class BuildHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => Math.Max(320, (value is double height ? height : 600) - 40);
    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
}











