using System.Text;

namespace SamaraLauncher.Core.Logs;

/// <summary>
/// Единое определение кодировки логов. UTF-8 проверяется мягко: незавершённый
/// последний символ (лог пишется прямо сейчас) не должен переключать весь файл на cp1251.
/// </summary>
internal static class LogEncoding
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    private static Encoding _cp1251 = null!;

    static LogEncoding()
    {
        // Java 17 на Windows часто пишет latest.log в системной кодировке (cp1251).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        _cp1251 = Encoding.GetEncoding(1251);
    }

    public static Encoding Utf8NoBom { get; } = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public static Encoding Cp1251 => _cp1251;

    /// <summary>
    /// Определяет кодировку по началу файла. Незавершённый хвост UTF-8 игнорируется.
    /// </summary>
    public static Encoding Detect(byte[] bytes, int count) => Detect(bytes, 0, count);

    /// <summary>
    /// Определяет кодировку участка буфера. Начало может быть серединой UTF-8 символа
    /// (чтение хвоста файла) — ведущие continuation-байты игнорируются.
    /// </summary>
    public static Encoding Detect(byte[] bytes, int offset, int count)
    {
        if (count <= 0 || offset < 0 || offset >= bytes.Length)
        {
            return Utf8NoBom;
        }

        var end = Math.Min(bytes.Length, offset + count);
        var start = offset;

        if (start > 0)
        {
            start = TrimUtf8ContinuationStart(bytes, start, end);
        }

        if (start >= end)
        {
            return Utf8NoBom;
        }

        if (start == 0)
        {
            if (end >= 4)
            {
                if (bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00)
                {
                    return new UTF32Encoding(bigEndian: false, byteOrderMark: true, throwOnInvalidCharacters: false);
                }

                if (bytes[0] == 0x00 && bytes[1] == 0x00 && bytes[2] == 0xFE && bytes[3] == 0xFF)
                {
                    return new UTF32Encoding(bigEndian: true, byteOrderMark: true, throwOnInvalidCharacters: false);
                }
            }

            if (end >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                return Utf8NoBom;
            }

            if (end >= 2)
            {
                if (bytes[0] == 0xFF && bytes[1] == 0xFE)
                {
                    return new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: false);
                }

                if (bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    return new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: false);
                }
            }
        }

        var complete = TrimIncompleteUtf8Tail(bytes, start, end - start);
        if (complete <= start)
        {
            return Utf8NoBom;
        }

        try
        {
            _ = StrictUtf8.GetString(bytes, start, complete - start);
            return Utf8NoBom;
        }
        catch (DecoderFallbackException)
        {
            return Cp1251;
        }
    }

    /// <summary>Декодирует буфер целиком, обрезая неполный UTF-8 символ на границах.</summary>
    public static string Decode(byte[] bytes, int count) =>
        Decode(bytes, 0, count, Detect(bytes, count));

    /// <summary>Декодирует буфер заранее определённой кодировкой.</summary>
    public static string Decode(byte[] bytes, int count, Encoding encoding) =>
        Decode(bytes, 0, count, encoding);

    /// <summary>Декодирует участок буфера, не создавая «кракозябр» на границах UTF-8.</summary>
    public static string Decode(byte[] bytes, int offset, int count, Encoding encoding)
    {
        if (count <= 0)
        {
            return "";
        }

        var start = offset;
        var end = offset + count;

        if (encoding.CodePage == Encoding.UTF8.CodePage)
        {
            start = TrimUtf8ContinuationStart(bytes, start, end);
            end = TrimIncompleteUtf8Tail(bytes, start, end - start);
        }

        if (end <= start)
        {
            return "";
        }

        return encoding.GetString(bytes, start, end - start).TrimStart('\uFEFF');
    }

    /// <summary>
    /// Возвращает индекс конца буфера без незавершённой последовательности UTF-8.
    /// Для cp1251 и других однобайтовых кодировок не используется.
    /// </summary>
    public static int TrimIncompleteUtf8Tail(byte[] bytes, int offset, int count)
    {
        var end = offset + count;
        if (end <= offset)
        {
            return end;
        }

        var index = end - 1;
        var continuation = 0;

        while (index >= offset && (bytes[index] & 0xC0) == 0x80 && continuation < 3)
        {
            index--;
            continuation++;
        }

        if (index < offset)
        {
            // Начало буфера — неполная последовательность, оставляем как есть.
            return end;
        }

        var lead = bytes[index];
        var expected = lead switch
        {
            >= 0xF0 and <= 0xF7 => 4,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xC0 and <= 0xDF => 2,
            _ => 1
        };

        var available = end - index;
        return available < expected ? index : end;
    }

    private static int TrimUtf8ContinuationStart(byte[] bytes, int offset, int end)
    {
        var index = offset;
        var continuation = 0;

        while (index < end && (bytes[index] & 0xC0) == 0x80 && continuation < 3)
        {
            index++;
            continuation++;
        }

        return index;
    }
}
