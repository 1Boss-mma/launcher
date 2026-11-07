using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows.Forms;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Tools;

namespace SamaraLauncher.Updater;

/// <summary>
/// Заменяет Launcher.exe после его завершения. Запускается лаунчером с аргументами
/// --pid, --source, --target и необязательным --restart.
/// </summary>
internal static class Program
{
    private const int WaitTimeoutMs = 60_000;
    private const int CopyAttempts = 30;
    private const int CopyRetryDelayMs = 500;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SamaraLauncher",
        "logs",
        "updater.log");

    [STAThread]
    private static int Main(string[] args)
    {
        Log("=== Launcher.Updater ===");
        Log("Args: " + string.Join(" ", args.Select(QuoteForLog)));

        try
        {
            var options = UpdaterArgs.Parse(args);
            Run(options);
            Log("Update completed successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Log("ERROR: " + ex);
            ShowError(ex);
            return 1;
        }
    }

    private static void Run(UpdaterArgs options)
    {
        var source = Path.GetFullPath(options.Source!);
        var target = Path.GetFullPath(options.Target!);

        Log("Source: " + source);
        Log("Target: " + target);

        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Файл обновления не найден.", source);
        }

        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Путь к обновлению совпадает с целевым файлом.");
        }

        var targetDir = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(targetDir))
        {
            throw new InvalidOperationException("Не удалось определить папку целевого файла.");
        }

        Directory.CreateDirectory(targetDir);

        WaitForProcess(options.Pid);
        BackupTarget(target);
        CopyWithRetry(source, target);

        if (options.Restart)
        {
            try
            {
                ProcessHost.ShellOpen(target, targetDir);
                Log("Started updated launcher.");
            }
            catch (LauncherException ex)
            {
                throw new InvalidOperationException(
                    "Лаунчер обновлён, но запустить его не удалось: " + ex.Message,
                    ex);
            }
        }

        TryDeleteSource(source);
    }

    private static void WaitForProcess(int? pid)
    {
        if (pid is null)
        {
            Log("No --pid, skipping wait.");
            return;
        }

        Process process;
        try
        {
            process = Process.GetProcessById(pid.Value);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            Log($"Process {pid} is not running: {ex.Message}");
            return;
        }

        using (process)
        {
            Log($"Waiting for process {pid} to exit (timeout {WaitTimeoutMs / 1000} s)...");

            try
            {
                if (process.WaitForExit(WaitTimeoutMs))
                {
                    Log($"Process {pid} exited.");
                }
                else
                {
                    Log($"WARN: process {pid} is still running after timeout; attempting copy anyway.");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
            {
                Log($"WARN: could not wait for process {pid}: {ex.Message}");
            }
        }
    }

    private static void BackupTarget(string target)
    {
        if (!File.Exists(target))
        {
            Log("Target does not exist; backup skipped.");
            return;
        }

        var backup = target + ".old";

        try
        {
            File.Copy(target, backup, overwrite: true);
            Log("Backup created: " + backup);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"WARN: could not create backup {backup}: {ex.Message}");
        }
    }

    private static void CopyWithRetry(string source, string target)
    {
        Exception? lastError = null;

        for (var attempt = 1; attempt <= CopyAttempts; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);

                var sourceLength = new FileInfo(source).Length;
                var targetLength = new FileInfo(target).Length;

                if (sourceLength != targetLength)
                {
                    throw new IOException(
                        $"Размер скопированного файла не совпадает: {targetLength} из {sourceLength} байт.");
                }

                Log($"Copied on attempt {attempt}: {source} -> {target} ({targetLength} bytes).");
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                lastError = ex;
                Log($"WARN: copy attempt {attempt}/{CopyAttempts} failed: {ex.Message}");

                if (attempt < CopyAttempts)
                {
                    Thread.Sleep(CopyRetryDelayMs);
                }
            }
        }

        throw new IOException(
            "Не удалось заменить файл лаунчера: он занят или недоступен.",
            lastError);
    }

    private static void TryDeleteSource(string source)
    {
        try
        {
            File.Delete(source);
            Log("Removed downloaded source: " + source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log($"WARN: could not remove source {source}: {ex.Message}");
        }
    }

    private static void ShowError(Exception ex)
    {
        var message =
            "Обновление лаунчера завершилось с ошибкой.\n\n" +
            ex.Message +
            "\n\nПодробности записаны в лог:\n" +
            LogPath;

        try
        {
            MessageBox.Show(
                message,
                "Лаунчер — обновление",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Нет интерактивного рабочего стола — остаётся только лог.
        }
    }

    private static void Log(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(LogPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(
                LogPath,
                $"[{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}] {message}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
        catch
        {
            // Логирование не должно ломать обновление.
        }
    }

    private static string QuoteForLog(string argument) =>
        argument.Contains(' ') ? "\"" + argument + "\"" : argument;

    private sealed class UpdaterArgs
    {
        public int? Pid { get; private set; }

        public string? Source { get; private set; }

        public string? Target { get; private set; }

        public bool Restart { get; private set; }

        public static UpdaterArgs Parse(string[] args)
        {
            var result = new UpdaterArgs();

            for (var i = 0; i < args.Length; i++)
            {
                var (name, inlineValue) = Split(args[i]);

                switch (name.ToLowerInvariant())
                {
                    case "--pid":
                    {
                        var value = inlineValue ?? TakeValue(args, ref i, name);

                        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid) ||
                            pid <= 0)
                        {
                            throw new ArgumentException("Некорректное значение --pid.");
                        }

                        result.Pid = pid;
                        break;
                    }

                    case "--source":
                        result.Source = inlineValue ?? TakeValue(args, ref i, name);
                        break;

                    case "--target":
                        result.Target = inlineValue ?? TakeValue(args, ref i, name);
                        break;

                    case "--restart":
                        result.Restart = true;
                        break;

                    default:
                        throw new ArgumentException($"Неизвестный аргумент: {args[i]}");
                }
            }

            if (string.IsNullOrWhiteSpace(result.Source))
            {
                throw new ArgumentException("Не указан --source.");
            }

            if (string.IsNullOrWhiteSpace(result.Target))
            {
                throw new ArgumentException("Не указан --target.");
            }

            return result;
        }

        private static string TakeValue(string[] args, ref int index, string name)
        {
            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Для {name} не указано значение.");
            }

            index++;
            return args[index];
        }

        private static (string Name, string? Value) Split(string argument)
        {
            var separator = argument.IndexOf('=');

            return separator > 0
                ? (argument[..separator], argument[(separator + 1)..])
                : (argument, null);
        }
    }
}
