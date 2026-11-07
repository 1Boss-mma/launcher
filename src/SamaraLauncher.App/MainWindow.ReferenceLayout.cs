using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Automation;
using SamaraLauncher.App.Controls;

namespace SamaraLauncher.App;

public partial class MainWindow
{
    private void ApplyReferenceLayout()
    {
        var titleBorder = (Border)RootGrid.Children[0];
        var main = (Grid)RootGrid.Children[1];
        main.ColumnDefinitions[0].Width = new GridLength(0);
        main.Children[0].Visibility = Visibility.Collapsed;
        var area = (Grid)PageScroll.Parent;
        var heading = (DockPanel)area.Children[0];
        var oldProfile = (Button)heading.Children[0];
        heading.Children.Remove(oldProfile);

        // The home scene fills the viewport independently of the scrolling pages.
        ((Panel)HomeView.Parent).Children.Remove(HomeView);
        Grid.SetRowSpan(HomeView, 2);
        area.Children.Add(HomeView);
        HomeView.MinHeight = 0;
        PageScroll.SizeChanged += (_, _) => HomeView.MinHeight = 0;

        var title = new Grid { Width = 1586, Height = 80 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(304) });
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(304) });
        var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(16,0,0,0), VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new PixelBlock { Width = 36, Height = 36 });
        var brandText = new StackPanel { Margin = new Thickness(12,0,0,0), VerticalAlignment = VerticalAlignment.Center };
        brandText.Children.Add(new TextBlock { Text = "Launcher", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White });
        brandText.Children.Add(new TextBlock { Text = AppVersion, FontSize = 12, Foreground = new SolidColorBrush(Color.FromRgb(145,159,168)), Margin = new Thickness(1,0,0,0), ToolTip = "Версия лаунчера" });
        brand.Children.Add(brandText);
        title.Children.Add(brand);
        var navigation = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(navigation, 1);
        var navStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="RadioButton">
              <Setter Property="Foreground" Value="#D6D7DC"/><Setter Property="FontFamily" Value="Segoe UI"/>
              <Setter Property="FontSize" Value="20"/><Setter Property="Height" Value="80"/><Setter Property="Padding" Value="18,0"/>
              <Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle" Value="{x:Null}"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
                <Border x:Name="Surface" Background="Transparent" CornerRadius="0" BorderThickness="0,0,0,4" BorderBrush="Transparent" Padding="{TemplateBinding Padding}">
                  <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsChecked" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="#509C39"/><Setter Property="Foreground" Value="White"/></Trigger>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#254535"/></Trigger>
                  <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="BorderBrush" Value="#72B862"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        foreach (var entry in new[] { ("home","Главная"), ("instances","Сборки"), ("worlds","Миры"), ("servers","Серверы"), ("workshop","Мастерская") })
        {
            var radio = _workspaceNav[entry.Item1];
            ((Panel)radio.Parent).Children.Remove(radio);
            radio.Style = navStyle;
            radio.MinHeight = 0;
            radio.Margin = new Thickness(0);
            var label = new TextBlock { Text = entry.Item2, FontFamily = new FontFamily("Segoe UI"), FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            label.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { Source = radio });
            var navContent = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var kind = entry.Item1 switch { "home" => LauncherGlyph.Home, "instances" => LauncherGlyph.Builds, "worlds" => LauncherGlyph.Worlds, "servers" => LauncherGlyph.Servers, _ => LauncherGlyph.Workshop };
            var icon = new LauncherIcon { Kind = kind, Width = 26, Height = 26, Margin = new Thickness(0,0,12,0), VerticalAlignment = VerticalAlignment.Center };
            icon.SetBinding(LauncherIcon.StrokeProperty, new Binding("Foreground") { Source = radio });
            navContent.Children.Add(icon);
            navContent.Children.Add(label);
            radio.Content = navContent;
            AutomationProperties.SetName(radio, entry.Item2);
            navigation.Children.Add(radio);
        }
        title.Children.Add(navigation);
        var right = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetColumn(right, 2);
        var profileLine = new StackPanel { Orientation = Orientation.Horizontal };
        var avatar = new PixelAvatar { Width = 52, Height = 52 };
        avatar.SetBinding(PixelAvatar.UsernameProperty, new Binding("Text") { Source = NavNickText });
        var profile = new Button { Content = avatar, Style = (Style)FindResource("HomeSelectorButton"), Width = 56, Height = 56, Padding = new Thickness(0), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Профиль" };
        AutomationProperties.SetName(profile, "Открыть профиль");
        profile.Click += (_, _) => NavigateWorkspace("profile");
        profileLine.Children.Add(profile);
        var nickHost = new Grid { Margin = new Thickness(12,0,0,0), VerticalAlignment = VerticalAlignment.Center, Width = 100 };
        ((Panel)NavNickText.Parent).Children.Remove(NavNickText);
        ((Panel)NavNickEdit.Parent).Children.Remove(NavNickEdit);
        NavNickText.FontSize = 22;
        NavNickText.FontWeight = FontWeights.Normal;
        NavNickText.Foreground = Brushes.White;
        NavNickText.TextTrimming = TextTrimming.CharacterEllipsis;
        NavNickText.VerticalAlignment = VerticalAlignment.Center;
        NavNickText.ToolTip = "Изменить ник";
        NavNickEdit.FontSize = 20;
        NavNickEdit.Height = 36;
        NavNickEdit.MinWidth = 0;
        NavNickEdit.Padding = new Thickness(4,0,4,0);
        AutomationProperties.SetName(NavNickEdit, "Ник игрока");
        nickHost.Children.Add(NavNickText);
        nickHost.Children.Add(NavNickEdit);
        profileLine.Children.Add(nickHost);
        profileLine.Margin = new Thickness(0,0,16,0);
        right.Children.Add(profileLine);
        foreach (var (glyph, hint, action) in new (LauncherGlyph,string,Action)[] {
            (LauncherGlyph.Minimize, "Свернуть", () => WindowState = WindowState.Minimized),
            (LauncherGlyph.Close, "Закрыть", Close) })
        {
            var button = new Button { Content = new LauncherIcon { Kind = glyph, Width = 24, Height = 24 }, Width = 54, Height = 80, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(0), BorderThickness = new Thickness(0), Style = (Style)FindResource("HomeSelectorButton"), ToolTip = hint };
            AutomationProperties.SetName(button, hint);
            button.Click += (_, _) => action();
            right.Children.Add(button);
        }
        title.Children.Add(right);
        // Fill both axes: uniform scaling leaves blank strips at the window edges
        // when the bottom titlebar border reduces its available height.
        titleBorder.Child = new Viewbox { Child = title, Stretch = Stretch.Fill };
        titleBorder.Background = new LinearGradientBrush(Color.FromRgb(16,26,33), Color.FromRgb(10,18,24), 90);
        titleBorder.BorderThickness = new Thickness(0,0,0,1);
        titleBorder.BorderBrush = Brushes.Black;

        var launch = (Border)HomeView.Children[0];
        launch.Width = 656;
        launch.MaxWidth = 656;
        launch.HorizontalAlignment = HorizontalAlignment.Center;
        launch.VerticalAlignment = VerticalAlignment.Bottom;
        launch.Padding = new Thickness(16,14,16,14);
        launch.CornerRadius = new CornerRadius(22);
        launch.BorderThickness = new Thickness(2);
        launch.BorderBrush = new SolidColorBrush(Color.FromRgb(69,82,92));
        launch.Background = new SolidColorBrush(Color.FromArgb(235,17,30,37));
        HomeOperationStrip.Resources["TextBrush"] = Brushes.White;
        var launchRow = (Grid)((StackPanel)launch.Child).Children[0];
        launchRow.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        launchRow.ColumnDefinitions[1].Width = new GridLength(200);
        HomeBuildButton.Margin = new Thickness(0,0,20,0);
        HomeBuildButton.Height = 58;
        HomeBuildButton.Padding = new Thickness(0,0,12,0);
        HomeBuildButton.Style = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Foreground" Value="White"/><Setter Property="Cursor" Value="Hand"/><Setter Property="FocusVisualStyle"><Setter.Value><Style TargetType="Control"><Setter Property="Template"><Setter.Value><ControlTemplate><Border BorderThickness="0,0,0,2" BorderBrush="{DynamicResource AccentBrush}" Margin="4"/></ControlTemplate></Setter.Value></Setter></Style></Setter.Value></Setter>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                <Border x:Name="Surface" CornerRadius="10" Background="Transparent" BorderBrush="Transparent" BorderThickness="2" Padding="{TemplateBinding Padding}">
                  <ContentPresenter VerticalAlignment="Center"/>
                </Border>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#26352F"/></Trigger>

                  <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value=".6"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        var selected = (Grid)HomeBuildButton.Content;
        selected.Children[0].Visibility = Visibility.Visible;
        selected.ColumnDefinitions[0].Width = new GridLength(46);
        ((StackPanel)SelectedInstanceName.Parent).Margin = new Thickness(0,0,8,0);
        SelectedInstanceName.FontFamily = new FontFamily("Segoe UI");
        SelectedInstanceName.FontSize = 23;
        SelectedInstanceName.FontWeight = FontWeights.SemiBold;
        SelectedInstanceName.Foreground = Brushes.White;
        SelectedInstanceMeta.Visibility = Visibility.Collapsed;
        HomeBuildArrow.Foreground = Brushes.White;
        HomeBuildArrow.Text = "";
        HomeBuildArrow.Visibility = Visibility.Collapsed;
        var chevron = new LauncherIcon { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Kind = LauncherGlyph.ChevronDown };
        Grid.SetColumn(chevron, 2);
        selected.Children.Add(chevron);
        HomeBuildPopup.Opened += (_, _) => chevron.Kind = LauncherGlyph.ChevronUp;
        HomeBuildPopup.Closed += (_, _) => chevron.Kind = LauncherGlyph.ChevronDown;
        var divider = new Border { Width = 1, Background = new SolidColorBrush(Color.FromRgb(50,75,82)), HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,2,9.5,2), IsHitTestVisible = false };
        launchRow.Children.Add(divider);
        PlayButton.Height = 60;
        PlayButton.FontFamily = new FontFamily("Segoe UI");
        PlayButton.FontSize = 25;
        PlayButton.IconSize = 20;
        PlayButton.ReferenceChrome = true;

        HomeBuildPopupSurface.SetBinding(FrameworkElement.WidthProperty, new System.Windows.Data.Binding("ActualWidth") { Source = HomeBuildButton });
        HomeBuildPopupSurface.Padding = new Thickness(0);
        HomeBuildPopupSurface.Margin = new Thickness(0,0,0,8);
        HomeBuildPopupSurface.CornerRadius = new CornerRadius(10);
        HomeBuildPopupSurface.BorderThickness = new Thickness(3);
        HomeBuildPopupSurface.BorderBrush = new SolidColorBrush(Color.FromRgb(64,73,80));
        HomeBuildPopupSurface.Background = new SolidColorBrush(Color.FromArgb(245,24,32,39));
        HomeBuildPopupSurface.Resources["Panel2Brush"] = new SolidColorBrush(Color.FromRgb(38,53,47));
        HomeBuildPopupSurface.Resources["TextBrush"] = Brushes.White;
        HomeBuildPopupSurface.Resources["MutedBrush"] = new SolidColorBrush(Color.FromRgb(174,187,193));
        HomeBuildPopupSurface.ClipToBounds = true;
        VersionCatalogList.MaxHeight = 288;
        // Keep the creation action visible while the version catalog scrolls.
        var addBuild = new Button { Content = new TextBlock { Text = "+  Добавить сборку", FontSize = 20, Foreground = Brushes.White }, Style = (Style)FindResource("HomeSelectorButton"), Padding = new Thickness(18,10,18,10), HorizontalContentAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(addBuild, "Добавить сборку");
        addBuild.Click += (_, _) => { SetQuickBuildOpen(false); OpenInstancesOverlay(); };
        ((StackPanel)HomeBuildPopupSurface.Child).Children.Add(new Border { BorderThickness = new Thickness(0,1,0,0), BorderBrush = launch.BorderBrush, Child = addBuild });

        var settings = new Button { Content = new LauncherIcon { Kind = LauncherGlyph.Settings, Width = 27, Height = 27 }, Width = 50, Height = 50, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, ToolTip = "Настройки" };
        settings.Style = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Cursor" Value="Hand"/><Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
                <Border x:Name="Surface" Background="#ED24313C" CornerRadius="10" BorderThickness="2" BorderBrush="#45525C"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border>
                <ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Background" Value="#394C59"/></Trigger><Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="Surface" Property="Background" Value="#26352F"/></Trigger></ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        AutomationProperties.SetName(settings, "Настройки");
        settings.Click += (_, _) => NavigateWorkspace("settings");
        HomeView.Children.Add(settings);
        _homeUpdateButton = new Button
        {
            Content = new LauncherIcon { Kind = LauncherGlyph.Download, Width = 24, Height = 24 },
            Width = 50, Height = 50, Style = settings.Style,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = Visibility.Collapsed,
            ToolTip = "Скачать и установить обновление"
        };
        AutomationProperties.SetName(_homeUpdateButton, "Скачать и установить обновление лаунчера");
        _homeUpdateButton.Click += InstallLauncherUpdate_Click;
        HomeView.Children.Add(_homeUpdateButton);
        void ResizeReference()
        {
            var scale = ActualWidth > 0 ? ActualWidth / 1586d : Width / 1586d;
            RootGrid.RowDefinitions[0].Height = new GridLength(80 * scale);
            launch.LayoutTransform = new ScaleTransform(scale, scale);
            launch.Margin = new Thickness(0,0,0,36 * scale);
            settings.LayoutTransform = new ScaleTransform(scale, scale);
            settings.Margin = new Thickness(0,0,30 * scale,36 * scale);
            _homeUpdateButton.LayoutTransform = new ScaleTransform(scale, scale);
            _homeUpdateButton.Margin = new Thickness(0,0,30 * scale,94 * scale);
            HomeBuildPopupSurface.LayoutTransform = new ScaleTransform(scale, scale);
        }
        SizeChanged += (_, _) => ResizeReference();
        ResizeReference();
    }
}








