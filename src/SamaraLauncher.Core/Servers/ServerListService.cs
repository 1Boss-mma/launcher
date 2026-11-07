using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Worlds;

namespace SamaraLauncher.Core.Servers;

/// <summary>
/// Чтение и запись servers.dat инстанса. Формат Minecraft: gzip-сжатый NBT,
/// корневой compound со списком "servers" (name, ip, icon, acceptTextures).
/// </summary>
public sealed class ServerListService
{
    public const string ServersFileName = "servers.dat";

    /// <summary>Путь к servers.dat инстанса.</summary>
    public string GetServersDatPath(InstanceProfile instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        if (string.IsNullOrWhiteSpace(instance.GameDir))
        {
            throw new LauncherException("Не указана папка игры инстанса.");
        }

        try
        {
            return Path.Combine(Path.GetFullPath(instance.GameDir), ServersFileName);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException(
                $"Недопустимая папка игры инстанса: {instance.GameDir}", ex);
        }
    }

    /// <summary>
    /// Читает список серверов. Нет файла — пустой список; битый файл — LauncherException.
    /// </summary>
    public IReadOnlyList<ServerEntry> Load(InstanceProfile instance)
    {
        var path = GetServersDatPath(instance);
        if (!File.Exists(path))
        {
            return Array.Empty<ServerEntry>();
        }

        NbtCompound root;
        try
        {
            root = NbtReader.ReadFile(path);
        }
        catch (LauncherException ex)
        {
            throw new LauncherException(
                $"Не удалось прочитать {ServersFileName}: {ex.Message}", ex);
        }

        var list = root.GetList("servers");
        if (list is null || list.Count == 0)
        {
            return Array.Empty<ServerEntry>();
        }

        var result = new List<ServerEntry>(list.Count);

        foreach (var item in list)
        {
            if (item is not NbtCompound compound)
            {
                continue;
            }

            var rawAddress = (compound.GetString("ip") ?? "").Trim();
            if (rawAddress.Length == 0)
            {
                continue;
            }

            if (!ServerAddress.TryParse(
                    rawAddress,
                    ServerEntry.DefaultPort,
                    out var host,
                    out var port))
            {
                continue;
            }

            var name = (compound.GetString("name") ?? "").Trim();
            if (name.Length == 0)
            {
                name = ServerAddress.Format(host, port);
            }

            result.Add(new ServerEntry
            {
                Name = name,
                Host = host,
                Port = port,
                IconBase64 = NormalizeIcon(compound.GetString("icon"))
            });
        }

        return result;
    }

    /// <summary>
    /// Перезаписывает servers.dat известными полями. Неизвестные теги и поля
    /// (включая избранное/заметки — их нет в ванильном формате) теряются.
    /// </summary>
    public void Save(InstanceProfile instance, IEnumerable<ServerEntry> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        var path = GetServersDatPath(instance);
        var list = new NbtList(NbtTagType.Compound);

        foreach (var server in servers)
        {
            if (server is null || string.IsNullOrWhiteSpace(server.Host))
            {
                continue;
            }

            if (!ServerAddress.TryParse(
                    server.Host,
                    server.Port,
                    out var host,
                    out var port))
            {
                throw new LauncherException($"Некорректный адрес сервера: {server.Host}");
            }

            var name = string.IsNullOrWhiteSpace(server.Name)
                ? ServerAddress.Format(host, port)
                : server.Name.Trim();

            var compound = new NbtCompound
            {
                ["name"] = name,
                ["ip"] = ServerAddress.Format(host, port),
                ["acceptTextures"] = (sbyte)1
            };

            var icon = NormalizeIcon(server.IconBase64);
            if (icon is not null)
            {
                compound["icon"] = "data:image/png;base64," + icon;
            }

            list.Add(compound);
        }

        var root = new NbtCompound { ["servers"] = list };
        var tempPath = path + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            ServersNbtWriter.WriteCompressedFile(tempPath, root);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (LauncherException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new LauncherException(
                $"Не удалось сохранить {ServersFileName}: {ex.Message}", ex);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    /// <summary>Добавляет сервер или обновляет существующий с тем же host:port.</summary>
    public void AddOrUpdate(InstanceProfile instance, ServerEntry server)
    {
        ArgumentNullException.ThrowIfNull(server);

        if (string.IsNullOrWhiteSpace(server.Host))
        {
            throw new LauncherException("Не указан адрес сервера.");
        }

        if (!ServerAddress.TryParse(
                server.Host,
                server.Port,
                out var host,
                out var port))
        {
            throw new LauncherException($"Некорректный адрес сервера: {server.Host}");
        }

        var normalized = CopyWithAddress(server, host, port);
        var servers = Load(instance).ToList();
        var index = servers.FindIndex(item => SameAddress(item, host, port));

        if (index >= 0)
        {
            servers[index] = normalized;
        }
        else
        {
            servers.Add(normalized);
        }

        Save(instance, servers);
    }

    /// <summary>Удаляет сервер по адресу. false — сервер не найден.</summary>
    public bool Remove(
        InstanceProfile instance,
        string host,
        int port = ServerEntry.DefaultPort)
    {
        if (!ServerAddress.TryParse(host, port, out var targetHost, out var targetPort))
        {
            throw new LauncherException($"Некорректный адрес сервера: {host}");
        }

        var servers = Load(instance).ToList();
        var removed = servers.RemoveAll(
            item => SameAddress(item, targetHost, targetPort)) > 0;

        if (!removed)
        {
            return false;
        }

        Save(instance, servers);
        return true;
    }

    /// <summary>Удаляет конкретную запись. false — сервер не найден.</summary>
    public bool Remove(InstanceProfile instance, ServerEntry server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return Remove(instance, server.Host, server.Port);
    }

    /// <summary>Перемещает сервер с одной позиции на другую.</summary>
    public void Move(InstanceProfile instance, int fromIndex, int toIndex)
    {
        var servers = Load(instance).ToList();

        if (fromIndex < 0 || fromIndex >= servers.Count)
        {
            throw new LauncherException($"Недопустимый индекс сервера: {fromIndex}.");
        }

        if (toIndex < 0 || toIndex >= servers.Count)
        {
            throw new LauncherException($"Недопустимый индекс сервера: {toIndex}.");
        }

        if (fromIndex == toIndex)
        {
            return;
        }

        var server = servers[fromIndex];
        servers.RemoveAt(fromIndex);
        servers.Insert(toIndex, server);
        Save(instance, servers);
    }

    /// <summary>Перемещает конкретную запись на новую позицию.</summary>
    public void Move(InstanceProfile instance, ServerEntry server, int newIndex)
    {
        ArgumentNullException.ThrowIfNull(server);

        var servers = Load(instance);
        var index = -1;

        for (var position = 0; position < servers.Count; position++)
        {
            if (SameAddress(servers[position], server.Host, server.Port))
            {
                index = position;
                break;
            }
        }

        if (index < 0)
        {
            throw new LauncherException(
                $"Сервер не найден: {ServerAddress.Format(server.Host, server.Port)}");
        }

        Move(instance, index, newIndex);
    }

    private static ServerEntry CopyWithAddress(ServerEntry source, string host, int port) => new()
    {
        Name = source.Name,
        Host = host,
        Port = port,
        IsFavorite = source.IsFavorite,
        LastPlayed = source.LastPlayed,
        IconBase64 = source.IconBase64,
        Notes = source.Notes
    };

    private static bool SameAddress(ServerEntry entry, string host, int port) =>
        entry.Port == port
        && string.Equals(entry.Host, host, StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeIcon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var text = value.Trim();
        var comma = text.IndexOf(',');
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            text = text[(comma + 1)..].Trim();
        }

        return text.Length == 0 ? null : text;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// Минимальный NBT writer для servers.dat: BigEndian, gzip.
/// Поддерживает TAG_Compound, TAG_List, TAG_String и TAG_Byte.
/// </summary>
internal static class ServersNbtWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    public static void WriteCompressedFile(string path, NbtCompound root)
    {
        using var file = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.SequentialScan);

        using var gzip = new GZipStream(file, CompressionLevel.Optimal);

        WriteByte(gzip, (byte)NbtTagType.Compound);
        WriteString(gzip, ""); // имя корневого тега всегда пустое
        WriteCompoundPayload(gzip, root);
    }

    private static void WriteCompoundPayload(Stream stream, NbtCompound compound)
    {
        foreach (var pair in compound)
        {
            if (pair.Value is null)
            {
                continue;
            }

            var type = GetTagType(pair.Value);
            WriteByte(stream, (byte)type);
            WriteString(stream, pair.Key);
            WritePayload(stream, type, pair.Value);
        }

        WriteByte(stream, (byte)NbtTagType.End);
    }

    private static NbtTagType GetTagType(object value) => value switch
    {
        sbyte or byte => NbtTagType.Byte,
        string => NbtTagType.String,
        NbtList => NbtTagType.List,
        NbtCompound => NbtTagType.Compound,
        _ => throw new LauncherException(
            $"NBT writer не поддерживает тип {value.GetType().Name}.")
    };

    private static void WritePayload(Stream stream, NbtTagType type, object value)
    {
        switch (type)
        {
            case NbtTagType.Byte:
                WriteByte(stream, value is sbyte signed
                    ? unchecked((byte)signed)
                    : (byte)value);
                break;

            case NbtTagType.String:
                WriteString(stream, (string)value);
                break;

            case NbtTagType.List:
                WriteListPayload(stream, (NbtList)value);
                break;

            case NbtTagType.Compound:
                WriteCompoundPayload(stream, (NbtCompound)value);
                break;

            default:
                throw new LauncherException($"NBT writer не поддерживает тег {type}.");
        }
    }

    private static void WriteListPayload(Stream stream, NbtList list)
    {
        if (list.Count == 0)
        {
            WriteByte(stream, (byte)NbtTagType.End);
            WriteInt32(stream, 0);
            return;
        }

        var first = list[0]
            ?? throw new LauncherException("NBT-список содержит null-элемент.");

        var elementType = list.ElementType == NbtTagType.End
            ? GetTagType(first)
            : list.ElementType;

        if (elementType is not (
                NbtTagType.Byte or NbtTagType.String or NbtTagType.List or NbtTagType.Compound))
        {
            throw new LauncherException(
                $"NBT writer не поддерживает список с элементами {elementType}.");
        }

        WriteByte(stream, (byte)elementType);
        WriteInt32(stream, list.Count);

        foreach (var item in list)
        {
            if (item is null || GetTagType(item) != elementType)
            {
                throw new LauncherException(
                    "NBT-список содержит элементы разных типов.");
            }

            WritePayload(stream, elementType, item);
        }
    }

    private static void WriteByte(Stream stream, byte value) => stream.WriteByte(value);

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new LauncherException("Строка слишком длинная для NBT.");
        }

        var lengthBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(lengthBytes, (ushort)bytes.Length);
        stream.Write(lengthBytes, 0, lengthBytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }
}
