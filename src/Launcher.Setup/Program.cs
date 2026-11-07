using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Launcher.Setup;

internal static class Program
{
    internal const string Marker = ".launcher-install";
    internal const string ProductId = "Launcher-9ed546d7";
    internal const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductId;
    internal static string Version => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    [STAThread]
    private static int Main(string[] args)
    {
        // Extraction validates the exact payload without touching shortcuts, registry or player data.
        if (args.Length == 2 && args[0] == "--extract")
        {
            try { Extract(args[1]); return 0; }
            catch { return 1; }
        }
        var app = new System.Windows.Application();
        if (args.Length == 3 && args[0] == "--remove")
        {
            try
            {
                using var parent = Process.GetProcessById(int.Parse(args[2]));
                if (!parent.WaitForExit(30000)) throw new IOException("Закройте окно удаления и повторите.");
            }
            catch (ArgumentException) { }
            try { Remove(args[1]); MessageBox.Show("Launcher удалён. Сборки, миры и настройки сохранены.", "Launcher"); return 0; }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Не удалось удалить Launcher"); return 1; }
        }
        bool uninstall = args.Contains("--uninstall");
        return app.Run(new Wizard(uninstall));
    }

    internal static ZipArchive OpenPayload() => new(
        Assembly.GetExecutingAssembly().GetManifestResourceStream("Launcher.Payload.zip")
            ?? throw new IOException("Установочные файлы отсутствуют."), ZipArchiveMode.Read);

    internal static void Extract(string destination)
    {
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(root);
        using var archive = OpenPayload();
        foreach (var entry in archive.Entries)
        {
            var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Некорректный путь в пакете.");
            if (entry.Name.Length == 0) { Directory.CreateDirectory(path); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    internal static void EnsureLauncherClosed()
    {
        using var mutex = new Mutex(false, "SamaraLauncher.SingleInstance");
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("Сначала закройте Launcher, затем повторите установку.");
        mutex.ReleaseMutex();
    }

    internal static string Install(string destination, bool desktop, IProgress<string> progress)
    {
        EnsureLauncherClosed();
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination.Trim()));
        if (root == Path.GetPathRoot(root) || root.StartsWith(@"\\"))
            throw new IOException("Выберите отдельную локальную папку для Launcher.");
        if (Directory.Exists(root) && Directory.EnumerateFileSystemEntries(root).Any())
        {
            if (!File.Exists(Path.Combine(root, Marker)) || File.ReadAllText(Path.Combine(root, Marker)) != ProductId)
                throw new IOException("Папка не пуста. Выберите новую папку для установки Launcher.");
        }
        var stage = root + ".install-" + Guid.NewGuid().ToString("N");
        var backup = root + ".backup-" + Guid.NewGuid().ToString("N");
        bool replaced = false;
        try
        {
            progress.Report("Распаковка файлов приложения…");
            Extract(stage);
            File.WriteAllText(Path.Combine(stage, Marker), ProductId);
            File.Copy(Environment.ProcessPath!, Path.Combine(stage, "Uninstall.exe"));
            progress.Report("Установка приложения…");
            if (Directory.Exists(root)) Directory.Move(root, backup);
            try { Directory.Move(stage, root); replaced = true; }
            catch { if (Directory.Exists(backup)) Directory.Move(backup, root); throw; }
            progress.Report("Создание ярлыков…");
            CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Launcher.lnk"), root);
            if (desktop) CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Запустить лаунчер.lnk"), root);
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key.SetValue("DisplayName", "Launcher");
            key.SetValue("DisplayVersion", Version);
            key.SetValue("InstallLocation", root);
            key.SetValue("DisplayIcon", Path.Combine(root, "Launcher.exe"));
            key.SetValue("UninstallString", "\"" + Path.Combine(root, "Uninstall.exe") + "\" --uninstall");
            key.SetValue("NoModify", 1);
            key.SetValue("NoRepair", 1);
            return root;
        }
        catch
        {
            if (replaced)
            {
                // Only the directory just created by this installation is rolled back.
                DeletePayload(root);
                if (Directory.Exists(backup) && !Directory.Exists(root)) Directory.Move(backup, root);
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(stage)) DeletePayload(stage);
            // A previous installation may contain personal additions: keep its backup.
            if (Directory.Exists(backup)) progress.Report("Предыдущая установка сохранена: " + backup);
        }
    }

    private static void CreateShortcut(string path, string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(path);
            try
            {
                shortcut.TargetPath = Path.Combine(root, "Launcher.exe");
                shortcut.WorkingDirectory = root;
                shortcut.IconLocation = Path.Combine(root, "Launcher.exe") + ",0";
                shortcut.Save();
            }
            finally { Marshal.FinalReleaseComObject(shortcut); }
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    internal static void StartUninstall()
    {
        var root = AppContext.BaseDirectory;
        if (!File.Exists(Path.Combine(root, Marker)) || File.ReadAllText(Path.Combine(root, Marker)) != ProductId)
            throw new IOException("Не найдена установленная копия Launcher.");
        var helper = Path.Combine(Path.GetTempPath(), "Launcher-remove-" + Guid.NewGuid().ToString("N") + ".exe");
        File.Copy(Environment.ProcessPath!, helper);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--remove"); start.ArgumentList.Add(root); start.ArgumentList.Add(Environment.ProcessId.ToString());
        Process.Start(start);
    }

    private static void Remove(string root)
    {
        EnsureLauncherClosed();
        if (!File.Exists(Path.Combine(root, Marker)) || File.ReadAllText(Path.Combine(root, Marker)) != ProductId)
            throw new IOException("Неверная папка установки.");
        DeletePayload(root);
        Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);
        foreach (var path in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Launcher.lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Запустить лаунчер.lnk") })
        {
            if (!File.Exists(path)) continue;
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            try
            {
                dynamic link = shell.CreateShortcut(path);
                try { if (string.Equals((string)link.TargetPath, Path.Combine(root, "Launcher.exe"), StringComparison.OrdinalIgnoreCase)) File.Delete(path); }
                finally { Marshal.FinalReleaseComObject(link); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
    }

    private static void DeletePayload(string directory)
    {
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        using var zip = OpenPayload();
        foreach (var name in zip.Entries.Where(e => e.Name.Length > 0).Select(e => e.FullName)
                     .Concat(new[] { Marker, "Uninstall.exe", "Launcher.exe.old" }))
        {
            var path = Path.GetFullPath(Path.Combine(root, name));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Некорректный путь удаления.");
            File.Delete(path);
            var parent = Path.GetDirectoryName(path);
            while (parent is not null && parent.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                   Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            { Directory.Delete(parent); parent = Path.GetDirectoryName(parent); }
        }
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}

internal sealed class Wizard : Window
{
    private readonly StackPanel _body = new() { Margin = new Thickness(36, 28, 36, 24) };
    private readonly TextBlock _heading = new() { FontSize = 25, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,14) };
    private readonly TextBlock _description = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23, Foreground = new SolidColorBrush(Color.FromRgb(169,186,195)) };
    private readonly Button _next = new() { MinWidth = 160, Height = 42, Content = "Начать установку", Margin = new Thickness(12,0,0,0) };
    private readonly Button _cancel = new() { MinWidth = 100, Height = 42, Content = "Отмена" };
    private readonly TextBox _path = new() { Height = 38, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10,0,10,0) };
    private readonly CheckBox _desktop = new() { Content = "Создать ярлык на рабочем столе", IsChecked = true, Margin = new Thickness(0,20,0,0) };
    private int _step;
    private bool _installing;
    private string? _installed;
    private readonly bool _uninstall;

    internal Wizard(bool uninstall)
    {
        _uninstall = uninstall;
        Title = uninstall ? "Удаление Launcher" : "Установка Launcher";
        Width = 720; Height = 520; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush(Color.FromRgb(16,24,32)); Foreground = Brushes.White;
        FontFamily = new FontFamily("Segoe UI");
        Resources[typeof(Button)] = System.Windows.Markup.XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Button">
              <Setter Property="Foreground" Value="White"/><Setter Property="Background" Value="#263640"/><Setter Property="FontSize" Value="14"/><Setter Property="Cursor" Value="Hand"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button"><Border x:Name="Surface" Background="{TemplateBinding Background}" Padding="18,8" CornerRadius="5"><ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/></Border><ControlTemplate.Triggers><Trigger Property="IsMouseOver" Value="True"><Setter TargetName="Surface" Property="Opacity" Value=".8"/></Trigger><Trigger Property="IsEnabled" Value="False"><Setter TargetName="Surface" Property="Opacity" Value=".45"/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter>
            </Style>
            """);
        _next.Background = new SolidColorBrush(Color.FromRgb(60,125,47));
        var grid = new Grid(); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(90) }); grid.RowDefinitions.Add(new RowDefinition()); grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(86) });
        var header = new Border { Background = new SolidColorBrush(Color.FromRgb(27,41,51)), Padding = new Thickness(36,18,36,18) };
        using (var scene = Assembly.GetExecutingAssembly().GetManifestResourceStream("Launcher.Setup.Scene.png"))
        {
            if (scene is not null)
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = scene; bitmap.EndInit(); bitmap.Freeze();
                header.Background = new ImageBrush(bitmap) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Center, Opacity = .6 };
            }
        }
        _path.Background = new SolidColorBrush(Color.FromRgb(27,41,51));
        _path.Foreground = Brushes.White; _path.BorderBrush = new SolidColorBrush(Color.FromRgb(64,81,93));
        _desktop.Foreground = new SolidColorBrush(Color.FromRgb(237,244,245));
        var brand = new StackPanel(); brand.Children.Add(new TextBlock { Text = "LAUNCHER", FontSize = 24, FontWeight = FontWeights.SemiBold });
        brand.Children.Add(new TextBlock { Text = "Minecraft · версия " + Program.Version, FontSize = 12, Foreground = _description.Foreground, Margin = new Thickness(0,5,0,0) });
        header.Child = brand; grid.Children.Add(header);
        _body.Children.Add(_heading); _body.Children.Add(_description); Grid.SetRow(_body,1); grid.Children.Add(_body);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(36,20,36,24) };
        footer.Children.Add(_cancel); footer.Children.Add(_next); Grid.SetRow(footer,2); grid.Children.Add(footer); Content = grid;
        _cancel.Click += (_,_) => Close(); _next.Click += Next;
        Closing += (_,e) => { if (_installing) e.Cancel = true; };
        _path.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Launcher");
        _heading.Text = uninstall ? "Удаление приложения" : "Всё готово для вашей игры";
        _description.Text = uninstall ? "Будут удалены только файлы Launcher и его ярлыки. Ваши сборки, миры, моды и настройки останутся на компьютере." : "Мастер установит Launcher на этот компьютер.\n\nВыберите сборку, добавляйте моды и запускайте Minecraft из одного окна. Java и файлы игры загружаются при установке выбранной сборки.\n\nПрава администратора и отдельная установка .NET не требуются.";
        if (uninstall) _next.Content = "Удалить Launcher";
    }

    private async void Next(object sender, RoutedEventArgs e)
    {
        if (_uninstall)
        {
            try { Program.StartUninstall(); Close(); } catch (Exception ex) { _description.Text = ex.Message; }
            return;
        }
        if (_step == 0)
        {
            _step = 1; _heading.Text = "Место установки";
            _description.Text = "Укажите отдельную папку для приложения. Игровые сборки и настройки хранятся отдельно и сохраняются при обновлении и удалении.";
            var row = new Grid { Margin = new Thickness(0,24,0,0) };
            row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.Children.Add(_path);
            var browse = new Button { Content = "Обзор…", Margin = new Thickness(8,0,0,0) }; Grid.SetColumn(browse,1); row.Children.Add(browse);
            browse.Click += (_,_) => { using var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = "Папка установки Launcher", SelectedPath = _path.Text }; if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) _path.Text = dialog.SelectedPath; };
            _body.Children.Add(row); _body.Children.Add(_desktop); _next.Content = "Установить"; return;
        }
        if (_step == 2)
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(_installed!, "Launcher.exe")) { UseShellExecute = true, WorkingDirectory = _installed }); Close(); }
            catch (Exception ex) { _description.Text = ex.Message; }
            return;
        }
        _installing = true; _next.IsEnabled = false; _cancel.IsEnabled = false;
        _path.IsEnabled = false; _desktop.IsEnabled = false;
        var bar = new ProgressBar { IsIndeterminate = true, Height = 5, Margin = new Thickness(0,24,0,0), Foreground = _next.Background }; _body.Children.Add(bar);
        try
        {
            var path = _path.Text; var desktop = _desktop.IsChecked == true;
            _installed = await Task.Run(() => Program.Install(path, desktop, new Progress<string>(s => Dispatcher.Invoke(() => _description.Text = s))));
            _step = 2; _heading.Text = "Launcher установлен"; _description.Text = "Можно начинать.\n\nПри появлении новой версии кнопка скачивания отобразится на главной над настройками.\n\nПапка приложения: " + _installed;
            _next.Content = "Запустить Launcher"; _cancel.Content = "Готово";
        }
        catch (Exception ex) { _heading.Text = "Установка не завершена"; _description.Text = ex.Message; _path.IsEnabled = true; _desktop.IsEnabled = true; }
        finally { _body.Children.Remove(bar); _installing = false; _next.IsEnabled = true; _cancel.IsEnabled = true; }
    }
}
