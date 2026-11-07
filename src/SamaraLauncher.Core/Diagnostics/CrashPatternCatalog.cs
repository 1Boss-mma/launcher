namespace SamaraLauncher.Core.Diagnostics;

/// <summary>
/// Встроенная база шаблонов Crash Analyzer 2.0. Все выражения регистронезависимые,
/// содержат технический признак проблемы и готовую рекомендацию.
/// </summary>
public static class CrashPatternCatalog
{
    /// <summary>Все встроенные шаблоны в порядке приоритета отчёта.</summary>
    public static IReadOnlyList<CrashPattern> All { get; } = Array.AsReadOnly(new CrashPattern[]
    {
        new(
            "out-of-memory",
            "OutOfMemoryError — нехватка памяти",
            CrashPatternCategory.Memory,
            @"\bOutOfMemoryError\b",
            "Увеличьте выделенную память (например, до 6144–8192 МБ), закройте лишние программы " +
            "и убедитесь, что аргумент -Xmx действительно применяется к игре.",
            CrashSeverity.Fatal),
        new(
            "java-heap-space",
            "Java heap space — переполнение кучи",
            CrashPatternCategory.Memory,
            @"Java heap space|GC overhead limit exceeded",
            "Игре не хватает памяти: увеличьте -Xmx в настройках профиля, уменьшите дальность прорисовки " +
            "и отключите тяжёлые ресурспаки.",
            CrashSeverity.Error),
        new(
            "no-such-method",
            "NoSuchMethodError — несовместимый метод",
            CrashPatternCategory.Java,
            @"\bNoSuchMethodError\b",
            "Обновите или откатите мод, в стеке которого возникла ошибка, и проверьте, что его версия " +
            "совместима с вашей версией Minecraft и Forge.",
            CrashSeverity.Error),
        new(
            "class-not-found",
            "ClassNotFoundException — класс не найден",
            CrashPatternCategory.Java,
            @"\bClassNotFoundException\b",
            "Проверьте, что все зависимости мода установлены, а jar не повреждён; при необходимости " +
            "переустановите мод и его библиотеки.",
            CrashSeverity.Error),
        new(
            "no-class-def-found",
            "NoClassDefFoundError — класс недоступен",
            CrashPatternCategory.Java,
            @"\bNoClassDefFoundError\b",
            "Обычно не хватает зависимости или jar повреждён. Переустановите мод из стека и проверьте " +
            "наличие обязательных библиотек.",
            CrashSeverity.Error),
        new(
            "mixin-apply-failed",
            "Mixin apply failed — ошибка применения миксина",
            CrashPatternCategory.Mods,
            @"Mixin apply (?:for mod .{0,160} )?failed|MixinApplyError|Mixin .{0,120} failed to apply",
            "Отключите мод, указанный в блоке Mixin, или обновите его до версии для вашей сборки; " +
            "проверьте конфликты с другими модами.",
            CrashSeverity.Error),
        new(
            "mixin-transformation",
            "Mixin transformation failed — ошибка трансформации",
            CrashPatternCategory.Mods,
            @"Mixin transformation of .{0,160} failed|MixinTransformerError|Mixin transformation|" +
            @"Mixin .{0,120} transformer .{0,80}(?:error|failed)",
            "Обновите Forge и мод, чей миксин не трансформируется; удалите мод, если он не поддерживает " +
            "текущую версию игры.",
            CrashSeverity.Error),
        new(
            "duplicate-mods",
            "Дубликаты модов (duplicate mod id)",
            CrashPatternCategory.Mods,
            @"Duplicate mods?(?: found)?|DuplicateModsFoundException|duplicate mod id|" +
            @"mod id .{0,80} is already (?:registered|in use)|Found duplicate mod",
            "Удалите дубликаты модов, оставив ровно одну версию каждого мода; проверьте папку mods " +
            "на копии с суффиксами (1), copy и т.п.",
            CrashSeverity.Error),
        new(
            "missing-dependency",
            "Отсутствует зависимость мода (missing dependency)",
            CrashPatternCategory.Mods,
            @"Missing or unsupported mandatory dependencies|MissingDependenciesException|Missing dependency|" +
            @"Mod .{0,120} requires .{0,120} (?:which is missing|that is not installed)|" +
            @"requires .{0,80} which is missing",
            "Установите недостающую зависимость, указанную в отчёте, или отключите мод, который её требует.",
            CrashSeverity.Error),
        new(
            "incompatible-mods",
            "Несовместимые моды (incompatible mods)",
            CrashPatternCategory.Mods,
            @"Incompatible mods|IncompatibleModSetException|incompatible mod set|" +
            @"mod .{0,120} is incompatible|conflicting mods",
            "Отключите несовместимые моды или замените их версиями, которые поддерживают вашу сборку " +
            "Minecraft и Forge.",
            CrashSeverity.Error),
        new(
            "glfw-error",
            "GLFW error — ошибка окна",
            CrashPatternCategory.Graphics,
            @"GLFW error|GLFWError|Failed to initialize GLFW|" +
            @"glfw(?:CreateWindow|Init|WindowHint).{0,100}(?:failed|error)|Error initializing GLFW",
            "Обновите драйверы видеокарты и проверьте установку OpenGL; попробуйте запустить игру " +
            "без шейдеров и оверлеев.",
            CrashSeverity.Fatal),
        new(
            "opengl-pixel-format",
            "OpenGL / Pixel format — ошибка графики",
            CrashPatternCategory.Graphics,
            @"Pixel format .{0,80}(?:not supported|unsupported)|Failed to create (?:an )?OpenGL|" +
            @"OpenGL .{0,80}(?:error|failed|not supported)|GL_INVALID_(?:ENUM|VALUE|OPERATION)|" +
            @"Unsupported pixel format",
            "Обновите драйвер видеокарты. Если используется удалённый рабочий стол или виртуальная машина, " +
            "включите программный рендеринг либо смените разрешение и формат пикселей.",
            CrashSeverity.Error),
        new(
            "access-violation",
            "EXCEPTION_ACCESS_VIOLATION — сбой нативной памяти",
            CrashPatternCategory.Graphics,
            @"EXCEPTION_ACCESS_VIOLATION",
            "Обновите драйвер GPU и Java; отключите моды, добавляющие нативный код (рендер-моды, шейдеры), " +
            "и проверьте целостность файлов игры.",
            CrashSeverity.Fatal),
        new(
            "tesselating-model",
            "Tesselating block model — ошибка модели",
            CrashPatternCategory.Graphics,
            @"Tesselating block model|Tesselating .{0,60}model .{0,60}(?:exception|crash|error)",
            "Отключите ресурспаки и моды, добавляющие блоки или модели; проверьте, что ресурспак совместим " +
            "с версией игры.",
            CrashSeverity.Warning),
        new(
            "stack-overflow",
            "StackOverflowError — переполнение стека",
            CrashPatternCategory.Java,
            @"\bStackOverflowError\b",
            "Отключите недавно добавленные моды: вероятна рекурсия в их коде. Как временная мера можно " +
            "увеличить размер стека аргументом -Xss.",
            CrashSeverity.Error),
        new(
            "concurrent-modification",
            "ConcurrentModificationException — изменение коллекции",
            CrashPatternCategory.Mods,
            @"\bConcurrentModificationException\b",
            "Обновите моды, которые изменяют реестры или списки во время игры; отключите недавно " +
            "добавленный мод.",
            CrashSeverity.Warning),
        new(
            "registry-remapping",
            "Registry remapping failed — ошибка реестров",
            CrashPatternCategory.Mods,
            @"Registry remapping failed|Registry (?:is )?already frozen|" +
            @"Registry .{0,120}(?:remap|freeze) .{0,80}failed",
            "Проверьте совместимость модов, работающих с реестрами, удалите дубликаты и обновите Forge; " +
            "помогает запуск с минимальным набором модов.",
            CrashSeverity.Error),
        new(
            "failed-to-load-mod",
            "Failed to load mod — мод не загрузился",
            CrashPatternCategory.Mods,
            @"Failed to load mod(?:s| file| class)?\b|ModLoaderException|Error loading mod|Unable to load mod",
            "Найдите имя мода в отчёте, переустановите его jar и проверьте обязательные зависимости.",
            CrashSeverity.Error),
        new(
            "unsupported-java",
            "Unsupported class file major version — неподходящая Java",
            CrashPatternCategory.Java,
            @"Unsupported class file major version|UnsupportedClassVersionError|" +
            @"compiled by a more recent version of the Java Runtime|requires (?:at least )?Java \d+|" +
            @"Java .{0,60}(?:too old|is not supported|not compatible)",
            "Установите Java 17 (для Minecraft 1.20.1) или ту версию, которую требует сборка, и укажите " +
            "её в настройках профиля.",
            CrashSeverity.Error),
        new(
            "missing-file",
            "FileNotFoundException / отсутствует ассет",
            CrashPatternCategory.Files,
            @"FileNotFoundException|Missing asset|Unable to load asset|Asset .{0,80} not found|" +
            @"resource .{0,80} not found",
            "Проверьте целостность файлов игры и ресурспаков; переустановите отсутствующий файл или ассет.",
            CrashSeverity.Warning),
        new(
            "access-denied",
            "AccessDeniedException — нет доступа к файлу",
            CrashPatternCategory.Files,
            @"AccessDeniedException|Access is denied|отказано в доступе",
            "Запустите лаунчер под пользователем с правами на папку игры или перенесите инстанс в папку " +
            "без ограничений (например, в Документы).",
            CrashSeverity.Warning),
        new(
            "corrupt-jar",
            "ZipException — повреждённый jar",
            CrashPatternCategory.Files,
            @"ZipException|zip END header not found|invalid LOC header|corrupt(?:ed)? (?:jar|zip)|" +
            @"error in opening zip file",
            "Переустановите повреждённый jar: удалите его из mods и скачайте мод заново с официального источника.",
            CrashSeverity.Error),
        new(
            "invalid-mod-file",
            "mod file is not a valid mod file — некорректный мод",
            CrashPatternCategory.Mods,
            @"is not a valid mod file|InvalidModFileException|invalid mod file|Not a mod file",
            "Удалите файл из mods и скачайте мод заново; убедитесь, что это jar для вашей версии игры, " +
            "а не установщик или архив.",
            CrashSeverity.Error),
        new(
            "optifine-oculus",
            "OptiFine / Oculus — конфликт",
            CrashPatternCategory.Graphics,
            @"OptiFine.{0,200}(?:Oculus|Iris)|(?:Oculus|Iris).{0,200}OptiFine|" +
            @"(?:Oculus|Iris).{0,100}not compatible with OptiFine|" +
            @"OptiFine.{0,100}not compatible with (?:Oculus|Iris)",
            "OptiFine несовместим с Oculus/Iris. Оставьте только один из них и используйте совместимые шейдеры.",
            CrashSeverity.Error),
        new(
            "sodium-embeddium",
            "Sodium / Embeddium — конфликт",
            CrashPatternCategory.Graphics,
            @"Sodium.{0,200}(?:Embeddium|Rubidium)|(?:Embeddium|Rubidium).{0,200}Sodium|" +
            @"Embeddium instance tainted by mods|Multiple rendering mods|" +
            @"(?:Sodium|Embeddium|Rubidium).{0,100}not compatible with",
            "Sodium и Embeddium/Rubidium конфликтуют. Оставьте только один оптимизатор рендера и проверьте " +
            "совместимость модов с ним.",
            CrashSeverity.Error),
        new(
            "shader-renderer",
            "EntityRenderer / шейдеры — ошибка рендера",
            CrashPatternCategory.Graphics,
            @"EntityRenderer.{0,160}(?:exception|error|crash)|Rendering overlay|" +
            @"Shader(?:s|Exception)?.{0,100}(?:failed|error|crash)|GLSL.{0,100}error|" +
            @"Failed to compile (?:shader|program)",
            "Отключите шейдеры и ресурспаки, проверьте совместимость Iris/Oculus с другими модами рендера " +
            "и обновите графические моды.",
            CrashSeverity.Warning),
        new(
            "watchdog",
            "Watchdog / server tick — зависание",
            CrashPatternCategory.Performance,
            @"\bWatchdog\b|A single server tick took|server tick loop|ServerHangWatchdog|" +
            @"Timed out waiting for (?:the )?server",
            "Найдите мод, вызывающий зависание серверного тика: отключите тяжёлые моды и проверьте логи " +
            "на последние действия перед зависанием.",
            CrashSeverity.Fatal),
        new(
            "no-chat-reports",
            "No Chat Reports / PlatformProvider.getConfigDir — сбой",
            CrashPatternCategory.Mods,
            @"(?s)Suspected Mods?:.{0,200}No Chat Reports|PlatformProvider\.getConfigDir|" +
            @"NCRConfig.{0,120}(?:exception|error)|" +
            @"NoChatReports.{0,200}(?:exception|error)|No Chat Reports.{0,160}(?:crash|failed|error)",
            "Обновите No Chat Reports до версии для вашей сборки или удалите мод; проверьте, что он " +
            "соответствует версии Minecraft и Forge.",
            CrashSeverity.Error)
    });
}
