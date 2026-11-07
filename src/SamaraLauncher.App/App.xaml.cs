using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SamaraLauncher.App;

public partial class App : Application
{
    private const string SingleInstanceMutexName = "SamaraLauncher.SingleInstance";

    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SamaraLauncher",
        "Logs",
        "launcher-ui.log");

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        StartupTrace("OnStartup begin");

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            _singleInstanceMutex = new Mutex(
                initiallyOwned: true,
                SingleInstanceMutexName,
                out var createdNew);
            _ownsSingleInstanceMutex = createdNew;

            if (!createdNew)
            {
                MessageBox.Show(
                    "Лаунчер уже запущен.\n\nЗакройте другой экземпляр и попробуйте снова.",
                    "Launcher",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                Shutdown();
                return;
            }
        }
        catch (Exception ex)
        {
            // Если named mutex недоступен, не мешаем обычному запуску.
            WriteLog(ex);
        }

        var window = new MainWindow();
        StartupTrace("MainWindow constructed");

        if (window.StartupBlocked)
        {
            StartupTrace("MainWindow startup blocked");
            Shutdown();
            return;
        }

        MainWindow = window;
        window.Show();
        StartupTrace("MainWindow shown");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsSingleInstanceMutex)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Мьютекс уже освобождён — выходим штатно.
            }

            _ownsSingleInstanceMutex = false;
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);
    }

    internal static void StartupTrace(string message)
    {
        if (Environment.GetEnvironmentVariable("LAUNCHER_TRACE") != "1")
        {
            return;
        }

        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SamaraLauncher",
                "Logs");
            Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "startup.log"),
                $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Трассировка не должна мешать запуску.
        }
    }

    private static void OnDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog(e.Exception);
        MessageBox.Show(
            "Лаунчер столкнулся с ошибкой:\n\n" + e.Exception.Message +
            "\n\nПодробности записаны в:\n" + CrashLogPath,
            "Launcher",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteLog(exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteLog(e.Exception);
        e.SetObserved();
    }

    private static void WriteLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            var builder = new StringBuilder()
                .AppendLine()
                .AppendLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] {exception.GetType().FullName}")
                .AppendLine(exception.Message)
                .AppendLine(exception.StackTrace);

            if (exception.InnerException is not null)
            {
                builder.AppendLine("--- inner ---")
                    .AppendLine(exception.InnerException.ToString());
            }

            File.AppendAllText(CrashLogPath, builder.ToString());
        }
        catch
        {
            // Логирование не должно ронять приложение.
        }
    }
}
