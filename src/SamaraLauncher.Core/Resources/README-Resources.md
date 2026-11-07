# Модуль ресурспаков и шейдерпаков (SamaraLauncher.Core.Resources)

Модуль для списка ресурспаков и шейдерпаков инстанса, их включения через
`options.txt` / `optionsshaders.txt`, импорта zip и удаления в бэкап.
Только BCL (`System.IO.Compression`, `System.Text.Json`, `System.Diagnostics`),
без NuGet и WPF.

## Файлы

- `PackInfo.cs` — `PackKind` и модель `PackInfo`.
- `PackIconReader.cs` — чтение `pack.png`, описания из `pack.mcmeta`,
  защита от zip-bomb.
- `ResourcePackService.cs` — папка `resourcepacks`, `options.txt`.
- `ShaderPackService.cs` — папка `shaderpacks`, `optionsshaders.txt`.
- `PackSupport.cs` — внутренние файловые операции и построчный редактор
  `options*.txt`.

## API

### PackInfo

```csharp
public enum PackKind
{
    ResourcePack,
    ShaderPack
}

public sealed class PackInfo
{
    public string FileName { get; set; }
    public string FullPath { get; set; }
    public long Size { get; set; }
    public bool Enabled { get; set; }
    public string Title { get; set; }        // имя без .zip или имя папки
    public string Description { get; set; }  // из pack.mcmeta
    public byte[]? IconPng { get; set; }     // pack.png, не более 4 МБ
    public string Format { get; set; }       // "zip" или "folder"
    public DateTimeOffset ModifiedAt { get; set; }
}
```

### PackIconReader

```csharp
byte[]? icon = PackIconReader.ReadIcon(
    @"C:\...\resourcepacks\MyPack.zip",
    PackKind.ResourcePack);
```

Для ресурспака читается `pack.png` в корне zip/папки. Для шейдерпака —
`pack.png` в корне, затем `shaders/pack.png`. Ошибки чтения и повреждённые
архивы не выбрасываются: возвращается `null`.

Ограничения zip-bomb:

- не более 100 000 записей (`MaxArchiveEntries`);
- суммарный распакованный размер не более 8 ГБ
  (`MaxArchiveUncompressedBytes`);
- `pack.png` — не более 4 МБ (`MaxIconBytes`);
- `pack.mcmeta` — не более 1 МБ;
- содержимое читается с жёстким лимитом, даже если размер в заголовке zip врёт.

### ResourcePackService

```csharp
var service = new ResourcePackService();
// new ResourcePackService(new LauncherPaths(appDataDir, localDataDir)) —
// для тестов и нестандартного размещения.

string folder = service.GetFolder(instance);       // <GameDir>\resourcepacks
IReadOnlyList<PackInfo> packs = service.List(instance);

IReadOnlyList<string> enabled = service.GetEnabled(instance);
service.SetEnabled(instance, "MyPack.zip", true);
service.SetEnabled(instance, "MyPack.zip", false);

IReadOnlyList<PackInfo> imported = service.Import(
    instance,
    new[] { @"C:\Downloads\MyPack.zip" },
    progress);                                     // IProgress<LauncherProgress>?

string backupPath = service.Delete(pack);          // в removed-resourcepacks
service.Delete(pack, moveToBackups: false);        // безвозвратно

service.OpenFolder(instance);                      // создаёт и открывает папку
```

`List` возвращает все `.zip` в папке и папки, содержащие `pack.mcmeta`.
Регистр имён не важен. Ошибка одного пака не прерывает список.

`Import` принимает только `.zip`, требует `pack.mcmeta` в корне архива,
не перезаписывает существующие файлы и **не включает** импортированный пак.
Копирование идёт через временный файл с последующим переименованием.
`LauncherProgress` сообщает стадию `ProgressStage.Pack`.

`Delete` по умолчанию переносит zip/папку в
`%LOCALAPPDATA%\SamaraLauncher\backups\removed-resourcepacks\<timestamp>\`.
Возвращает путь, куда перенесён пак (или исходный путь при безвозвратном
удалении). Список `resourcePacks` при удалении не меняется — при
необходимости сначала вызовите `SetEnabled(..., false)`.

### ShaderPackService

```csharp
var shaders = new ShaderPackService();

IReadOnlyList<PackInfo> packs = shaders.List(instance);
IReadOnlyList<string> active = shaders.GetEnabled(instance); // 0 или 1 имя

shaders.SetEnabled(instance, "Complementary.zip", true);
shaders.SetEnabled(instance, "Complementary.zip", false);    // shaderPack:OFF

var imported = shaders.Import(instance, new[] { zipPath }, progress);
shaders.Delete(pack);                    // removed-shaderpacks
shaders.OpenFolder(instance);
```

Для папки-шейдерпака достаточно `shaders/shaders.properties` или
`shaders/`; `pack.mcmeta` не обязателен (но поддерживается). Для zip при
импорте проверяется наличие `shaders/` или `pack.mcmeta`.

## Формат options.txt

Строка Minecraft:

```text
resourcePacks:["file/MyPack.zip","vanilla","file/FolderPack"]
```

- значение — JSON-массив строк;
- `file/` — префикс файлового пака, при чтении отбрасывается;
- `vanilla`, `programmer_art` и другие служебные имена сохраняются как есть;
- сравнение имён — без учёта регистра;
- меняется только строка `resourcePacks`; порядок, остальные строки,
  BOM, кодировка и переводы строк сохраняются;
- если ключа нет — строка добавляется в конец;
- если `options.txt` нет — создаётся при первом `SetEnabled`;
- перед изменением создаётся `options.txt.yyyyMMdd-HHmmss.bak`;
- запись — временный файл в той же папке и атомарный `File.Replace`;
- повреждённый JSON: `GetEnabled` возвращает пустой список,
  `SetEnabled` выбрасывает `LauncherException` и не трогает файл.

Поддерживается UTF-8 (с BOM и без), UTF-16 LE/BE и UTF-32 LE/BE с BOM.
Файл без BOM считается UTF-8. Если в файле смешаны переводы строк, при
записи используется первый найденный вариант.

## Формат optionsshaders.txt

```text
shaderPack:ComplementaryReimagined_r5.2.2.zip
```

- активен только один шейдерпак;
- выключение активного пака записывает `shaderPack:OFF`;
- пустое значение и `OFF` означают, что шейдеры выключены;
- если ключа нет — добавляется в конец; если файла нет — создаётся;
- остальные строки, порядок, кодировка и переводы строк сохраняются;
- `.bak` и атомарная замена — как у `options.txt`.

## Ограничения

- Поддерживается стандартная раскладка zip: `pack.mcmeta` / `shaders` в
  корне. Zip с единственной вложенной папкой верхнего уровня не
  распознаётся как пак.
- Совместимость `pack_format` с версией Minecraft не проверяется.
- Содержимое паков не распаковывается и не редактируется.
- `pack.png` не декодируется и не проверяется на корректность PNG;
  просто возвращаются байты до 4 МБ.
- Паки больше 8 ГБ распакованного размера или с более чем 100 000 записей
  считаются подозрительными и не дают иконку/метаданные.
- `Size` папки-пака считается рекурсивно и может быть медленным для
  очень больших папок.
- Бэкапы удалённых паков не чистятся автоматически.
- `Delete` не меняет `options.txt` / `optionsshaders.txt`.
- `Import` не включает пак и не проверяет совместимость с загрузчиком шейдеров.
- Прогресс `LauncherProgress` есть только у `Import`; `List` и `Delete`
  синхронные.

## Сборка

```powershell
dotnet build `
  'src\SamaraLauncher.Core\SamaraLauncher.Core.csproj' `
  -c Debug -p:TreatWarningsAsErrors=true
```
