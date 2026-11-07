using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SamaraLauncher.App.Controls;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Mods;

namespace SamaraLauncher.App;

public partial class MainWindow
{
    private List<object> _installedContent = new();
    private List<SamaraLauncher.App.ViewModels.PackRow> _installedPacks = new();
    private sealed record InstalledContentDivider(string DisplayName) { public bool IsSeparator => true; }
    private void RefreshInstalledContent()
    {
        if (ModSearch is null) return;
        var query = ModSearch.Text.Trim();
        var filter = GetModFilter();
        var content = new List<object>();
        var packs = _installedPacks.Where(p => filter != "updates" && filter != "problems" && (p.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || p.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)) && (filter != "enabled" || p.Enabled) && (filter != "disabled" || !p.Enabled)).ToList();
        foreach (var shader in new[] { false, true })
        {
            var group = _workspaceReady ? new List<SamaraLauncher.App.ViewModels.PackRow>() : packs.Where(p => p.IsShader == shader).ToList();
            if (group.Count == 0) continue;
            content.Add(new InstalledContentDivider(shader ? "ШЕЙДЕРЫ" : "ТЕКСТУРПАКИ"));
            foreach (var pack in group) content.Add(pack);
        }
        if (_modRows.Count > 0 && !_workspaceReady) content.Add(new InstalledContentDivider("МОДЫ"));
        foreach (var mod in _modRows.OrderBy(mod => mod.DisplayName, StringComparer.OrdinalIgnoreCase)) content.Add(mod);
        if (_installedContent.SequenceEqual(content)) return;
        _installedContent = content;
        var selectedFile = (ModsList.SelectedItem as SamaraLauncher.App.ViewModels.ModRow)?.FileName;
        ModsList.ItemsSource = content;
        if (_workspaceSection == "instances" && _packTab == "Моды")
            ModsList.SelectedItem = content.OfType<SamaraLauncher.App.ViewModels.ModRow>().FirstOrDefault(m => m.FileName == selectedFile)
                ?? content.OfType<SamaraLauncher.App.ViewModels.ModRow>().FirstOrDefault();
        NoModsText.Visibility = _installedContent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void InstalledContentToggle_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SamaraLauncher.App.ViewModels.PackRow) TogglePack_Click(sender, e);
        else ModToggle_Click(sender, e);
    }
    private void InstalledContentDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is SamaraLauncher.App.ViewModels.PackRow) DeletePack_Click(sender, e);
        else DeleteMod_Click(sender, e);
    }
    private ContentBrowser? _resourceBrowser;
    private ContentBrowser? _worldBrowser;
    private void ResourceMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || ResourceCatalogHost is null || ResourcesInstalledView is null) return;
        bool catalog = ResourcesCatalogMode.IsChecked == true;
        ResourcesInstalledView.Visibility = catalog ? Visibility.Collapsed : Visibility.Visible;
        ResourceCatalogHost.Visibility = catalog ? Visibility.Visible : Visibility.Collapsed;
        if (catalog)
        {
            _resourceBrowser ??= new ContentBrowser(this, false);
            if (!ResourceCatalogHost.Children.Contains(_resourceBrowser)) ResourceCatalogHost.Children.Add(_resourceBrowser);
            _ = _resourceBrowser.ReloadAsync();
        }
        else _ = RefreshResourcesAsync(force: true);
    }
    private void WorldMode_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || WorldCatalogHost is null || WorldsInstalledView is null) return;
        bool catalog = WorldCatalogTab.IsChecked == true;
        WorldsInstalledView.Visibility = catalog ? Visibility.Collapsed : Visibility.Visible;
        WorldCatalogHost.Visibility = catalog ? Visibility.Visible : Visibility.Collapsed;
        if (catalog)
        {
            _worldBrowser ??= new ContentBrowser(this, true);
            if (!WorldCatalogHost.Children.Contains(_worldBrowser)) WorldCatalogHost.Children.Add(_worldBrowser);
            _ = _worldBrowser.ReloadAsync();
        }
        else _ = RefreshWorldsAsync(force: true);
    }

    private void CatalogKind_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) UpdateCatalogKind();
    }
    private void UpdateCatalogKind()
    {
        if (CatalogView is null || ModsResourceCatalogHost is null || CatalogTab?.IsChecked != true) return;
        var kind = (CatalogKindFilter.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "mod";
        CatalogCategoryFilter.Visibility = kind == "mod" ? Visibility.Visible : Visibility.Collapsed;
        if (CatalogCategoryFilter.Parent is Grid filterRow)
            filterRow.ColumnDefinitions[1].Width = kind == "mod" ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        CatalogView.Visibility = kind == "mod" ? Visibility.Visible : Visibility.Collapsed;
        ModsResourceCatalogHost.Visibility = kind == "mod" ? Visibility.Collapsed : Visibility.Visible;
        _catalogRequest?.Cancel();
        if (kind == "mod") ScheduleCatalogSearch();
        else
        {
            _resourceBrowser ??= new ContentBrowser(this, false);
            if (_resourceBrowser.Parent is Panel parent && parent != ModsResourceCatalogHost) parent.Children.Remove(_resourceBrowser);
            if (!ModsResourceCatalogHost.Children.Contains(_resourceBrowser)) ModsResourceCatalogHost.Children.Add(_resourceBrowser);
            _resourceBrowser.SetKind(kind);
        }
    }
    private void ImportPackFromMods_Click(object sender, RoutedEventArgs e)
    {
        ResourceShadersTab.IsChecked = (sender as FrameworkElement)?.Tag?.ToString() == "shaders";
        ResourcePacksTab.IsChecked = ResourceShadersTab.IsChecked != true;
        ImportResources_Click(sender, e);
    }
    private void OpenPackFolderFromMods_Click(object sender, RoutedEventArgs e)
    {
        ResourceShadersTab.IsChecked = (sender as FrameworkElement)?.Tag?.ToString() == "shaders";
        ResourcePacksTab.IsChecked = ResourceShadersTab.IsChecked != true;
        OpenResourcesFolder_Click(sender, e);
    }

    private sealed class ContentBrowser : Grid
    {
        private static readonly HttpClient Http = CreateHttp();
        private readonly MainWindow _owner;
        private readonly bool _worlds;
        private readonly TextBox _search = new();
        private readonly ComboBox _kind = new();
        private readonly ComboBox _sort = new();
        private readonly ComboBox _instances = new();
        private readonly ComboBox _worldVersion = new();
        private readonly TextBlock _status = new();
        private readonly StackPanel _items = new();
        private readonly ScrollViewer _scroll = new();
        private CancellationTokenSource? _request;
        private int _page;
        private bool _loading;
        private bool _more;
        private bool _installing;
        private bool _updatingInstances;
        private string _worldVersionValue = "";
        private bool _loadingVersions;
        private string _query = "";
        private string _kindValue = "resourcepack";
        private InstanceProfile? _selected;
        private static HttpClient CreateHttp()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SamaraLauncher/0.3.1");
            return http;
        }
        public ContentBrowser(MainWindow owner, bool worlds)
        {
            _owner = owner; _worlds = worlds;
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            MinHeight = worlds ? 500 : 0;
            var searchGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition());
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _search.Style = (Style)owner.ModsView.FindResource("ModsTextBox");
            _search.Height = 34; _search.Padding = new Thickness(10, 0, 10, 0); _search.VerticalContentAlignment = VerticalAlignment.Center;
            _kind.Style = (Style)owner.FindResource("PixelComboBox"); _kind.Height = 28;
            _instances.Style = (Style)owner.FindResource("PixelComboBox"); _instances.Height = 28;
            _search.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; _ = ReloadAsync(); } };
            searchGrid.Children.Add(_search);
            var find = new Button { Content = "Найти", Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(14, 0, 14, 0) };
            var findStyle = new Style(typeof(Button), (Style)owner.FindResource("HomeSelectorButton"));
            findStyle.Setters.Add(new Setter(Button.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x20,0x2C,0x35))));
            findStyle.Setters.Add(new Setter(Button.ForegroundProperty, Brushes.White));
            findStyle.Setters.Add(new Setter(Button.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x64,0x76,0x80))));
            findStyle.Setters.Add(new Setter(Button.MinHeightProperty, 28d)); find.Style = findStyle;
            if (worlds)
            {
                searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var import = new Button { Content = "Добавить ZIP", Style = findStyle, MinHeight = 28, Padding = new Thickness(10, 0, 10, 0), Margin = new Thickness(8, 0, 0, 0) };
                import.Click += (sender, e) => owner.ImportWorld_Click(sender, e);
                SetColumn(import, 2); searchGrid.Children.Add(import);
            }
            find.Click += (_, _) => _ = ReloadAsync(); SetColumn(find, 1); searchGrid.Children.Add(find); Children.Add(searchGrid);
            if (!worlds) searchGrid.Visibility = Visibility.Collapsed;
            var filters = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.ColumnDefinitions.Add(new ColumnDefinition());
            if (!worlds)
            {
                _kind.Items.Add(new ComboBoxItem { Content = "Текстурпаки", Tag = "resourcepack" });
                _kind.Items.Add(new ComboBoxItem { Content = "Шейдеры", Tag = "shader" });
                _kind.SelectedIndex = 0; _kind.Margin = new Thickness(0, 0, 8, 0);
                _kind.SelectionChanged += (_, _) => { if (IsLoaded) _ = ReloadAsync(); };
                _kind.Visibility = Visibility.Collapsed;
                _sort.Style = (Style)owner.FindResource("PixelComboBox");
                _sort.Items.Add(new ComboBoxItem { Content = "Популярные", Tag = "downloads" });
                _sort.Items.Add(new ComboBoxItem { Content = "Непопулярные", Tag = "newest" });
                _sort.SelectedIndex = 0;
                _sort.SelectionChanged += (_, _) => { if (IsLoaded) _ = ReloadAsync(); };
                filters.Children.Add(_sort);
            }
            else
            {
                _worldVersion.Style = (Style)owner.FindResource("PixelComboBox");
                _worldVersion.Height = 28; _worldVersion.Margin = new Thickness(0, 0, 8, 0);
                _worldVersion.Items.Add(new ComboBoxItem { Content = "Все версии Minecraft", Tag = "" });
                _worldVersion.SelectedIndex = 0;
                _worldVersion.SelectionChanged += (_, _) => { if (IsLoaded && !_loadingVersions) _ = ReloadAsync(); };
                filters.Children.Add(_worldVersion);
                Loaded += async (_, _) => await LoadWorldVersionsAsync();
            }
            _instances.DisplayMemberPath = "Name"; _instances.SelectionChanged += (_, _) =>
            {
                if (!IsLoaded || _updatingInstances) return;
                if (_instances.SelectedItem is InstanceProfile chosen && chosen.Id != _owner._instance.Id)
                {
                    if (_owner._busy || _owner._game is { IsRunning: true } || !_owner.SwitchActiveInstance(chosen))
                    {
                        _updatingInstances = true;
                        _instances.SelectedItem = (_instances.ItemsSource as System.Collections.IEnumerable)?.Cast<InstanceProfile>().FirstOrDefault(p => p.Id == _owner._instance.Id);
                        _updatingInstances = false;
                        _status.Text = "Не удалось сменить сборку. Закройте игру и дождитесь завершения операций.";
                        return;
                    }
                }
                _ = ReloadAsync();
            };
            if (worlds) filters.ColumnDefinitions[0].Width = new GridLength(260);
            SetColumn(_instances, 1); filters.Children.Add(_instances);
            SetRow(filters, 1); Children.Add(filters);
            if (!worlds) filters.Visibility = Visibility.Collapsed;
            _status.Margin = new Thickness(0, 0, 0, 7); _status.Foreground = new SolidColorBrush(Color.FromRgb(0xB7, 0xC7, 0xD1));
            SetRow(_status, 2); Children.Add(_status);
            _items.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("ViewportWidth") { Source = _scroll });
            _items.MinWidth = 0;
            ClipToBounds = true;
            _scroll.CanContentScroll = false;
            _scroll.Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='{x:Type ScrollViewer}' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'><Grid ClipToBounds='True'><Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions><ScrollContentPresenter x:Name='PART_ScrollContentPresenter' CanContentScroll='False' Margin='{TemplateBinding Padding}' Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}'/><ScrollBar x:Name='PART_VerticalScrollBar' Grid.Column='1' Orientation='Vertical' Maximum='{TemplateBinding ScrollableHeight}' ViewportSize='{TemplateBinding ViewportHeight}' Value='{Binding VerticalOffset, RelativeSource={RelativeSource TemplatedParent}, Mode=OneWay}' Visibility='{TemplateBinding ComputedVerticalScrollBarVisibility}'/></Grid></ControlTemplate>");
            _scroll.Content = _items; _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.ScrollChanged += (_, e) => { if (!_loading && _more && e.ExtentHeight > 0 && e.VerticalOffset + e.ViewportHeight >= e.ExtentHeight - 100) _ = LoadPageAsync(); };
            var border = new Border { Background = new SolidColorBrush(Color.FromRgb(0x11, 0x1B, 0x22)), BorderBrush = new SolidColorBrush(Color.FromRgb(0x43, 0x56, 0x62)), BorderThickness = new Thickness(1), Child = _scroll, Height = 420 };
            SetRow(border, 3); Children.Add(border);
        }
        public void SetKind(string kind)
        {
            var index = kind == "shader" ? 1 : 0;
            if (_kind.SelectedIndex != index) _kind.SelectedIndex = index;
            else _ = ReloadAsync();
        }
        private async Task LoadWorldVersionsAsync()
        {
            if (!_worlds || _loadingVersions || _worldVersion.Items.Count > 1) return;
            _loadingVersions = true;
            try
            {
                var versions = await _owner.VersionCatalog.GetVersionsAsync();
                foreach (var version in versions.Where(v => v.IsRelease))
                    _worldVersion.Items.Add(new ComboBoxItem { Content = version.Id, Tag = version.Id });
            }
            catch (Exception)
            {
                _worldVersion.ToolTip = "Не удалось загрузить список версий Minecraft. Каталог карт доступен без фильтра.";
            }
            finally { _loadingVersions = false; }
        }
        public async Task ReloadAsync()
        {
            if (_installing) return;
            _request?.Cancel();
            var prior = _owner._instance.Id;
            _updatingInstances = true;
            try
            {
                _instances.ItemsSource = _owner._instances.List(_owner._config);
                _instances.SelectedItem = (_instances.ItemsSource as System.Collections.IEnumerable)?.Cast<InstanceProfile>().FirstOrDefault(p => p.Id == prior) ?? _owner._instance;
            }
            finally { _updatingInstances = false; }
            _selected = _instances.SelectedItem as InstanceProfile ?? _owner._instance;
            _kindValue = (_kind.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "resourcepack";
            _query = (_worlds ? _search.Text : _owner.CatalogSearch.Text).Trim(); _worldVersionValue = (_worldVersion.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? ""; _page = 0; _more = false; _items.Children.Clear();
            _request = new CancellationTokenSource();
            await LoadPageAsync();
        }
        private async Task LoadPageAsync()
        {
            if (_loading || _selected is null || _request is null) return;
            _loading = true; var request = _request; var instance = _selected; var kind = _kindValue; var next = _page + 1;
            try
            {
                _status.Text = "Загрузка каталога…";
                if (_worlds)
                {
                    var html = await Http.GetStringAsync("https://mapmc.app/showcase", request.Token);
                    if (request.IsCancellationRequested || request != _request) return;
                    var cards = Regex.Matches(html, @"<article\b[^>]*>.*?</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
                    foreach (Match card in cards)
                    {
                        var text = card.Value;
                        var slug = Regex.Match(text, "href=\"/api/world-tour/download/([a-z0-9-]+)/java\"", RegexOptions.IgnoreCase).Groups[1].Value;
                        var title = WebUtility.HtmlDecode(Regex.Match(text, @"<h3\b[^>]*>(.*?)</h3>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value);
                        var version = WebUtility.HtmlDecode(Regex.Match(text, @"Archive version</dt>\s*<dd[^>]*>(.*?)</dd>", RegexOptions.Singleline | RegexOptions.IgnoreCase).Groups[1].Value).Trim();
                        var image = WebUtility.HtmlDecode(Regex.Match(text, "<img[^>]+src=\"([^\"]+)\"", RegexOptions.IgnoreCase).Groups[1].Value);
                        if (slug.Length == 0 || title.Length == 0 || version.Length == 0) continue;
                        if (_worldVersionValue.Length > 0 && !string.Equals(version, _worldVersionValue, StringComparison.OrdinalIgnoreCase)) continue;
                        if (_query.Length > 0 && title.IndexOf(_query, StringComparison.CurrentCultureIgnoreCase) < 0) continue;
                        if (image.StartsWith('/')) image = "https://mapmc.app" + image;
                        AddRow(new Entry(slug, title, "MapMC · Minecraft " + version, "mapmc", image, version), instance);
                    }
                    if (cards.Count == 0) throw new InvalidDataException("Не удалось прочитать каталог MapMC.");
                    _more = false;
                }
                else
                {
                    var result = await _owner._catalogClient.SearchModsAsync(_query, instance.BaseMinecraftVersion, "", ((_owner.CatalogSort.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "unpopular" ? "newest" : (_owner.CatalogSort.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "downloads"), (next - 1) * 20, cancellationToken: request.Token, projectType: kind);
                    if (request.IsCancellationRequested || request != _request) return;
                    foreach (var item in ((_owner.CatalogSort.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "unpopular" ? result.Hits.OrderBy(item => item.Downloads) : result.Hits.AsEnumerable())) AddRow(new Entry(item.Id, item.Title, item.Description, kind, item.IconUri ?? ""), instance);
                    _more = next * 20 < result.TotalHits;
                }
                _page = next; _status.Text = _worlds
                    ? $"{(_worldVersionValue.Length == 0 ? "Все версии Minecraft" : "Minecraft " + _worldVersionValue)} · найдено: {_items.Children.Count}"
                    : $"{instance.Name} · Minecraft {instance.BaseMinecraftVersion} · найдено: {_items.Children.Count}";
                if (_items.Children.Count == 0) _status.Text += " · ничего не найдено";
                if (!_worlds && kind == "shader") _status.Text += " · для шейдеров нужен Iris, Oculus или OptiFine";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (request == _request) _status.Text = "Не удалось загрузить каталог: " + ex.Message; }
            finally { _loading = false; if (request != _request && _request is not null) _ = LoadPageAsync(); }
        }
        private void AddRow(Entry entry, InstanceProfile instance)
        {
            var grid = new Grid { Margin = new Thickness(10, 7, 10, 7), MinHeight = 48 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var icon = new Border { Width = 44, Height = 44, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Color.FromRgb(0x27,0x34,0x3D)) };
            if (Uri.TryCreate(entry.Icon, UriKind.Absolute, out var imageUri) && imageUri.Scheme == Uri.UriSchemeHttps)
                icon.Child = new Image { Source = new BitmapImage(imageUri), Stretch = Stretch.UniformToFill };
            grid.Children.Add(icon);
            var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labels.Children.Add(new TextBlock { Text = entry.Title, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeights.Bold, FontSize = 14, Foreground = Brushes.White });
            labels.Children.Add(new TextBlock { Text = entry.Description, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = new SolidColorBrush(Color.FromRgb(0xB7,0xC7,0xD1)) });
            SetColumn(labels, 1); grid.Children.Add(labels);
            var button = new PixelDownloadButton { Content = "Установить", IsInstalled = IsInstalled(instance, entry), Margin = new Thickness(12,0,0,0) };
            if (button.IsInstalled) button.Content = "Установлено";
            button.Click += async (_, _) => await InstallAsync(instance, entry, button); SetColumn(button, 2); grid.Children.Add(button);
            _items.Children.Add(new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(0x36,0x47,0x50)), BorderThickness = new Thickness(0,0,0,1), Child = grid });
        }
        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        private static string MarkerDir(InstanceProfile instance) => Path.Combine(instance.GameDir, ".samara-content");
        private static string MarkerPath(InstanceProfile instance, Entry entry) => Path.Combine(MarkerDir(instance), entry.Kind + "-" + string.Concat(entry.Id.Where(char.IsLetterOrDigit)) + ".json");
        private static bool IsInstalled(InstanceProfile instance, Entry entry)
        {
            try { var path = MarkerPath(instance, entry); if (!File.Exists(path)) return false; var relative = File.ReadAllText(path); var full = Path.GetFullPath(Path.Combine(instance.GameDir, relative)); return full.StartsWith(Path.GetFullPath(instance.GameDir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && (File.Exists(full) || Directory.Exists(full)); } catch { return false; }
        }
        private async Task InstallAsync(InstanceProfile instance, Entry entry, PixelDownloadButton button)
        {
            if (_installing || button.IsInstalled) return;
            if (_owner._game is { IsRunning: true }) { _status.Text = "Закройте игру перед установкой."; return; }
            _installing = true; button.IsWorking = true; button.Progress = 0;
            _instances.IsEnabled = false; _kind.IsEnabled = false; _worldVersion.IsEnabled = false;
            var temp = Path.Combine(Path.GetTempPath(), "samara-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                string url, sha, filename;
                if (_worlds)
                {
                    if (!string.Equals(entry.Version, instance.BaseMinecraftVersion, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException($"Карта опубликована для Minecraft {entry.Version}. Выберите сборку этой версии.");
                    var page = await Http.GetStringAsync("https://mapmc.app/showcase/" + Uri.EscapeDataString(entry.Id));
                    var checksum = Regex.Match(page, @"SHA-256</dt>\s*<dd[^>]*>\s*([a-fA-F0-9]{64})\s*</dd>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (!checksum.Success) throw new InvalidOperationException("Источник не предоставил контрольную сумму карты.");
                    sha = checksum.Groups[1].Value;
                    url = "https://mapmc.app/api/world-tour/download/" + Uri.EscapeDataString(entry.Id) + "/java";
                    filename = entry.Id + ".zip";
                }
                else
                {
                    var versions = await _owner._catalogClient.GetProjectVersionsAsync(entry.Id);
                    var version = versions.Where(v => v.GameVersions.Contains(instance.BaseMinecraftVersion, StringComparer.OrdinalIgnoreCase) && v.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).OrderByDescending(v => v.DatePublished).FirstOrDefault();
                    if (version is null) throw new InvalidOperationException("Не найдена совместимая версия для этой сборки.");
                    url = version.DownloadUrl; sha = version.Sha512; filename = version.FileName;
                }
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https") throw new InvalidOperationException("Недопустимый адрес загрузки.");
                if (filename != Path.GetFileName(filename) || !filename.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Недопустимое имя файла.");
                int hashLength = _worlds ? 64 : 128;
                if (sha.Length != hashLength || !sha.All(Uri.IsHexDigit)) throw new InvalidOperationException("Источник не предоставил контрольную сумму.");
                _status.Text = "Загрузка: " + entry.Title;
                using (var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode(); var total = response.Content.Headers.ContentLength;
                    await using var source = await response.Content.ReadAsStreamAsync(); await using var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                    var buffer = new byte[81920]; long received = 0; int n;
                    while ((n = await source.ReadAsync(buffer)) > 0) { received += n; if (received > 2147483648L) throw new InvalidOperationException("Файл слишком большой."); await output.WriteAsync(buffer.AsMemory(0,n)); if (total is > 0) button.Progress = Math.Min(.9, .9 * received / total.Value); }
                }
                await using (var stream = File.OpenRead(temp))
                {
                    var hash = _worlds ? await SHA256.HashDataAsync(stream) : await SHA512.HashDataAsync(stream);
                    if (!string.Equals(Convert.ToHexString(hash), sha, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Контрольная сумма файла не совпала.");
                }
                string target;
                if (_worlds) target = await Task.Run(() => ExtractWorld(temp, instance, entry));
                else
                {
                    using var zip = ZipFile.OpenRead(temp);
                    bool valid = entry.Kind == "resourcepack" ? zip.Entries.Any(x => x.FullName.Equals("pack.mcmeta", StringComparison.OrdinalIgnoreCase)) : zip.Entries.Any(x => x.FullName.Replace('\\','/').StartsWith("shaders/", StringComparison.OrdinalIgnoreCase));
                    if (!valid) throw new InvalidOperationException("Архив не содержит ожидаемые файлы пакета.");
                    var directory = Path.Combine(instance.GameDir, entry.Kind == "shader" ? "shaderpacks" : "resourcepacks"); Directory.CreateDirectory(directory);
                    target = Path.Combine(directory, filename);
                    if (File.Exists(target) || Directory.Exists(target)) throw new InvalidOperationException("Файл с таким именем уже существует.");
                    File.Copy(temp, target, false);
                }
                Directory.CreateDirectory(MarkerDir(instance));
                File.WriteAllText(MarkerPath(instance,entry), Path.GetRelativePath(instance.GameDir,target));
                button.Progress = 1; button.IsInstalled = true; button.Content = "Установлено";
                _status.Text = entry.Title + " · установлено в " + instance.Name;
            }
            catch (Exception ex) { _status.Text = "Не удалось установить: " + ex.Message; }
            finally { button.IsWorking = false; _installing = false; _instances.IsEnabled = true; _kind.IsEnabled = true; _worldVersion.IsEnabled = true; try { File.Delete(temp); } catch (IOException) { } }
        }
        private static string ExtractWorld(string archive, InstanceProfile instance, Entry entry)
        {
            using var zip = ZipFile.OpenRead(archive);
            var level = zip.Entries.Where(x => x.FullName.Replace('\\','/').EndsWith("/level.dat", StringComparison.OrdinalIgnoreCase) || x.FullName.Equals("level.dat", StringComparison.OrdinalIgnoreCase)).ToList();
            if (level.Count != 1) throw new InvalidOperationException("В архиве должен быть один мир с level.dat.");
            var name = level[0].FullName.Replace('\\','/'); var prefix = name[..^"level.dat".Length];
            var saves = Path.Combine(instance.GameDir, "saves"); Directory.CreateDirectory(saves);
            var safeName = string.Concat(entry.Title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim().TrimEnd('.');
            if (string.IsNullOrWhiteSpace(safeName)) safeName = "Карта";
            if (safeName.Length > 80) safeName = safeName[..80];
            var target = Path.Combine(saves, safeName); int number = 2;
            while (Directory.Exists(target) || File.Exists(target)) target = Path.Combine(saves, safeName + " (" + number++ + ")");
            var stage = Path.Combine(saves, ".samara-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try
            {
                long total = 0; var root = Path.GetFullPath(stage) + Path.DirectorySeparatorChar;
                foreach (var item in zip.Entries)
                {
                    var path = item.FullName.Replace('\\','/'); if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length) continue;
                    var relative = path[prefix.Length..]; if (relative.StartsWith('/') || relative.Contains(':')) throw new InvalidDataException("Недопустимый путь в архиве.");
                    var output = Path.GetFullPath(Path.Combine(stage, relative)); if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Недопустимый путь в архиве.");
                    total += item.Length; if (total > 2147483648L) throw new InvalidDataException("Распакованная карта слишком большая.");
                    if (path.EndsWith('/')) { Directory.CreateDirectory(output); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!); item.ExtractToFile(output, false);
                }
                Directory.Move(stage, target); return target;
            }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
        }
        private sealed record Entry(string Id, string Title, string Description, string Kind, string Icon, string Version = "");
    }
}

