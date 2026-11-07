# Crash Analyzer 2.0

Модуль `SamaraLauncher.Core.Diagnostics` разбирает crash-report или хвост `latest.log`,
сопоставляет текст с базой из 28 шаблонов, извлекает подозрительные моды и формирует
готовые рекомендации. Только BCL, без NuGet и без изменения файлов пользователя.

## Файлы

| Файл | Назначение |
| --- | --- |
| `CrashPattern.cs` | `CrashPattern`, `CrashPatternCategory`, скомпилированное regex-выражение с таймаутом. |
| `CrashPatternCatalog.cs` | Встроенная база шаблонов `CrashPatternCatalog.All`. |
| `CrashAnalysisResult.cs` | `CrashAnalysisResult`, `CrashSeverity` и `ToText()`. |
| `CrashAnalyzer.cs` | Поиск и разбор отчётов, извлечение модов, рекомендации, экспорт. |

## Быстрый старт

```csharp
using SamaraLauncher.Core.Diagnostics;
using SamaraLauncher.Core.Models;

var analyzer = new CrashAnalyzer();

// Сначала crash-report, затем — запасной вариант по latest.log.
var result = analyzer.AnalyzeLatest(instance)
             ?? analyzer.AnalyzeLatestLog(instance);

if (result is not null)
{
    Console.WriteLine(result.ToText());

    var reportPath = analyzer.ExportReport(
        result,
        Path.Combine(instance.GameDir, "crash-analysis.txt"));

    Console.WriteLine($"Отчёт сохранён: {reportPath}");
}
```

`AnalyzeLatest` не бросает исключение, если папки игры или `crash-reports` нет — просто
возвращает `null`. `AnalyzeLatestLog` возвращает `null`, если `logs\latest.log` отсутствует
или пуст. Ошибки чтения существующего файла и некорректные пути приходят как
`LauncherException` с русским текстом.

## API

| Метод | Поведение |
| --- | --- |
| `CrashAnalysisResult? AnalyzeLatest(InstanceProfile instance)` | Ищет самый свежий `crash-reports\crash-*.txt` по `LastWriteTimeUtc` и разбирает его. |
| `CrashAnalysisResult AnalyzeFile(string crashFilePath)` | Разбирает указанный crash-report. |
| `CrashAnalysisResult? AnalyzeLatestLog(InstanceProfile instance)` | Разбирает последний ~1 МБ `logs\latest.log`; используется, когда crash-report нет. |
| `string ExportReport(CrashAnalysisResult result, string targetPath)` | Сохраняет `ToText()` в UTF-8 с BOM, создаёт родительскую папку, возвращает полный путь. |

`CrashAnalysisResult`:

- `CrashFile` — полный путь к проанализированному файлу;
- `Time` — `Time:` из отчёта, для лога — дата файла и последняя метка времени;
- `Description` — `Description:` или пояснение для лога;
- `Cause` — первая строка `Caused by:` либо первое исключение/ошибка;
- `SuspectedMods` — до 10 нормализованных имён без дублей;
- `Recommendations` — до 10 рекомендаций, последняя всегда про приложение отчёта;
- `MatchedPatterns` — названия совпавших шаблонов, сначала более важные;
- `Severity` — `Info`, `Warning`, `Error` или `Fatal`;
- `RawExcerpt` — заголовок отчёта и строки вокруг совпадений, не более ~24 КБ;
- `ToText()` — готовый многострочный отчёт.

## Шаблоны

Все выражения регистронезависимые. `CrashPattern.IsMatch` ограничен таймаутом 2 секунды:
при таймауте шаблон считается несовпавшим и анализ продолжается.

| Id | Категория | Уровень | Что ищет |
| --- | --- | --- | --- |
| `out-of-memory` | Memory | Fatal | `OutOfMemoryError` |
| `java-heap-space` | Memory | Error | `Java heap space`, `GC overhead limit exceeded` |
| `no-such-method` | Java | Error | `NoSuchMethodError` |
| `class-not-found` | Java | Error | `ClassNotFoundException` |
| `no-class-def-found` | Java | Error | `NoClassDefFoundError` |
| `mixin-apply-failed` | Mods | Error | `Mixin apply failed`, `MixinApplyError` |
| `mixin-transformation` | Mods | Error | `Mixin transformation`, `MixinTransformerError` |
| `duplicate-mods` | Mods | Error | duplicate mods / duplicate mod id |
| `missing-dependency` | Mods | Error | missing dependency / обязательная зависимость |
| `incompatible-mods` | Mods | Error | incompatible mods / conflicting mods |
| `glfw-error` | Graphics | Fatal | `GLFW error`, `Failed to initialize GLFW` |
| `opengl-pixel-format` | Graphics | Error | OpenGL error, `Pixel format not supported`, `GL_INVALID_*` |
| `access-violation` | Graphics | Fatal | `EXCEPTION_ACCESS_VIOLATION` |
| `tesselating-model` | Graphics | Warning | `Tesselating block model` |
| `stack-overflow` | Java | Error | `StackOverflowError` |
| `concurrent-modification` | Mods | Warning | `ConcurrentModificationException` |
| `registry-remapping` | Mods | Error | `Registry remapping failed`, `Registry is already frozen` |
| `failed-to-load-mod` | Mods | Error | `Failed to load mod`, `ModLoaderException` |
| `unsupported-java` | Java | Error | `Unsupported class file major version`, неподходящая Java |
| `missing-file` | Files | Warning | `FileNotFoundException`, missing asset/resource |
| `access-denied` | Files | Warning | `AccessDeniedException`, «отказано в доступе» |
| `corrupt-jar` | Files | Error | `ZipException`, corrupt jar/zip |
| `invalid-mod-file` | Mods | Error | `mod file is not a valid mod file` |
| `optifine-oculus` | Graphics | Error | OptiFine вместе с Oculus/Iris |
| `sodium-embeddium` | Graphics | Error | Sodium вместе с Embeddium/Rubidium, `Embeddium instance tainted` |
| `shader-renderer` | Graphics | Warning | EntityRenderer, ошибки шейдеров/GLSL |
| `watchdog` | Performance | Fatal | Watchdog, server tick, `ServerHangWatchdog` |
| `no-chat-reports` | Mods | Error | `No Chat Reports`, `PlatformProvider.getConfigDir`, `NCRConfig` |

## Подозрительные моды

Источники обрабатываются по приоритету, чтобы не засорять список модами из общего
стека загрузки:

1. блоки `Suspected Mod:` / `Suspected Mods:` и строки `Mod File:`;
2. `Failure message:` и следующие три строки;
3. заголовки секций `-- Mod <id> --`;
4. `modid=...` / `modid: ...` — если имён ещё нет;
5. имена `.jar` из строк stacktrace — только если других зацепок нет.

Нормализация: убираются `, Version: ...`, `Issue tracker URL`, служебные пути и хвосты
`jar%23330!/...`; остаётся имя мода или имя jar. Дубликаты сравниваются без учёта регистра,
платформенные jar (`minecraft`, `forge`, `client`, `fmlloader`, `modlauncher`, `mixin` и т.п.)
отбрасываются, список ограничен 10 элементами. Если подозрительный мод не найден, модуль
не придумывает имя и не советует отключать конкретный мод.

## Рекомендации

Рекомендации складываются из совпавших шаблонов и найденных модов:

- отключить подозрительный мод и запустить игру снова;
- обновить или откатить мод до совместимой версии;
- увеличить память JVM или обновить Java;
- удалить дубликат, установить зависимость, переустановить повреждённый jar;
- отключить шейдеры/ресурспаки при ошибках рендера;
- приложить отчёт и `latest.log` к обращению в поддержку.

Список дедуплицируется и ограничен 10 пунктами; рекомендация «приложите отчёт»
сохраняется последней.

## Устойчивость и ограничения

- Crash-report читается целиком до 2 МБ; у большего файла берутся первые и последние
  1 МБ с маркером пропущенной середины. `latest.log` читается только с конца, до 1 МБ.
- Кодировка определяется по первым 4 КБ: UTF-8 (с BOM или без) либо cp1251. Битые
  последовательности не приводят к исключению.
- Regex имеют таймаут 2 секунды; таймаут отдельного шаблона или извлечения модов не
  роняет анализ.
- Ошибки пути, отсутствующий файл и ошибки чтения/записи оборачиваются в
  `LauncherException`. Исключение — неверные аргументы конструктора `CrashPattern`
  (`ArgumentException`, это ошибка программиста, а не пользователя).
- `AnalyzeLatest` выбирает файл по времени изменения, а не по имени. Если игра ещё
  не падала и папки `crash-reports` нет, результат — `null`.
- `AnalyzeLatestLog` видит только хвост лога: начало длинного `latest.log` не анализируется.
- Разбор эвристический: шаблон может дать ложное совпадение, а незнакомая ошибка —
  остаться без шаблона. Полный отчёт всё равно стоит просматривать вручную.
- Модуль ничего не удаляет и не отключает: рекомендации выполняет пользователь.
- `ExportReport` перезаписывает существующий файл по указанному пути.
