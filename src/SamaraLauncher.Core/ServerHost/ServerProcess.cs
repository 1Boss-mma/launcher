using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.ServerHost;

/// <summary>
/// Запущенный процесс Minecraft-сервера с построчным чтением stdout/stderr,
/// записью в лог и отправкой команд в stdin.
/// Ограничение: Java пишет в перенаправленные pipe, поэтому при закрытии лаунчера
/// процесс завершается в <see cref="Dispose"/> — иначе сервер может зависнуть
/// на записи в закрытый pipe. Корректная остановка — через <see cref="StopAsync"/>.
/// </summary>
public sealed class ServerProcess : IDisposable
{
    private const int FlushIntervalMs = 1000;
    private const int FlushPendingLines = 50;

    private static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(60);
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly Process _process;
    private readonly StreamWriter? _logWriter;
    private readonly object _logLock = new();
    private readonly SemaphoreSlim _stdinLock = new(1, 1);
    private readonly int _id;

    private int _exitCode;
    private int _hasExitCode;
    private int _exitedRaised;
    private int _disposed;
    private long _lastFlushTicks;
    private int _pendingLines;

    internal ServerProcess(ProcessStartInfo startInfo, string? logPath)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (!string.IsNullOrWhiteSpace(logPath))
        {
            var fullLogPath = Path.GetFullPath(logPath);
            var logDir = Path.GetDirectoryName(fullLogPath);
            if (!string.IsNullOrWhiteSpace(logDir))
            {
                Directory.CreateDirectory(logDir);
            }

            var stream = new FileStream(fullLogPath, FileMode.Append, FileAccess.Write, FileShare.Read);
            _logWriter = new StreamWriter(stream, Utf8NoBom) { AutoFlush = false };
            _lastFlushTicks = Environment.TickCount64;
        }

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.WindowStyle = ProcessWindowStyle.Hidden;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.StandardInputEncoding ??= Utf8NoBom;
        startInfo.StandardOutputEncoding ??= Utf8NoBom;
        startInfo.StandardErrorEncoding ??= Utf8NoBom;

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
                throw new LauncherException("Не удалось запустить процесс сервера.");
            }
        }
        catch (LauncherException)
        {
            CleanupAfterFailedStart();
            throw;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            CleanupAfterFailedStart();
            throw new LauncherException(
                $"Не удалось запустить сервер ({startInfo.FileName}): {ex.Message}", ex);
        }

        _id = _process.Id;
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    /// <summary>Вызывается для каждой строки stdout/stderr.</summary>
    public event Action<LogLine>? LineReceived;

    /// <summary>Вызывается один раз после завершения процесса.</summary>
    public event Action<int>? Exited;

    public int Id => _id;

    public bool IsRunning
    {
        get
        {
            try
            {
                return !_process.HasExited;
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
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
            catch (Exception ex) when (
                ex is InvalidOperationException
                    or Win32Exception
                    or NotSupportedException
                    or ObjectDisposedException)
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

    /// <summary>
    /// Отправляет команду в консоль сервера. Переводы строк внутри команды
    /// заменяются пробелами, пустые команды игнорируются.
    /// </summary>
    public async Task SendCommandAsync(string command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new LauncherException("Сервер уже остановлен: команда не отправлена.");
        }

        var normalized = command
            .Replace("\r\n", " ", StringComparison.Ordinal)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();

        if (normalized.Length == 0)
        {
            return;
        }

        await _stdinLock.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            ct.ThrowIfCancellationRequested();

            if (!IsRunning)
            {
                throw new LauncherException("Сервер не запущен: команда не отправлена.");
            }

            await _process.StandardInput
                .WriteLineAsync(normalized.AsMemory(), ct)
                .ConfigureAwait(false);

            await _process.StandardInput.FlushAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (
            ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            throw new LauncherException(
                "Не удалось отправить команду серверу: процесс уже завершился.", ex);
        }
        finally
        {
            _stdinLock.Release();
        }
    }

    /// <summary>
    /// Отправляет <c>stop</c> и ждёт завершения. При таймауте убивает процесс
    /// и возвращает false. При отмене токена процесс не убивается.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!IsRunning)
        {
            return true;
        }

        try
        {
            await SendCommandAsync("stop", ct).ConfigureAwait(false);
        }
        catch (LauncherException)
        {
            return !IsRunning;
        }

        var wait = timeout ?? DefaultStopTimeout;
        if (wait < TimeSpan.Zero)
        {
            wait = TimeSpan.Zero;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(wait);

        try
        {
            await WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            if (!IsRunning)
            {
                return true;
            }

            Kill();
            return false;
        }
    }

    /// <summary>Завершает процесс вместе с дочерними процессами Java.</summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
                or Win32Exception
                or NotSupportedException
                or ObjectDisposedException)
        {
            // Процесс уже завершился или нет прав — ждать больше нечего.
        }
    }

    /// <summary>
    /// Ждёт завершения процесса. При отмене токена процесс не убивается — это решает вызывающий.
    /// </summary>
    public async Task WaitForExitAsync(CancellationToken ct = default)
    {
        await _process.WaitForExitAsync(ct).ConfigureAwait(false);

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
                or NotSupportedException
                or ObjectDisposedException)
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

        // Java пишет в перенаправленные pipes: после закрытия лаунчера их нельзя
        // просто оставить, иначе сервер может зависнуть на записи. Если процесс
        // ещё работает, завершаем его дерево.
        if (IsRunning)
        {
            Kill();
            try
            {
                _process.WaitForExit(5000);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
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
                    // Диск закончился или файл закрыт — сервер из-за лога не останавливаем.
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
                // Ошибка подписчика не должна ломать чтение вывода сервера.
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
            ex is InvalidOperationException or Win32Exception or ObjectDisposedException)
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
                    or NotSupportedException
                    or ObjectDisposedException)
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
