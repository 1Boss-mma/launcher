# Модуль каналов обновления (SamaraLauncher.Core.Channels)

Модуль проверяет `latest.json` каналов `stable` / `beta` / `dev`, выбирает
подходящий релиз, скачивает exe с проверкой SHA-256 и размера, а также хранит
предыдущий `Launcher.exe` для отката. Только BCL: без NuGet, WPF и внешних
процессов.

## Файлы

- `UpdateChannel.cs` — enum `UpdateChannel` и разбор `stable/beta/dev`.
- `SemVer.cs` — лёгкий SemVer 2.0.0: разбор, `CompareTo`, `IsGreaterThan`,
  `TryParse`, `Parse`.
- `ChannelManifest.cs` — модель релиза и проверка `MinVersion`.
- `ChannelService.cs` — загрузка манифеста, выбор релиза, скачивание exe.
- `UpdateRollbackService.cs` — сохранение и восстановление предыдущего exe.

Пространство имён: `SamaraLauncher.Core.Channels`.

## API

```csharp
var service = new ChannelService(paths, httpClient); // оба аргумента необязательны

ChannelManifest? manifest = await service.CheckAsync(
    "https://updates.example.org/launcher/latest.json",
    LauncherVersion.Current,
    UpdateChannel.Stable,
    ct);

string downloadPath = await service.DownloadAsync(manifest, progress, ct);

ChannelManifest? checkedAndDownloaded = await service.CheckAndDownloadAsync(
    "https://updates.example.org/launcher/latest.json",
    LauncherVersion.Current,
    UpdateChannel.Beta,
    progress,
    ct);
```

`CheckAsync` возвращает `null`, если новее текущей версии ничего нет или все
релизы канала несовместимы по `minVersion`. Ошибки манифеста и валидации —
`LauncherException` с русским текстом.

```csharp
var rollback = new UpdateRollbackService(paths);

await rollback.SaveCurrentAsync(launcherExePath, "0.2.1", UpdateChannel.Stable, ct);

string? backup = await rollback.GetRollbackAsync(ct); // null — отката нет
string restored = await rollback.RestoreAsync(targetLauncherExePath, ct);
```

## Формат latest.json

Поддерживаются три формы:

1. Объект с массивом `releases` (основной вариант).
2. Один объект релиза.
3. Корневой массив релизов (совместимость).

```json
{
  "releases": [
    {
      "version": "0.3.0",
      "channel": "stable",
      "url": "https://updates.example.org/launcher/Launcher-0.3.0.exe",
      "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "size": 73400320,
      "notes": "Исправлены ошибки запуска.",
      "mandatory": false,
      "minVersion": "0.2.0",
      "publishedAt": "2026-10-01T12:00:00Z"
    },
    {
      "version": "0.4.0-beta.2",
      "channel": "beta",
      "url": "https://updates.example.org/launcher/Launcher-0.4.0-beta.2.exe",
      "sha256": "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
      "size": 74123456,
      "notes": "Бета-сборка с новым менеджером сборок.",
      "mandatory": false,
      "minVersion": "0.3.0",
      "publishedAt": "2026-10-15T09:30:00Z"
    },
    {
      "version": "0.5.0-dev.1",
      "channel": "dev",
      "url": "https://updates.example.org/launcher/Launcher-0.5.0-dev.1.exe",
      "sha256": "89abcdef0123456789abcdef0123456789abcdef0123456789abcdef01234567",
      "size": 75000000,
      "notes": "Ночная сборка.",
      "mandatory": false,
      "minVersion": "0.4.0",
      "publishedAt": "2026-10-25T23:00:00Z"
    }
  ]
}
```

Один объект:

```json
{
  "version": "0.4.0-beta.2",
  "channel": "beta",
  "url": "Launcher-0.4.0-beta.2.exe",
  "sha256": "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
  "size": 74123456,
  "notes": "Бета-сборка.",
  "mandatory": true,
  "minVersion": "0.3.0",
  "publishedAt": "2026-10-15T09:30:00Z"
}
```

### Поля

| Поле | Обязательно | Описание |
| --- | --- | --- |
| `version` | да | SemVer `MAJOR.MINOR.PATCH[-prerelease][+build]`. |
| `channel` | нет | `stable`, `beta` или `dev` без учёта регистра. Если не указан — считается каналом запроса. |
| `url` | да | `http(s)`-адрес exe; относительный URL разрешается относительно `latest.json`, итоговая схема обязана быть `http`/`https`. |
| `sha256` | да | Ровно 64 hex-символа, регистр не важен. Допускается префикс `sha256:`. |
| `size` | да | Положительный размер exe в байтах. |
| `notes` | нет | Текст заметок к релизу. |
| `mandatory` | нет | `true`, если обновление обязательное. По умолчанию `false`. |
| `minVersion` | нет | Минимальная текущая версия, с которой разрешён релиз. Если текущая версия меньше — релиз пропускается. |
| `publishedAt` | нет | Дата ISO 8601, например `2026-10-01T12:00:00Z`. |

Имена полей в JSON читаются без учёта регистра (`Version`, `SHA256` и т. п.
тоже подойдут).

### Выбор канала и версии

- `Stable` — только релизы `stable`;
- `Beta` — `stable` и `beta` (стабильный релиз новее беты тоже подходит);
- `Dev` — все каналы.

Среди подходящих релизов выбирается максимальная SemVer-версия, которая
строго новее текущей и совместима по `minVersion`. При равной версии
предпочтение отдаётся `stable`, затем `beta`, затем `dev`.

`minVersion` — это минимальная *установленная* версия лаунчера, а не версия
обновления. Например, для релиза `0.5.0` с `minVersion: "0.3.0"` обновление
предлагается только с `0.3.0` и выше. Если все релизы канала несовместимы,
`CheckAsync` вернёт `null`; промежуточные версии нужно публиковать отдельно.

## SemVer

Поддерживается `MAJOR.MINOR.PATCH` с необязательными prerelease и build:

- `0.2.1 < 0.3.0 < 1.0.0`;
- `1.0.0-alpha < 1.0.0-alpha.1 < 1.0.0-alpha.beta < 1.0.0-beta < 1.0.0-beta.2 < 1.0.0-beta.11 < 1.0.0-rc.1 < 1.0.0`;
- числовые prerelease-идентификаторы сравниваются как числа, текстовые — лексикографически;
- build-метаданные (`+sha.abc`) сохраняются, но на сравнение не влияют;
- ведущие нули в `MAJOR/MINOR/PATCH` и числовых prerelease-идентификаторах запрещены;
- допускается ведущий `v` (`v1.2.3`).

`SemVer.IsGreaterThan`, `CompareTo` и `SemVer.Compare` реализуют это сравнение
без NuGet-пакетов.

## Скачивание

Файл сохраняется в:

```
%LOCALAPPDATA%\SamaraLauncher\updates\Launcher-<version>-<channel>.exe.download
```

Примеры: `Launcher-0.3.0-stable.exe.download`,
`Launcher-0.4.0-beta.2-beta.exe.download`.

Порядок:

1. `DownloadService` скачивает во временный `.tmp` с повторами.
2. Проверяются SHA-256 и размер из манифеста.
3. Временный файл атомарно перемещается в `.download`.
4. Если `.download` уже существует и совпадает по хешу и размеру, повторно не
   скачивается.

`progress` получает `LauncherProgress` со стадией `Update`. Сетевые операции
асинхронные, поддерживают `CancellationToken`; проверка манифеста ограничена
30 секундами, у HttpClient есть общий таймаут.

## Откат

`SaveCurrentAsync` копирует текущий exe в:

```
%LOCALAPPDATA%\SamaraLauncher\updates\rollback\Launcher-<version>.exe
```

и атомарно пишет `rollback.json`:

```json
{
  "version": "0.2.1",
  "channel": "stable",
  "savedAt": "2026-10-31T12:00:00.0000000+00:00",
  "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
}
```

`GetRollbackAsync` возвращает путь к этой копии или `null`, если
`rollback.json` ещё нет. Если `rollback.json` есть, а файл копии отсутствует,
выбрасывается `LauncherException`. Целостность копии проверяется в
`RestoreAsync`. `RestoreAsync`:

1. проверяет SHA-256 резервной копии по `rollback.json`;
2. копирует текущий `targetLauncherExePath` в
   `updates\rollback\previous\Launcher-<timestamp>.exe`;
3. копирует резервную копию во временный файл рядом с целью, ещё раз проверяет
   SHA-256 и атомарно заменяет цель;
4. не удаляет исходную резервную копию, поэтому откат можно повторить.

Сервис не запускает процессы и не пытается заменить exe текущего процесса:
если `targetLauncherExePath` совпадает с `Environment.ProcessPath`,
выбрасывается `LauncherException` с просьбой закрыть лаунчер.

## Ограничения

- Применение обновления и перезапуск не входят в модуль: это задача
  `LauncherUpdateService` / `Launcher.Updater.exe`. `ChannelService` только
  проверяет и скачивает `.exe.download`.
- Проверяется SHA-256, но не цифровая подпись exe. Подпись нужно проверять
  отдельно, если она используется при публикации.
- Нет delta-обновлений, ETag/кэширования манифеста и докачки по HTTP Range.
- `size` в манифесте обязателен; без него релиз считается некорректным.
- Работающий exe нельзя заменить средствами файловой системы Windows.
  `RestoreAsync` заранее отказывается только для текущего процесса; если
  запущен другой экземпляр лаунчера, замена завершится ошибкой ввода-вывода с
  подсказкой закрыть лаунчер.
- Старые файлы `rollback\Launcher-*.exe` и `rollback\previous\*` автоматически
  не удаляются.
- `latest.json` читается целиком в память; ограничения на размер ответа нет.
- Модуль кроссплатформенный по коду, но пути и сценарий замены exe рассчитаны
  на Windows.
