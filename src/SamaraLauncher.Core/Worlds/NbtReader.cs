using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace SamaraLauncher.Core.Worlds;

/// <summary>Типы тегов NBT (как в спецификации Minecraft).</summary>
public enum NbtTagType : byte
{
    End = 0,
    Byte = 1,
    Short = 2,
    Int = 3,
    Long = 4,
    Float = 5,
    Double = 6,
    ByteArray = 7,
    String = 8,
    List = 9,
    Compound = 10,
    IntArray = 11,
    LongArray = 12
}

/// <summary>
/// NBT-словарь. Значения: sbyte, short, int, long, float, double, string,
/// byte[], int[], long[], <see cref="NbtList"/> или <see cref="NbtCompound"/>.
/// </summary>
public sealed class NbtCompound : Dictionary<string, object?>
{
    public NbtCompound() : base(StringComparer.Ordinal)
    {
    }

    public string? GetString(string key, string? fallback = null)
    {
        if (!TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        if (value is string text)
        {
            return text;
        }

        return value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double
            ? Convert.ToString(value, CultureInfo.InvariantCulture)
            : fallback;
    }

    public long GetLong(string key, long fallback = 0) =>
        TryGetLong(key, out var value) ? value : fallback;

    public int GetInt(string key, int fallback = 0)
    {
        if (!TryGetLong(key, out var value) || value < int.MinValue || value > int.MaxValue)
        {
            return fallback;
        }

        return (int)value;
    }

    public short GetShort(string key, short fallback = 0)
    {
        if (!TryGetLong(key, out var value) || value < short.MinValue || value > short.MaxValue)
        {
            return fallback;
        }

        return (short)value;
    }

    public byte GetByte(string key, byte fallback = 0)
    {
        if (!TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        if (value is sbyte signed)
        {
            return unchecked((byte)signed);
        }

        if (value is byte raw)
        {
            return raw;
        }

        if (!TryGetLong(key, out var number) || number < byte.MinValue || number > byte.MaxValue)
        {
            return fallback;
        }

        return (byte)number;
    }

    public bool GetBool(string key, bool fallback = false) =>
        GetByte(key, fallback ? (byte)1 : (byte)0) != 0;

    public float GetFloat(string key, float fallback = 0)
    {
        if (!TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        try
        {
            return value switch
            {
                float number => number,
                double number => (float)number,
                string text when float.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => Convert.ToSingle(value, CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return fallback;
        }
    }

    public double GetDouble(string key, double fallback = 0)
    {
        if (!TryGetValue(key, out var value) || value is null)
        {
            return fallback;
        }

        try
        {
            return value switch
            {
                double number => number,
                float number => number,
                string text when double.TryParse(
                    text,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var parsed) => parsed,
                _ => Convert.ToDouble(value, CultureInfo.InvariantCulture)
            };
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            return fallback;
        }
    }

    public NbtCompound? GetCompound(string key) =>
        TryGetValue(key, out var value) ? value as NbtCompound : null;

    public NbtList? GetList(string key) =>
        TryGetValue(key, out var value) ? value as NbtList : null;

    public byte[]? GetByteArray(string key) =>
        TryGetValue(key, out var value) ? value as byte[] : null;

    public int[]? GetIntArray(string key) =>
        TryGetValue(key, out var value) ? value as int[] : null;

    public long[]? GetLongArray(string key) =>
        TryGetValue(key, out var value) ? value as long[] : null;

    /// <summary>Пытается получить целое число, в том числе из строки (некоторые моды пишут seed строкой).</summary>
    public bool TryGetLong(string key, out long value)
    {
        value = 0;
        if (!TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        switch (raw)
        {
            case sbyte number:
                value = number;
                return true;
            case byte number:
                value = number;
                return true;
            case short number:
                value = number;
                return true;
            case ushort number:
                value = number;
                return true;
            case int number:
                value = number;
                return true;
            case uint number:
                value = number;
                return true;
            case long number:
                value = number;
                return true;
            case ulong number when number <= long.MaxValue:
                value = (long)number;
                return true;
            case float number when number >= long.MinValue && number <= long.MaxValue:
                value = (long)number;
                return true;
            case double number when number >= long.MinValue && number <= long.MaxValue:
                value = (long)number;
                return true;
            case string text when long.TryParse(
                text,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }

    public bool TryGetString(string key, out string value)
    {
        var text = GetString(key);
        value = text ?? "";
        return text is not null;
    }

    /// <summary>Разбирает NBT из байтов (gzip/zlib/несжатый).</summary>
    public static NbtCompound FromBytes(byte[] data) => NbtReader.FromBytes(data);

    /// <summary>Читает NBT из файла (gzip/zlib/несжатый).</summary>
    public static NbtCompound ReadFile(string path) => NbtReader.ReadFile(path);
}

/// <summary>Список NBT с типом элементов.</summary>
public sealed class NbtList : List<object?>
{
    public NbtList(NbtTagType elementType = NbtTagType.End)
    {
        ElementType = elementType;
    }

    public NbtTagType ElementType { get; }
}

/// <summary>
/// Минимальный NBT-парсер для level.dat: gzip, zlib и несжатый формат.
/// Без внешних зависимостей.
/// </summary>
public static class NbtReader
{
    public const int MaxDepth = 512;

    private const int MaxDecompressedBytes = 256 * 1024 * 1024;

    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    /// <summary>Читает level.dat (gzip и несжатый).</summary>
    public static NbtCompound ReadFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new LauncherException("Не указан путь к NBT-файлу.");
        }

        if (!File.Exists(path))
        {
            throw new LauncherException($"NBT-файл не найден: {path}");
        }

        try
        {
            var data = File.ReadAllBytes(path);
            return FromBytes(data);
        }
        catch (LauncherException ex)
        {
            throw new LauncherException(
                $"Не удалось прочитать NBT-файл {Path.GetFileName(path)}: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось прочитать NBT-файл {path}: {ex.Message}", ex);
        }
    }

    /// <summary>Разбирает NBT из байтов; автоматически распознаёт gzip и zlib.</summary>
    public static NbtCompound FromBytes(byte[] data)
    {
        if (data is null || data.Length == 0)
        {
            throw new LauncherException("NBT-данные пусты.");
        }

        try
        {
            var payload = IsGzip(data)
                ? DecompressGzip(data)
                : IsZlib(data)
                    ? DecompressZlib(data)
                    : data;

            var reader = new NbtBinaryReader(payload);
            var type = (NbtTagType)reader.ReadByte();

            if (type == NbtTagType.End)
            {
                throw new LauncherException("NBT-данные не содержат корневого тега.");
            }

            if (type != NbtTagType.Compound)
            {
                throw new LauncherException(
                    $"Корневой тег NBT должен быть Compound, а найден {type}.");
            }

            // У корневого тега тоже есть имя (обычно пустое).
            reader.ReadString();
            return ReadCompoundPayload(reader, 1);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            throw new LauncherException("Сжатые NBT-данные повреждены.", ex);
        }
        catch (Exception ex) when (ex is IOException or OverflowException or ArgumentException)
        {
            throw new LauncherException($"Не удалось разобрать NBT: {ex.Message}", ex);
        }
    }

    private static bool IsGzip(byte[] data) =>
        data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B;

    private static bool IsZlib(byte[] data) =>
        data.Length >= 2
        && (data[0] & 0x0F) == 8
        && ((data[0] << 8) | data[1]) % 31 == 0;

    private static byte[] DecompressGzip(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        return ReadDecompressed(gzip);
    }

    private static byte[] DecompressZlib(byte[] data)
    {
        using var input = new MemoryStream(data, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        return ReadDecompressed(zlib);
    }

    private static byte[] ReadDecompressed(Stream source)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];

        while (true)
        {
            var read = source.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            if (output.Length + read > MaxDecompressedBytes)
            {
                throw new LauncherException(
                    $"NBT-файл слишком большой после распаковки (лимит {MaxDecompressedBytes / (1024 * 1024)} МБ).");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static NbtCompound ReadCompoundPayload(NbtBinaryReader reader, int depth)
    {
        var compound = new NbtCompound();

        while (true)
        {
            var type = (NbtTagType)reader.ReadByte();
            if (type == NbtTagType.End)
            {
                return compound;
            }

            if (type > NbtTagType.LongArray)
            {
                throw new LauncherException($"Неизвестный тип тега NBT: {(byte)type}.");
            }

            var name = reader.ReadString();
            compound[name] = ReadPayload(type, reader, depth + 1);
        }
    }

    private static object ReadPayload(NbtTagType type, NbtBinaryReader reader, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new LauncherException(
                $"Структура NBT слишком глубокая (лимит {MaxDepth}).");
        }

        return type switch
        {
            NbtTagType.Byte => reader.ReadSByte(),
            NbtTagType.Short => reader.ReadInt16(),
            NbtTagType.Int => reader.ReadInt32(),
            NbtTagType.Long => reader.ReadInt64(),
            NbtTagType.Float => BitConverter.Int32BitsToSingle(reader.ReadInt32()),
            NbtTagType.Double => BitConverter.Int64BitsToDouble(reader.ReadInt64()),
            NbtTagType.ByteArray => reader.ReadByteArray(),
            NbtTagType.String => reader.ReadString(),
            NbtTagType.List => ReadListPayload(reader, depth),
            NbtTagType.Compound => ReadCompoundPayload(reader, depth),
            NbtTagType.IntArray => reader.ReadIntArray(),
            NbtTagType.LongArray => reader.ReadLongArray(),
            _ => throw new LauncherException($"Неожиданный тип тега NBT: {type}.")
        };
    }

    private static NbtList ReadListPayload(NbtBinaryReader reader, int depth)
    {
        var elementType = (NbtTagType)reader.ReadByte();
        var length = reader.ReadInt32();

        if (length < 0)
        {
            throw new LauncherException($"Отрицательная длина NBT-списка: {length}.");
        }

        if (elementType == NbtTagType.End && length > 0)
        {
            throw new LauncherException(
                "NBT-список с типом End не может содержать элементы.");
        }

        if (elementType > NbtTagType.LongArray)
        {
            throw new LauncherException(
                $"Неизвестный тип элемента NBT-списка: {(byte)elementType}.");
        }

        var list = new NbtList(elementType);
        for (var index = 0; index < length; index++)
        {
            list.Add(ReadPayload(elementType, reader, depth + 1));
        }

        return list;
    }

    private sealed class NbtBinaryReader
    {
        private readonly byte[] _data;

        public NbtBinaryReader(byte[] data)
        {
            _data = data;
        }

        public int Position { get; private set; }

        public int Remaining => _data.Length - Position;

        public byte ReadByte()
        {
            Ensure(1);
            return _data[Position++];
        }

        public sbyte ReadSByte() => unchecked((sbyte)ReadByte());

        public short ReadInt16()
        {
            Ensure(2);
            var value = BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(Position, 2));
            Position += 2;
            return value;
        }

        public int ReadInt32()
        {
            Ensure(4);
            var value = BinaryPrimitives.ReadInt32BigEndian(_data.AsSpan(Position, 4));
            Position += 4;
            return value;
        }

        public long ReadInt64()
        {
            Ensure(8);
            var value = BinaryPrimitives.ReadInt64BigEndian(_data.AsSpan(Position, 8));
            Position += 8;
            return value;
        }

        public string ReadString()
        {
            Ensure(2);
            var length = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(Position, 2));
            Position += 2;

            if (length == 0)
            {
                return "";
            }

            Ensure(length);
            var value = Utf8.GetString(_data, Position, length);
            Position += length;
            return value;
        }

        public byte[] ReadByteArray()
        {
            var length = ReadInt32();
            EnsureArray(length, 1);
            var value = new byte[length];
            if (length > 0)
            {
                Buffer.BlockCopy(_data, Position, value, 0, length);
                Position += length;
            }

            return value;
        }

        public int[] ReadIntArray()
        {
            var length = ReadInt32();
            EnsureArray(length, 4);
            var value = new int[length];

            for (var index = 0; index < length; index++)
            {
                value[index] = ReadInt32();
            }

            return value;
        }

        public long[] ReadLongArray()
        {
            var length = ReadInt32();
            EnsureArray(length, 8);
            var value = new long[length];

            for (var index = 0; index < length; index++)
            {
                value[index] = ReadInt64();
            }

            return value;
        }

        private void EnsureArray(int length, int itemSize)
        {
            if (length < 0)
            {
                throw new LauncherException($"Отрицательная длина NBT-массива: {length}.");
            }

            var total = (long)length * itemSize;
            if (total > int.MaxValue || total > Remaining)
            {
                throw new LauncherException(
                    $"NBT-массив обрывается: нужно {total} байт, осталось {Remaining}.");
            }
        }

        private void Ensure(int count)
        {
            if (count < 0 || count > Remaining)
            {
                throw new LauncherException(
                    $"NBT-данные обрываются: нужно {count} байт, осталось {Remaining}.");
            }
        }
    }
}
