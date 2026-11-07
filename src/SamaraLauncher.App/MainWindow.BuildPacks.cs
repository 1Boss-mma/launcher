using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SamaraLauncher.App.Controls;
using SamaraLauncher.App.ViewModels;

namespace SamaraLauncher.App;

public partial class MainWindow
{
    private void InstalledContent_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PackRow pack } host) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = host };
        foreach (var (label, action) in new (string, Action)[] {
            (pack.Enabled ? "Выключить" : "Включить", () => TogglePack_Click(new Button { Tag = pack }, new RoutedEventArgs())),
            ("Открыть папку", () => OpenResourcesFolder_Click(this, new RoutedEventArgs())),
            ("Удалить", () => DeletePack_Click(new Button { Tag = pack }, new RoutedEventArgs())) })
        {
            var item = new MenuItem { Header = label, IsEnabled = !_resourcesBusy };
            item.Click += (_, _) => action(); menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ComposeBuildPacks(Grid body, Grid layout, string tab)
    {
        bool shaders = tab == "Шейдеры";
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BuildSectionInset) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(260) });
        var toolbar = new Grid { Margin = new Thickness(0,0,0,BuildSectionInset) };
        toolbar.ColumnDefinitions.Add(new ColumnDefinition());
        toolbar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        var searchHost = new Grid { Margin = new Thickness(0,0,8,0) };
        var search = new TextBox { Style = (Style)FindResource("BuildSearch"), Height = 36 };
        var hint = new TextBlock { Text = shaders ? "Найти шейдеры…" : "Найти ресурспаки…", Margin = new Thickness(40,0,0,0), IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        searchHost.Children.Add(search); searchHost.Children.Add(hint); toolbar.Children.Add(searchHost);
        AutomationProperties.SetName(search, shaders ? "Поиск установленных шейдеров" : "Поиск установленных ресурспаков");
        var filter = new ComboBox { Style = (Style)FindResource("BuildCombo"), Height = 36, ItemsSource = new[] { "Все", "Включённые", "Выключенные" }, SelectedIndex = 0 };
        Grid.SetColumn(filter, 1); toolbar.Children.Add(filter); Grid.SetColumnSpan(toolbar, 3); body.Children.Add(toolbar);
        var listHost = new Grid(); Grid.SetRow(listHost, 1); body.Children.Add(listHost);
        var list = new ListBox { Foreground = (Brush)FindResource("TextBrush"), Background = Brushes.Transparent, BorderThickness = new Thickness(0), Template = (ControlTemplate)FindResource("BuildModsListTemplate"), ItemTemplate = ModsList.ItemTemplate, ItemContainerStyle = (Style)ModsView.Resources[typeof(ListBoxItem)], HorizontalContentAlignment = HorizontalAlignment.Stretch, SelectionMode = SelectionMode.Extended };
        list.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
        VirtualizingPanel.SetIsVirtualizing(list, true);
        VirtualizingPanel.SetVirtualizationMode(list, VirtualizationMode.Recycling);
        VirtualizingPanel.SetScrollUnit(list, ScrollUnit.Pixel);
        ScrollViewer.SetCanContentScroll(list, true);
        listHost.Children.Add(list);
        var empty = WorkspaceText(shaders ? "Читаем шейдеры…" : "Читаем ресурспаки…"); empty.Margin = new Thickness(12); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Center; listHost.Children.Add(empty);
        var details = new Grid();
        var frame = BuildSurface(details, new Thickness(BuildSectionInset)); frame.SetResourceReference(Border.BackgroundProperty, "BuildDetailsBrush");
        Grid.SetColumn(frame, 2); Grid.SetRow(frame, 1); body.Children.Add(frame);
        var loaded = false;
        void Populate()
        {
            hint.Visibility = string.IsNullOrEmpty(search.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (!loaded) return;
            var selectedPath = (list.SelectedItem as PackRow)?.Pack.FullPath;
            var rows = _packRows.Where(p => p.IsShader == shaders && (p.Title.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase) || p.FileName.Contains(search.Text.Trim(), StringComparison.OrdinalIgnoreCase)) && (filter.SelectedIndex == 0 || p.Enabled == (filter.SelectedIndex == 1))).OrderBy(p => p.Title, StringComparer.OrdinalIgnoreCase).ToList();
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(p => p.Pack.FullPath == selectedPath) ?? rows.FirstOrDefault();
            empty.Text = ResourcesStatusText.Text.StartsWith("Ошибка:", StringComparison.Ordinal) ? ResourcesStatusText.Text : _packRows.Count == 0 ? (shaders ? "Шейдеров пока нет. Импорт доступен в шестерёнке." : "Ресурспаков пока нет. Импорт доступен в шестерёнке.") : "Ничего не найдено";
            empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            PopulateBuildPackDetails(details, list.SelectedItem as PackRow);
        }
        search.TextChanged += (_, _) => Populate(); filter.SelectionChanged += (_, _) => Populate();
        list.SelectionChanged += (_, _) => PopulateBuildPackDetails(details, list.SelectedItem as PackRow);
        _refreshBuildPacks = () => { loaded = true; Populate(); };
        PopulateBuildPackDetails(details, null);
        layout.SizeChanged += (_, _) => { body.ColumnDefinitions[2].Width = new GridLength(layout.ActualWidth < 1000 ? 230 : 260); layout.ColumnDefinitions[0].Width = new GridLength(layout.ActualWidth < 850 ? 170 : 210); };
        _ = RefreshResourcesAsync(force: true);
    }

    private void PopulateBuildPackDetails(Grid details, PackRow? pack)
    {
        details.Children.Clear(); details.RowDefinitions.Clear();
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition());
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        details.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        if (pack is null) { var hint = WorkspaceText("Выбери элемент в списке, чтобы увидеть сведения."); Grid.SetRowSpan(hint, 4); details.Children.Add(hint); return; }
        var header = new Grid { Margin = new Thickness(0,0,0,12) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) }); header.ColumnDefinitions.Add(new ColumnDefinition());
        UIElement icon = BuildContentIcon(pack, pack.Glyph);
        header.Children.Add(icon);
        var titles = new StackPanel();
        var name = WorkspaceText(pack.Title, true); name.FontSize = 20; name.Margin = new Thickness(0,0,0,4); name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = pack.Title; titles.Children.Add(name);
        var kind = WorkspaceText(pack.IsShader ? "Шейдерпак" : "Ресурспак"); kind.FontSize = 12; kind.Margin = new Thickness(0,0,0,8); titles.Children.Add(kind);
        var status = WorkspaceText(pack.Enabled ? "✓ Установлен" : "Установлен · выключен"); status.FontSize = 12; status.Margin = new Thickness(0); status.SetResourceReference(TextBlock.ForegroundProperty, pack.Enabled ? "AccentTextBrush" : "MutedBrush");
        var badge = new Border { Child = status, CornerRadius = new CornerRadius(4), Padding = new Thickness(5,2,5,2), BorderThickness = new Thickness(1), HorizontalAlignment = HorizontalAlignment.Left };
        badge.SetResourceReference(Border.BackgroundProperty, pack.Enabled ? "GreenLoBrush" : "Panel2Brush"); badge.SetResourceReference(Border.BorderBrushProperty, pack.Enabled ? "GreenBrush" : "LineBrush"); titles.Children.Add(badge);
        Grid.SetColumn(titles, 1); header.Children.Add(titles); details.Children.Add(header);
        var description = WorkspaceText(pack.Description); description.TextTrimming = TextTrimming.CharacterEllipsis; description.ToolTip = pack.Description; description.ClipToBounds = true; description.Margin = new Thickness(0,0,0,8); Grid.SetRow(description, 1); details.Children.Add(description);
        var facts = new StackPanel { Margin = new Thickness(0,0,0,8) };
        var line = new Border { Height = 1, Margin = new Thickness(0,0,0,8) }; line.SetResourceReference(Border.BackgroundProperty, "LineBrush"); facts.Children.Add(line);
        BuildModFact(facts, LauncherGlyph.Image, "Формат: " + pack.FormatText);
        BuildModFact(facts, LauncherGlyph.Refresh, "Изменён: " + pack.ModifiedText);
        BuildModFact(facts, LauncherGlyph.Download, "Размер: " + pack.SizeText);
        BuildModFact(facts, pack.Glyph, "Файл: " + pack.FileName);
        Grid.SetRow(facts, 2); details.Children.Add(facts);
        var footer = new UniformGrid { Columns = 2 };
        var remove = BuildCommand("Удалить", () => DeletePack_Click(new Button { Tag = pack }, new RoutedEventArgs())); remove.Content = BuildLabel("Удалить", LauncherGlyph.Trash); remove.FontSize = 12; remove.Padding = new Thickness(4,8,4,8); remove.Margin = new Thickness(0,0,8,0); footer.Children.Add(remove);
        var folder = BuildCommand("Папка", () => OpenResourcesFolder_Click(this, new RoutedEventArgs())); folder.Content = BuildLabel("Папка", LauncherGlyph.Image); folder.FontSize = 12; folder.Margin = new Thickness(0); footer.Children.Add(folder);
        var footerFrame = new Border { Child = footer, BorderThickness = new Thickness(0,1,0,0), Padding = new Thickness(0,12,0,0) }; footerFrame.SetResourceReference(Border.BorderBrushProperty, "LineBrush"); Grid.SetRow(footerFrame, 3); details.Children.Add(footerFrame);
    }
}
