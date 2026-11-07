# Модуль серверов (SamaraLauncher.Core.Servers)

Модуль для списка серверов инстанса и Java Edition Server List Ping.
Только BCL (`System.Net.Sockets`, `System.Text.Json`, `System.IO.Compression`),
без NuGet и WPF. Сетевые операции асинхронные и поддерживают отмену.

## Файлы

- `ServerEntry.cs` — запись сервера.
- `ServerPingResult.cs` — результат ping и `PlayerSample`.
- `ServerPinger.cs` — status ping (handshake → status request → ping/pong).
- `ServerListService.cs` — чтение/запись `servers.dat` и операции со списком.

## API

### ServerEntry

```csharp
public sealed class ServerEntry
{
    public string Name { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }            // по умолчанию 25565
    public bool IsFavorite { get; set; }
    public DateTimeOffset? LastPlayed { get; set; }
    public string? IconBase64 { get; set; }  // PNG base64 без data URI
    public string Notes { get; set; }
    public string Address { get; }           // host или host:port
}
```

### ServerPinger

```csharp
var pinger = new ServerPinger();

ServerPingResult result = await pinger.PingAsync(
    "mc.example.org",              // допускается "mc.example.org:25566"
    port: 25565,
    timeout: TimeSpan.FromSeconds(8),
    ct: cancellationToken);
```

`ServerPingResult` содержит `Online`, `VersionName`, `Protocol`, `PlayersOnline`,
`PlayersMax`, `Motd` (без §-кодов), `Favicon` (base64 без префикса),
`LatencyMs`, `Error` и `Players`.

Ошибки сети и таймауты не выбрасываются: возвращается `Online = false` с текстом
в `Error`. `OperationCanceledException` пробрасывается только при отмене
переданного `CancellationToken`.

Таймауты: подключение — 5 с, чтение — 5 с, общий — 8 с (по умолчанию).
Handshake использует protocol 763 (Minecraft 1.20.1); серверы отвечают на status
независимо от версии протокола.

### ServerListService

```csharp
var service = new ServerListService();

IReadOnlyList<ServerEntry> servers = service.Load(instance);
service.Save(instance, servers);

service.AddOrUpdate(instance, new ServerEntry
{
    Name = "Hypixel",
    Host = "mc.hypixel.net"
});

bool removed = service.Remove(instance, "mc.hypixel.net");
service.Move(instance, fromIndex: 0, toIndex: 1);
```

Все методы работают с `<GameDir инстанса>\servers.dat`. `Load` возвращает пустой
список, если файла нет, и `LauncherException`, если файл повреждён.

## Формат servers.dat

Ванильный формат Minecraft:

- gzip-сжатый NBT;
- корневой `TAG_Compound` (пустое имя);
- `servers` — `TAG_List` из `TAG_Compound`;
- поля сервера: `name` (`TAG_String`), `ip` (`TAG_String`),
  `icon` (`TAG_String`, data URI PNG), `acceptTextures` (`TAG_Byte`).

`ip` хранится как `host`, `host:port` или `[ipv6]:port`; порт 25565 опускается.
Запись идёт во временный файл с последующей атомарной заменой.

## Ограничения

- **SRV-записи не поддерживаются.** Нужен явный `host:port` либо стандартный
  порт 25565. Внешние процессы (`nslookup`, `Resolve-DnsName`) не используются.
- `IsFavorite`, `LastPlayed` и `Notes` — поля лаунчера; в ванильном `servers.dat`
  для них нет места, поэтому они не сохраняются. При необходимости их стоит
  хранить отдельно (например, в настройках инстанса).
- При сохранении пишутся только известные поля. Неизвестные теги и поля,
  добавленные модами/другими лаунчерами, теряются.
- Иконка нормализуется до base64 без префикса; при записи префикс
  `data:image/png;base64,` добавляется обратно.
- Ping не проверяет SRV, не выполняет запросы legacy (pre-1.7) и не
  поддерживает Bedrock Edition.
