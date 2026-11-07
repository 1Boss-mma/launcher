# Doctor — диагностика инстанса

Модуль `SamaraLauncher.Core.Diagnostics` проверяет готовность сборки к запуску и умеет
безопасно создавать служебные папки и удалять временные файлы. Только BCL, без NuGet и WPF.

## Файлы

| Файл | Назначение |
| --- | --- |
| `DoctorCheck.cs` | Один пункт проверки: `Id`, `Title`, `Category`, `Status`, `Message`, `Details`, `CanAutoFix`, `FixHint`. |
| `DoctorReport.cs` | Отчёт: `StartedAt`, `Duration`, `Checks`, `Errors`, `Warnings`, `IsHealthy`, `Summary`, `ToText()`, `ToJson()`. |
| `DoctorService.cs` | `RunAsync(...)` — выполняет все проверки. |
| `DoctorFixService.cs` | `FixAsync(...)` — безопасные автоисправления по отчёту. |

## Быстрый старт

```csharp
using SamaraLauncher.Core.Diagnostics;
using SamaraLauncher.Core.Models;

var doctor = new DoctorService();
var report = await doctor.RunAsync(instance, progress, ct);

Console.WriteLine(report.Summary);
Console.WriteLine(report.ToText());

var json = report.ToJson(); // camelCase + indented, кириллица не экранируется

// Только если пользователь согласился:
var fixer = new DoctorFixService();
var fixedCount = await fixer.FixAsync(instance, report, progress, ct);
```

`RunAsync` не бросает исключений из-за проблем клиента: каждая проверка изолирована и
возвращает `Error`/`Warning`/`Skipped`. Исключение `LauncherException` — только фатальные
случаи (`FixAsync` без `GameDir`).

## Проверки

| Id | Категория | Что проверяет |
| --- | --- | --- |
| `java` | `Java` | Поиск Java 17+ через `JavaManager.FindFor`, существование `java.exe`, версию и major. |
| `gamedir.exists`, `gamedir.writable`, `gamedir.system` | `Files` | Папка игры существует, доступна на запись (temp-файл), не лежит в Windows/Program Files/корне диска. |
| `versions.main`, `versions.parent` | `Files` | `versions\<VersionId>\<VersionId>.json` существует и читается; родительская версия (`inheritsFrom`) на месте. |
| `versions.forge` | `Files` | Для Forge: jar'ы в `libraries\net\minecraftforge\forge` или forge-json. |
| `files.libraries`, `files.assets` | `Files` | Файлы в `libraries`, индексы и объекты в `assets`. |
| `disk.free` | `Disk` | Свободное место: < 2 ГБ — `Error`, < 5 ГБ — `Warning`. |
| `mods.present`, `mods.duplicates`, `mods.analysis` | `Mods` | Наличие jar'ов, дубликаты по имени без версии, `ModAnalyzer` (зависимости/дубликаты). |
| `config.packwiz`, `config.packurl` | `Config` | Найден `packwiz-installer-bootstrap.jar`; `PackUrl` задан и (для локального пути) существует. |
| `config.json`, `config.memory` | `Config` | `config.json` читается; `MemoryMb` в 1024..32768, `MinMemoryMb <= MemoryMb`. |
| `logs.crash` | `Logs` | Свежий crash-report (< 7 дней) — `Warning` с именем файла. |
| `network.mojang` | `Network` | HEAD/GET к `launchermeta.mojang.com`, timeout 10 с. Любая ошибка — только `Warning`. |
| `files.directories`, `files.temp` | `Files` | Служебные папки и `*.tmp` в versions/libraries/assets; поддерживают автоисправление. |

## Report API

- `Errors` — количество проверок со статусом `Error`.
- `Warnings` — количество проверок со статусом `Warning`.
- `IsHealthy` — `Errors == 0` (предупреждения допустимы).
- `Summary` — короткий итог по-русски.
- `ToText()` — многострочный отчёт для лога или текстового поля.
- `ToJson()` — `System.Text.Json`, camelCase, `WriteIndented`, enum'ы строками.

## Автоисправления

`DoctorFixService.FixAsync` исправляет только пункты с `CanAutoFix == true` и статусом
`Error`/`Warning`:

- создаёт отсутствующий `GameDir`;
- создаёт пустые `logs`, `crash-reports`, `mods`, `config`;
- удаляет `*.tmp` в `versions`, `libraries`, `assets`.

Не удаляются и не изменяются моды, миры, конфиги, `servers.dat` и другие файлы
пользователя. Возвращается число исправленных пунктов (не файлов): созданная папка игры,
служебные папки и очистка temp считаются отдельными пунктами.

## Как показать в UI

1. Запускать `RunAsync` в фоне (метод сам использует `Task.Run` для I/O), прогресс —
   `LauncherProgress(ProgressStage.Verify, ...)`.
2. `report.Checks` привязать к списку: цвет по `Status` (Ok — зелёный, Warning — жёлтый,
   Error — красный, Skipped — серый), заголовок `Title`, текст `Message`, раскрываемые
   `Details`.
3. Показать `Summary`, рядом — счётчики `Errors`/`Warnings`.
4. Кнопку «Исправить» показывать, если есть `Checks.Any(c => c.CanAutoFix)`; после
   `FixAsync` повторить `RunAsync`.
5. `ToText()` удобно копировать в буфер обмена для поддержки, `ToJson()` — сохранять в файл.

## Ограничения

- Java ищется теми же средствами, что и при запуске; проверка может занять время.
- Сетевой пункт никогда не делает отчёт нездоровым — это только предупреждение.
- Проверка памяти использует переданный `InstanceProfile`, а не перечитывает его из `config.json`.
- `packwiz-installer-bootstrap.jar` ищется рядом с приложением и вверх по дереву каталогов
  (`ToolLocator`), поэтому в тестовом harness путь может отличаться от установленного лаунчера.
