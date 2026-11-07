using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Запущенный процесс Minecraft с построчным чтением stdout/stderr и записью в лог.
/// Ограничение: Java пишет в перенаправленные pipe, поэтому при закрытии лаунчера
/// процесс игры завершается в <see cref="Dispose"/> — иначе игра может зависнуть
/// на записи в закрытый pipe. Полноценное «игра живёт после закрытия лаунчера»
/// требует передачи наследуемого файлового дескриптора и здесь не реализовано.
/// </summary>
public sealed class GameProcess : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter? _logWriter;
    private readonly object _logLock = new();

    private const int FlushIntervalMs = 1000;
    private const int FlushPendingLines = 50;

    private int _exitCode;
    private int _hasExitCode;
    private int _exitedRaised;
    private int _disposed;
    private long _lastFlushTicks;
    private int _pendingLines;

    internal GameProcess(ProcessStartInfo startInfo, string? logPath)
    {
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            var fullLogPath = Path.GetFullPath(logPath);
            var logDir = Path.GetDirectoryName(fullLogPath);
            if (!string.IsNullOrWhiteSpace(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            var stream = new FileStream(fullLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _logWriter = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false };
            _lastFlushTicks = Environment.TickCount64;
        }

        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };

        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnErrorDataReceived;
        _process.Exited += OnProcessExited;

        try
        {
            if (!_process.Start())
            {
                throw new LauncherException("Не удалось запустить процесс Java.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            CleanupAfterFailedStart();
            throw new LauncherException($"Не удалось запустить Java ({startInfo.FileName}): {ex.Message}", ex);
        }

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>Вызывается для каждой строки stdout/stderr.</summary>
    public event Action<LogLine>? LineReceived;

    /// <summary>Вызывается один раз после завершения процесса.</summary>
    public event Action<int>? Exited;

    public int Id => _process.Id;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public DateTime StartTime
    {
        get
        {
            try
            {
                return _process.StartTime;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                return DateTime.MinValue;
            }
        }
    }

    public int ExitCode
    {
        get
        {
            if (Volatile.Read(ref _hasExitCode) != 0)
            {
                return _exitCode;
            }

            try
            {
                return _process.ExitCode;
            }
            catch (ObjectDisposedException)
            {
                return Volatile.Read(ref _hasExitCode) != 0 ? _exitCode : -1;
            }
            catch (InvalidOperationException)
            {
                return -1;
            }
        }
    }

    /// <summary>Завершает процесс вместе с дочерними процессами Java.</summary>
    public void Kill(bool entireProcessTree = true)
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Процесс уже завершился или нет прав — ждать больше нечего.
        }
    }

    /// <summary>
    /// Ждёт завершения процесса. При отмене токена процесс не убивается — это решает вызывающий.
    /// </summary>
    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        await _process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        int exitCode;

        try
        {
            // WaitForExit(5000) гарантирует, что асинхронные обработчики stdout/stderr
            // успели дописать последние строки, но не может зависнуть навсегда.
            _process.WaitForExit(5000);
            exitCode = _process.ExitCode;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or Win32Exception
                or NotSupportedException)
        {
            exitCode = Volatile.Read(ref _hasExitCode) != 0 ? _exitCode : -1;
        }

        FlushLog();
        SetExitCode(exitCode);
        RaiseExited(exitCode);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _process.OutputDataReceived -= OnOutputDataReceived;
        _process.ErrorDataReceived -= OnErrorDataReceived;
        _process.Exited -= OnProcessExited;

        // Ограничение: Java пишет в перенаправленные pipes, поэтому после закрытия
        // лаунчера их нельзя просто оставить — игра может зависнуть на записи.
        // Если игра ещё работает, завершаем её дерево процессов.
        if (IsRunning)
        {
            Kill(entireProcessTree: true);
            try
            {
                _process.WaitForExit(5000);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or Win32Exception)
            {
                // Процесс уже завершился.
            }
        }

        FlushLog();

        lock (_logLock)
        {
            try
            {
                _logWriter?.Dispose();
            }
            catch (IOException)
            {
                // Файл уже закрыт.
            }
        }

        try
        {
            _process.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Повторный Dispose безопасен.
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
        {
            Emit(new LogLine(DateTimeOffset.Now, e.Data));
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data is not null)
        {
            Emit(new LogLine(DateTimeOffset.Now, e.Data, IsError: true));
        }
    }

    private void Emit(LogLine line)
    {
        if (_logWriter is not null)
        {
            lock (_logLock)
            {
                try
                {
                    var prefix = line.IsError ? "[stderr] " : "";
                    _logWriter.WriteLine($"[{line.Timestamp:yyyy-MM-dd HH:mm:ss.fff}] {prefix}{line.Text}");
                    _pendingLines++;

                    var now = Environment.TickCount64;
                    if (_pendingLines >= FlushPendingLines || now - _lastFlushTicks >= FlushIntervalMs)
                    {
                        _logWriter.Flush();
                        _pendingLines = 0;
                        _lastFlushTicks = now;
                    }
                }
                catch (IOException)
                {
                    // Диск закончился или файл закрыт — игру из-за лога не останавливаем.
                }
                catch (ObjectDisposedException)
                {
                    // Лог уже закрыт при завершении лаунчера.
                }
            }
        }

        var handlers = LineReceived;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<LogLine>>())
        {
            try
            {
                handler(line);
            }
            catch
            {
                // Ошибка подписчика не должна ломать чтение вывода игры.
            }
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        int exitCode;

        try
        {
            exitCode = _process.ExitCode;
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or Win32Exception)
        {
            exitCode = -1;
        }

        SetExitCode(exitCode);

        // WaitForExit() внутри Exited может заблокироваться на перенаправленных
        // потоках и уронить поток пула. Дожидаемся чтения вывода в отдельной задаче.
        _ = Task.Run(() =>
        {
            try
            {
                _process.WaitForExit(5000);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or Win32Exception
                    or NotSupportedException)
            {
                // Dispose уже закрыл процесс.
            }

            FlushLog();
            RaiseExited(exitCode);
        });
    }

    private void FlushLog()
    {
        if (_logWriter is null)
        {
            return;
        }

        lock (_logLock)
        {
            try
            {
                _logWriter.Flush();
                _pendingLines = 0;
                _lastFlushTicks = Environment.TickCount64;
            }
            catch (IOException)
            {
                // Диск закончился или файл закрыт.
            }
            catch (ObjectDisposedException)
            {
                // Лог уже закрыт при завершении лаунчера.
            }
        }
    }

    private void SetExitCode(int exitCode)
    {
        _exitCode = exitCode;
        Volatile.Write(ref _hasExitCode, 1);
    }

    private void RaiseExited(int exitCode)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _exitedRaised, 1) != 0)
        {
            return;
        }

        var handlers = Exited;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<int>>())
        {
            try
            {
                handler(exitCode);
            }
            catch
            {
                // Ошибка подписчика не должна ронять обработчик процесса.
            }
        }
    }

    private void CleanupAfterFailedStart()
    {
        _process.OutputDataReceived -= OnOutputDataReceived;
        _process.ErrorDataReceived -= OnErrorDataReceived;
        _process.Exited -= OnProcessExited;

        lock (_logLock)
        {
            _logWriter?.Dispose();
        }

        _process.Dispose();
    }
}
