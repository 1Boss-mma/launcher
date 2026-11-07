using System.Globalization;
using System.Text;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Logs;

/// <summary>
/// Чтение latest.log и слежение за его дописыванием без блокировки UI.
/// </summary>
public sealed class LogService : IDisposable
{
    public const int DefaultTailLines = 500;

    private const int PollIntervalMs = 500;
    private const int TailChunkBytes = 256 * 1024;
    private const int MaxTailReadBytes = 32 * 1024 * 1024;

    private static readonly Encoding Utf8NoBom = LogEncoding.Utf8NoBom;

    private readonly object _tailLock = new();
    private CancellationTokenSource? _tailCts;
    private Task? _tailTask;
    private long _tailPosition;
    private byte[] _tailRemainder = Array.Empty<byte>();
    private Encoding _tailEncoding = Utf8NoBom;

    /// <summary>Новая строка latest.log. Подписчик вызывается из фонового потока.</summary>
    public event Action<LogLine>? LineAppended;

    /// <summary>
    /// Читает latest.log. fromBeginning сохранён для совместимости; maxLines всегда
    /// ограничивает число последних строк (0 — весь файл). Файл читается с конца.
    /// Если файла нет, возвращает пустой список.
    /// </summary>
    public IReadOnlyList<LogLine> TailLatestLog(
        string gameDir,
        bool fromBeginning,
        int maxLines = DefaultTailLines)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            return Array.Empty<LogLine>();
        }

        var path = Path.Combine(gameDir, "logs", "latest.log");

        // maxLines ограничивает и режим fromBeginning: иначе большой latest.log
        // читается целиком ради последних строк. 0/отрицательное значение — весь файл.
        var limit = maxLines <= 0 ? 0 : Math.Max(1, maxLines);
        var lines = ReadTail(path, limit);

        var result = new List<LogLine>(lines.Count);
        foreach (var line in lines)
        {
            result.Add(CreateLogLine(line, parseMinecraftTimestamp: true));
        }

        return result;
    }

    /// <summary>Читает лог лаунчера, созданный GameProcess/GameLauncher.</summary>
    public IReadOnlyList<LogLine> ReadLauncherLog(string path, int maxLines = DefaultTailLines)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<LogLine>();
        }

        var limit = maxLines <= 0 ? 0 : maxLines;
        var lines = ReadTail(path, limit);

        var result = new List<LogLine>(lines.Count);
        foreach (var line in lines)
        {
            result.Add(ParseLauncherLine(line));
        }

        return result;
    }

    /// <summary>Начинает слежение за &lt;gameDir&gt;\logs\latest.log.</summary>
    public void StartTail(string gameDir, bool fromBeginning = false)
    {
        if (string.IsNullOrWhiteSpace(gameDir))
        {
            throw new ArgumentException("Не указана папка игры.", nameof(gameDir));
        }

        StopTail();

        var path = Path.Combine(gameDir, "logs", "latest.log");
        long position = 0;
        _tailEncoding = DetectFileEncoding(path);

        if (!fromBeginning)
        {
            try
            {
                position = File.Exists(path) ? new FileInfo(path).Length : 0;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                position = 0;
            }
        }

        var cts = new CancellationTokenSource();
        lock (_tailLock)
        {
            _tailCts = cts;
            _tailPosition = position;
            _tailRemainder = Array.Empty<byte>();
            _tailTask = Task.Run(() => TailLoopAsync(path, cts.Token));
        }
    }

    public void StopTail()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_tailLock)
        {
            cts = _tailCts;
            task = _tailTask;
            _tailCts = null;
            _tailTask = null;
        }

        if (cts is null)
        {
            return;
        }

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Уже остановлено.
        }

        // Даём циклу выйти, чтобы старый watcher не дописывал строки после нового StartTail.
        if (task is not null && task.Id != Task.CurrentId)
        {
            try
            {
                task.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Ошибка внутри цикла уже обработана; здесь важен только факт остановки.
            }
        }

        cts.Dispose();
    }

    public void Dispose()
    {
        StopTail();
    }

    private async Task TailLoopAsync(string path, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                ReadNewBytes(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // latest.log может быть пересоздан или занят игрой — повторим на следующем тике.
            }

            try
            {
                await Task.Delay(PollIntervalMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ReadNewBytes(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var length = new FileInfo(path).Length;
        if (length < _tailPosition)
        {
            // latest.log пересоздан или обрезан.
            _tailPosition = 0;
            _tailRemainder = Array.Empty<byte>();
        }

        if (length == _tailPosition)
        {
            return;
        }

        byte[] chunk;
        using (var stream = new FileStream(
                   path,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.ReadWrite | FileShare.Delete))
        {
            if (_tailPosition > stream.Length)
            {
                _tailPosition = 0;
                _tailRemainder = Array.Empty<byte>();
            }

            stream.Seek(_tailPosition, SeekOrigin.Begin);
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            chunk = buffer.ToArray();
            _tailPosition = stream.Position;
        }

        if (chunk.Length == 0)
        {
            return;
        }

        var data = new byte[_tailRemainder.Length + chunk.Length];
        Buffer.BlockCopy(_tailRemainder, 0, data, 0, _tailRemainder.Length);
        Buffer.BlockCopy(chunk, 0, data, _tailRemainder.Length, chunk.Length);

        var lastNewLine = Array.LastIndexOf(data, (byte)'\n');
        if (lastNewLine < 0)
        {
            _tailRemainder = data;
            return;
        }

        var complete = LogEncoding.Decode(data, 0, lastNewLine + 1, _tailEncoding);
        _tailRemainder = data[(lastNewLine + 1)..];

        var lines = complete.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (i == lines.Length - 1 && line.Length == 0)
            {
                continue;
            }

            line = line.TrimEnd('\r');
            if (i == 0 && line.Length > 0 && line[0] == '\uFEFF')
            {
                line = line[1..];
            }

            RaiseLineAppended(CreateLogLine(line, parseMinecraftTimestamp: true));
        }
    }

    private void RaiseLineAppended(LogLine line)
    {
        var handlers = LineAppended;
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
                // Ошибка подписчика не должна останавливать чтение лога.
            }
        }
    }

    private static List<string> ReadTail(string path, int maxLines)
    {
        var result = new List<string>();
        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                81920,
                FileOptions.SequentialScan);

            var length = stream.Length;
            if (length == 0)
            {
                return result;
            }

            if (maxLines <= 0)
            {
                var all = new byte[length];
                stream.Seek(0, SeekOrigin.Begin);
                ReadExactly(stream, all);
                return SplitLines(LogEncoding.Decode(all, all.Length));
            }

            var chunks = new List<byte[]>();
            long start = length;
            long readBytes = 0;
            var newLines = 0;

            while (start > 0 && newLines <= maxLines && readBytes < MaxTailReadBytes)
            {
                var chunkSize = (int)Math.Min(
                    Math.Min(TailChunkBytes, start),
                    MaxTailReadBytes - readBytes);

                if (chunkSize <= 0)
                {
                    break;
                }

                start -= chunkSize;
                stream.Seek(start, SeekOrigin.Begin);

                var chunk = new byte[chunkSize];
                ReadExactly(stream, chunk);

                foreach (var value in chunk)
                {
                    if (value == (byte)'\n')
                    {
                        newLines++;
                    }
                }

                chunks.Add(chunk);
                readBytes += chunkSize;
            }

            var buffer = new byte[readBytes];
            var offset = 0;

            for (var index = chunks.Count - 1; index >= 0; index--)
            {
                var chunk = chunks[index];
                Buffer.BlockCopy(chunk, 0, buffer, offset, chunk.Length);
                offset += chunk.Length;
            }

            var skip = 0;
            if (start > 0)
            {
                var firstNewLine = Array.IndexOf(buffer, (byte)'\n');
                skip = firstNewLine >= 0 ? firstNewLine + 1 : buffer.Length;
            }

            // Кодировку определяем по реально показываемому хвосту: cp1251-кириллица
            // может впервые встретиться далеко после первых 4 КБ файла.
            var tailEncoding = LogEncoding.Detect(buffer, skip, buffer.Length - skip);
            var text = LogEncoding.Decode(buffer, skip, buffer.Length - skip, tailEncoding);
            var lines = SplitLines(text);
            return lines.Count <= maxLines ? lines : lines.GetRange(lines.Count - maxLines, maxLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return result;
        }
    }

    private static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        using var reader = new StringReader(text);
        string? line;

        while ((line = reader.ReadLine()) is not null)
        {
            result.Add(line);
        }

        return result;
    }

    private static Encoding DetectFileEncoding(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return Utf8NoBom;
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return DetectFileEncoding(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Utf8NoBom;
        }
    }

    private static Encoding DetectFileEncoding(Stream stream)
    {
        var position = stream.Position;

        try
        {
            var sample = new byte[Math.Min(4096, (int)Math.Min(stream.Length, 4096))];
            stream.Seek(0, SeekOrigin.Begin);
            var read = stream.Read(sample, 0, sample.Length);
            return LogEncoding.Detect(sample, read);
        }
        finally
        {
            stream.Position = position;
        }
    }

    private static void ReadExactly(Stream stream, byte[] buffer)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }
    }

    private static LogLine CreateLogLine(string text, bool parseMinecraftTimestamp)
    {
        var timestamp = DateTimeOffset.Now;

        if (parseMinecraftTimestamp &&
            text.Length > 2 &&
            text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close > 1 &&
                TimeSpan.TryParseExact(
                    text[1..close],
                    @"hh\:mm\:ss",
                    CultureInfo.InvariantCulture,
                    out var time))
            {
                var now = DateTimeOffset.Now;
                timestamp = new DateTimeOffset(now.Date.Add(time), now.Offset);
            }
        }

        var isError = text.Contains("/ERROR]", StringComparison.OrdinalIgnoreCase)
                      || text.Contains("/FATAL]", StringComparison.OrdinalIgnoreCase);

        return new LogLine(timestamp, text, isError);
    }

    private static LogLine ParseLauncherLine(string text)
    {
        if (text.Length > 2 && text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close > 1 &&
                DateTimeOffset.TryParseExact(
                    text[1..close],
                    "yyyy-MM-dd HH:mm:ss.fff",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out var timestamp))
            {
                var rest = text[(close + 1)..].TrimStart();
                var isError = rest.StartsWith("[stderr] ", StringComparison.OrdinalIgnoreCase);
                if (isError)
                {
                    rest = rest["[stderr] ".Length..];
                }

                return new LogLine(timestamp, rest, isError);
            }
        }

        return new LogLine(DateTimeOffset.Now, text);
    }
}
