using System.Buffers.Binary;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SamaraLauncher.Core.Servers;

/// <summary>
/// Java Edition Server List Ping (protocol 763 / Minecraft 1.20.1).
/// Сетевые ошибки не выбрасываются наружу: результат приходит с Online=false и текстом в Error.
/// </summary>
public sealed class ServerPinger
{
    public const int DefaultPort = ServerEntry.DefaultPort;

    /// <summary>Версия протокола handshake. Серверы отвечают на status независимо от неё.</summary>
    public const int HandshakeProtocolVersion = 763;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(8);

    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private const int MaxPacketLength = 4 * 1024 * 1024;

    private const int MaxStringLength = 1024 * 1024;

    private static readonly Encoding Utf8 = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: false);

    /// <summary>
    /// Пингует сервер. <paramref name="host"/> может быть "host", "host:port"
    /// или "[ipv6]:port"; порт по умолчанию — 25565.
    /// </summary>
    public async Task<ServerPingResult> PingAsync(
        string host,
        int port = DefaultPort,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        if (!ServerAddress.TryParse(host, port, out var targetHost, out var targetPort))
        {
            return Offline("Некорректный адрес сервера. Укажите host или host:port.");
        }

        var effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero && effectiveTimeout != Timeout.InfiniteTimeSpan)
        {
            return Offline("Таймаут ping должен быть положительным.");
        }

        var displayAddress = ServerAddress.Format(targetHost, targetPort);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            if (effectiveTimeout != Timeout.InfiniteTimeSpan)
            {
                overallCts.CancelAfter(effectiveTimeout);
            }

            using var client = new TcpClient { NoDelay = true };

            using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token))
            {
                connectCts.CancelAfter(ConnectTimeout);
                try
                {
                    await client
                        .ConnectAsync(targetHost, targetPort, connectCts.Token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return Offline(overallCts.IsCancellationRequested
                        ? FormatPingTimeout(displayAddress, effectiveTimeout)
                        : $"Таймаут подключения к {displayAddress} ({FormatSeconds(ConnectTimeout)}).");
                }
            }

            await using var stream = client.GetStream();

            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            readCts.CancelAfter(ReadTimeout);

            try
            {
                await SendPacketAsync(
                        stream,
                        BuildHandshakeBody(targetHost, targetPort),
                        readCts.Token)
                    .ConfigureAwait(false);

                await SendPacketAsync(stream, new byte[] { 0x00 }, readCts.Token)
                    .ConfigureAwait(false);

                var json = await ReadStatusJsonAsync(stream, readCts.Token).ConfigureAwait(false);
                var latency = (int)Math.Min(stopwatch.ElapsedMilliseconds, int.MaxValue);

                var result = new ServerPingResult
                {
                    Online = true,
                    LatencyMs = latency
                };

                ApplyStatusJson(json, result);

                // Pong не влияет на данные статуса: некоторые серверы закрывают
                // соединение сразу после ответа, это не считается ошибкой ping.
                try
                {
                    await SendPingReadPongAsync(stream, readCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                }
                catch (Exception ex) when (ex is IOException or SocketException or InvalidDataException)
                {
                }

                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return Offline(overallCts.IsCancellationRequested
                    ? FormatPingTimeout(displayAddress, effectiveTimeout)
                    : $"Таймаут чтения ответа от {displayAddress} ({FormatSeconds(ReadTimeout)}).");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Offline(DescribeError(ex, displayAddress));
        }
    }

    private static string DescribeError(Exception exception, string address) => exception switch
    {
        EndOfStreamException =>
            $"Сервер {address} закрыл соединение до получения ответа.",
        SocketException socket =>
            $"Не удалось подключиться к {address}: {socket.Message}",
        JsonException json =>
            $"Некорректный JSON статуса {address}: {json.Message}",
        InvalidDataException invalid =>
            $"Некорректный ответ {address}: {invalid.Message}",
        IOException io =>
            $"Ошибка сети при ping {address}: {io.Message}",
        _ => $"Не удалось выполнить ping {address}: {exception.Message}"
    };

    private static ServerPingResult Offline(string error) => new()
    {
        Online = false,
        Error = error
    };

    private static string FormatPingTimeout(string address, TimeSpan timeout) =>
        $"Таймаут ping {address} ({FormatSeconds(timeout)}).";

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " с";

    private static byte[] BuildHandshakeBody(string host, int port)
    {
        using var body = new MemoryStream();

        WriteVarInt(body, 0x00);
        WriteVarInt(body, HandshakeProtocolVersion);

        // Поле address в протоколе ограничено 255 символами.
        WriteString(body, host.Length > 255 ? host[..255] : host);

        var portBytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(portBytes, (ushort)port);
        body.Write(portBytes, 0, portBytes.Length);

        WriteVarInt(body, 1); // next state: status
        return body.ToArray();
    }

    private static async Task SendPingReadPongAsync(Stream stream, CancellationToken ct)
    {
        var body = new byte[9];
        body[0] = 0x01;
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(1), Stopwatch.GetTimestamp());

        await SendPacketAsync(stream, body, ct).ConfigureAwait(false);

        var response = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
        using var packet = new MemoryStream(response, writable: false);

        var packetId = await ReadVarIntAsync(packet, ct).ConfigureAwait(false);
        if (packetId != 0x01)
        {
            throw new InvalidDataException(
                $"Ожидался pong 0x01, получен пакет 0x{packetId:X2}.");
        }

        var pongPayload = new byte[8];
        await packet.ReadExactlyAsync(pongPayload, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReadStatusJsonAsync(Stream stream, CancellationToken ct)
    {
        var response = await ReadPacketAsync(stream, ct).ConfigureAwait(false);
        using var packet = new MemoryStream(response, writable: false);

        var packetId = await ReadVarIntAsync(packet, ct).ConfigureAwait(false);
        if (packetId != 0x00)
        {
            throw new InvalidDataException(
                $"Ожидался пакет статуса 0x00, получен 0x{packetId:X2}.");
        }

        return await ReadStringAsync(packet, ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadPacketAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length <= 0 || length > MaxPacketLength)
        {
            throw new InvalidDataException($"Некорректная длина пакета: {length}.");
        }

        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return body;
    }

    private static async Task SendPacketAsync(Stream stream, byte[] body, CancellationToken ct)
    {
        using var packet = new MemoryStream();
        WriteVarInt(packet, body.Length);
        packet.Write(body, 0, body.Length);

        await stream.WriteAsync(packet.ToArray(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static void WriteVarInt(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[5];
        var index = 0;
        var remaining = (uint)value;

        do
        {
            var current = (byte)(remaining & 0x7F);
            remaining >>= 7;

            if (remaining != 0)
            {
                current |= 0x80;
            }

            buffer[index++] = current;
        }
        while (remaining != 0);

        stream.Write(buffer[..index]);
    }

    private static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var buffer = new byte[1];

        for (var index = 0; index < 5; index++)
        {
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            var current = buffer[0];

            result |= (current & 0x7F) << (7 * index);

            if ((current & 0x80) == 0)
            {
                return result;
            }
        }

        throw new InvalidDataException("VarInt длиннее 5 байт.");
    }

    private static void WriteString(Stream stream, string value)
    {
        var bytes = Utf8.GetBytes(value);
        if (bytes.Length > MaxStringLength)
        {
            throw new LauncherException("Строка слишком длинная для протокола Minecraft.");
        }

        // В сетевом протоколе длина строки — VarInt (не ushort, как в NBT).
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static async Task<string> ReadStringAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct).ConfigureAwait(false);
        if (length < 0 || length > MaxStringLength)
        {
            throw new InvalidDataException($"Некорректная длина строки: {length}.");
        }

        if (length == 0)
        {
            return "";
        }

        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        return Utf8.GetString(bytes);
    }

    private static void ApplyStatusJson(string json, ServerPingResult result)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Ответ статуса не является JSON-объектом.");
        }

        if (root.TryGetProperty("version", out var version)
            && version.ValueKind == JsonValueKind.Object)
        {
            if (version.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String)
            {
                result.VersionName = name.GetString() ?? "";
            }

            result.Protocol = GetInt32(version, "protocol");
        }

        if (root.TryGetProperty("players", out var players)
            && players.ValueKind == JsonValueKind.Object)
        {
            result.PlayersOnline = GetInt32(players, "online");
            result.PlayersMax = GetInt32(players, "max");

            if (players.TryGetProperty("sample", out var sample)
                && sample.ValueKind == JsonValueKind.Array)
            {
                var list = new List<PlayerSample>();
                foreach (var item in sample.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    list.Add(new PlayerSample
                    {
                        Name = GetString(item, "name"),
                        Id = GetString(item, "id")
                    });

                    if (list.Count >= 1000)
                    {
                        break;
                    }
                }

                result.Players = list;
            }
        }

        if (root.TryGetProperty("description", out var description))
        {
            var text = new StringBuilder();
            AppendDescription(description, text, 0);
            result.Motd = StripFormatting(text.ToString()).Trim();
        }

        if (root.TryGetProperty("favicon", out var favicon)
            && favicon.ValueKind == JsonValueKind.String)
        {
            result.Favicon = ExtractFavicon(favicon.GetString());
        }
    }

    private static int GetInt32(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : 0;
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String)
        {
            return "";
        }

        return value.GetString() ?? "";
    }

    private static void AppendDescription(JsonElement element, StringBuilder builder, int depth)
    {
        if (depth > 64)
        {
            return;
        }

        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                builder.Append(element.GetString());
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AppendDescription(item, builder, depth + 1);
                }

                break;

            case JsonValueKind.Object:
                if (element.TryGetProperty("text", out var text))
                {
                    AppendDescription(text, builder, depth + 1);
                }

                if (element.TryGetProperty("extra", out var extra))
                {
                    AppendDescription(extra, builder, depth + 1);
                }

                break;
        }
    }

    private static string StripFormatting(string value)
    {
        if (value.IndexOf('\u00A7') < 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] == '\u00A7')
            {
                index++; // пропускаем сам код и следующий символ
                continue;
            }

            builder.Append(value[index]);
        }

        return builder.ToString();
    }

    private static string ExtractFavicon(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var text = value.Trim();
        var comma = text.IndexOf(',');
        if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
        {
            text = text[(comma + 1)..].Trim();
        }

        return text;
    }
}
