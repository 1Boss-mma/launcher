using System.Globalization;
using System.Text.RegularExpressions;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Core.Mods;

/// <summary>
/// Проверка папки mods: дубликаты, обязательные зависимости, отключённые зависимости.
/// </summary>
public sealed class ModAnalyzer
{
    private static readonly HashSet<string> NonModDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft",
        "java",
        "forge",
        "neoforge",
        "javafml",
        "lowcodefml",
        "fabricloader",
        "fabric-loader",
        "fabric_loader",
        "quilt_loader",
        "quiltloader",
        "java_version"
    };

    private static readonly Regex VersionTokenRegex = new(
        @"(?<op>>=|<=|>|<|~|\^|=)?\s*(?<version>\d[0-9A-Za-z.\-_+]*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly ModService _mods;
    private readonly ModMetadataReader _reader;

    public ModAnalyzer(ModService? mods = null, ModMetadataReader? reader = null)
    {
        _mods = mods ?? new ModService();
        _reader = reader ?? new ModMetadataReader();
    }

    /// <summary>Сканирует mods инстанса, включая файлы <c>.jar.disabled</c>.</summary>
    public ModAnalysisReport Analyze(InstanceProfile instance)
    {
        var report = new ModAnalysisReport();
        var entries = _mods.List(instance);

        foreach (var entry in entries)
        {
            var metadata = _reader.Read(entry.FullPath, report.Warnings);
            if (metadata is null)
            {
                report.Warnings.Add($"Не удалось прочитать метаданные {entry.FileName}.");
                continue;
            }

            metadata.Enabled = entry.Enabled;
            report.Mods.Add(metadata);
        }

        report.Mods.Sort((left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.FileName, right.FileName));

        FindDuplicates(report);
        RecomputeDependencyIssues(report);

        return report;
    }

    private static void FindDuplicates(ModAnalysisReport report)
    {
        foreach (var group in report.Mods
                     .Where(mod => !string.IsNullOrWhiteSpace(mod.ModId))
                     .GroupBy(mod => mod.ModId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group
                         .Select(mod => mod.FileName)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .Count() > 1))
        {
            var files = group
                .Select(mod => mod.FileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            report.DuplicateModIds.Add(new DuplicateModId
            {
                ModId = group.Key,
                FileNames = files
            });
        }

        foreach (var group in report.Mods
                     .GroupBy(mod => mod.FileName, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            report.DuplicateFileNames.Add(group.Key);
        }
    }

    /// <summary>
    /// Пересчитывает проблемы зависимостей по текущему набору модов.
    /// Моды, вложенные в jar через JarJar или <c>META-INF/jars</c>, считаются
    /// доступными и включёнными вместе с родителем.
    /// </summary>
    public static void RecomputeDependencyIssues(ModAnalysisReport report)
    {
        report.MissingDependencies.Clear();
        report.DisabledDependencies.Clear();

        var enabledById = new Dictionary<string, List<ModMetadata>>(StringComparer.OrdinalIgnoreCase);
        var disabledById = new Dictionary<string, List<ModMetadata>>(StringComparer.OrdinalIgnoreCase);

        // Сначала корневые моды: если такой же modId вложен в другой jar,
        // приоритет у отдельного файла — JarJar не подменяет установленный мод.
        foreach (var mod in report.Mods)
        {
            AddToRegistry(mod, mod.Enabled, enabledById, disabledById);
        }

        foreach (var mod in report.Mods)
        {
            AddNestedToRegistry(mod, mod.Enabled, enabledById, disabledById);
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Зависимости отключённых модов не проверяем: они не будут загружены.
        foreach (var root in report.Mods.Where(mod => mod.Enabled))
        {
            foreach (var owner in EnumerateLoadedMods(root))
            {
                CheckDependencies(report, owner, root.FileName, enabledById, disabledById, seen);
            }
        }

        report.MissingDependencies.Sort(CompareIssues);
        report.DisabledDependencies.Sort(CompareIssues);
    }

    private static void AddToRegistry(
        ModMetadata mod,
        bool enabled,
        Dictionary<string, List<ModMetadata>> enabledById,
        Dictionary<string, List<ModMetadata>> disabledById)
    {
        if (string.IsNullOrWhiteSpace(mod.ModId))
        {
            return;
        }

        var registry = enabled ? enabledById : disabledById;
        if (!registry.TryGetValue(mod.ModId, out var list))
        {
            list = new List<ModMetadata>();
            registry[mod.ModId] = list;
        }

        list.Add(mod);
    }

    private static void AddNestedToRegistry(
        ModMetadata mod,
        bool parentEnabled,
        Dictionary<string, List<ModMetadata>> enabledById,
        Dictionary<string, List<ModMetadata>> disabledById)
    {
        foreach (var nested in mod.NestedMods)
        {
            var enabled = parentEnabled && nested.Enabled;
            AddToRegistry(nested, enabled, enabledById, disabledById);
            AddNestedToRegistry(nested, enabled, enabledById, disabledById);
        }
    }

    private static IEnumerable<ModMetadata> EnumerateLoadedMods(ModMetadata mod)
    {
        yield return mod;

        foreach (var nested in mod.NestedMods)
        {
            foreach (var item in EnumerateLoadedMods(nested))
            {
                yield return item;
            }
        }
    }

    private static void CheckDependencies(
        ModAnalysisReport report,
        ModMetadata owner,
        string ownerFileName,
        IReadOnlyDictionary<string, List<ModMetadata>> enabledById,
        IReadOnlyDictionary<string, List<ModMetadata>> disabledById,
        ISet<string> seen)
    {
        foreach (var dependency in owner.Dependencies)
        {
            if (string.IsNullOrWhiteSpace(dependency.ModId) ||
                NonModDependencies.Contains(dependency.ModId) ||
                string.Equals(dependency.ModId, owner.ModId, StringComparison.OrdinalIgnoreCase) ||
                IsServerOnly(dependency.Side) ||
                !dependency.Mandatory)
            {
                continue;
            }

            var key = string.Join(
                '|',
                ownerFileName,
                owner.ModId,
                dependency.ModId,
                dependency.VersionRange,
                dependency.Side);

            if (!seen.Add(key))
            {
                continue;
            }

            if (enabledById.TryGetValue(dependency.ModId, out var foundList))
            {
                if (foundList.Any(found => IsVersionCompatible(found.Version, dependency.VersionRange)))
                {
                    continue;
                }

                report.MissingDependencies.Add(
                    CreateIssue(owner, ownerFileName, dependency, foundList[0]));
                continue;
            }

            if (disabledById.TryGetValue(dependency.ModId, out var disabled))
            {
                foreach (var file in disabled)
                {
                    report.DisabledDependencies.Add(CreateIssue(owner, ownerFileName, dependency, file));
                }

                continue;
            }

            report.MissingDependencies.Add(CreateIssue(owner, ownerFileName, dependency, null));
        }
    }

    private static ModDependencyIssue CreateIssue(
        ModMetadata owner,
        string ownerFileName,
        ModDependency dependency,
        ModMetadata? found) =>
        new()
        {
            ModId = dependency.ModId,
            VersionRange = dependency.VersionRange,
            RequiredBy = owner.ModId,
            RequiredByFileName = ownerFileName,
            FoundVersion = found?.Version,
            FoundFileName = found?.FileName
        };

    private static int CompareIssues(ModDependencyIssue left, ModDependencyIssue right)
    {
        var byDependency = StringComparer.OrdinalIgnoreCase.Compare(left.ModId, right.ModId);
        return byDependency != 0
            ? byDependency
            : StringComparer.OrdinalIgnoreCase.Compare(left.RequiredByFileName, right.RequiredByFileName);
    }

    private static bool IsServerOnly(string side) =>
        string.Equals(side?.Trim(), "server", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Проверка версии по диапазону: Maven-подобные границы <c>[a,b)</c>,
    /// операторы <c>&gt;= &lt;= &gt; &lt; ~ ^ =</c>, условия через пробел и
    /// Fabric-альтернативы через <c>||</c>. Нераспознанные значения считаются совместимыми.
    /// </summary>
    private static bool IsVersionCompatible(string version, string range)
    {
        if (string.IsNullOrWhiteSpace(range))
        {
            return true;
        }

        var constraint = range.Trim();
        if (string.IsNullOrWhiteSpace(version))
        {
            // Версия неизвестна — не считаем зависимость отсутствующей.
            return true;
        }

        var candidate = ParseVersionParts(version);
        if (candidate is null)
        {
            return true;
        }

        var alternatives = constraint.Split(
            "||",
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (alternatives.Length > 1)
        {
            return alternatives.Any(alternative => IsRangeCompatible(candidate, alternative));
        }

        return IsRangeCompatible(candidate, constraint);
    }

    private static bool IsRangeCompatible(IReadOnlyList<VersionPart> candidate, string range)
    {
        var constraint = range.Trim();

        if (constraint.Length == 0 || constraint is "*" or "x" or "X")
        {
            return true;
        }

        if (constraint[0] is '[' or '(')
        {
            return IsBracketRangeCompatible(candidate, constraint);
        }

        return IsOperatorRangeCompatible(candidate, constraint);
    }

    private static bool IsBracketRangeCompatible(
        IReadOnlyList<VersionPart> candidate,
        string constraint)
    {
        var hasClosing = constraint[^1] is ']' or ')';
        var inner = constraint[1..(hasClosing ? constraint.Length - 1 : constraint.Length)];

        var comma = inner.IndexOf(',');
        if (comma < 0)
        {
            // [1.0.0] — точная версия.
            if (constraint[0] == '(')
            {
                return true;
            }

            var exact = ParseVersionParts(inner);
            return exact is null || CompareVersionParts(candidate, exact) == 0;
        }

        var lowerInclusive = constraint[0] == '[';
        var upperInclusive = hasClosing && constraint[^1] == ']';

        var lower = ParseVersionParts(inner[..comma]);
        var upper = ParseVersionParts(inner[(comma + 1)..]);

        if (lower is not null)
        {
            var comparison = CompareVersionParts(candidate, lower);
            if (lowerInclusive ? comparison < 0 : comparison <= 0)
            {
                return false;
            }
        }

        if (upper is not null)
        {
            var comparison = CompareVersionParts(candidate, upper);
            if (upperInclusive ? comparison > 0 : comparison >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsOperatorRangeCompatible(
        IReadOnlyList<VersionPart> candidate,
        string constraint)
    {
        var matches = VersionTokenRegex.Matches(constraint);
        if (matches.Count == 0)
        {
            return true;
        }

        foreach (Match match in matches)
        {
            var bound = ParseVersionParts(match.Groups["version"].Value);
            if (bound is null)
            {
                continue;
            }

            var comparison = CompareVersionParts(candidate, bound);

            switch (match.Groups["op"].Value)
            {
                case "<":
                    if (comparison >= 0)
                    {
                        return false;
                    }

                    break;

                case "<=":
                    if (comparison > 0)
                    {
                        return false;
                    }

                    break;

                case "=":
                    if (comparison != 0)
                    {
                        return false;
                    }

                    break;

                // "~", "^" и условие без оператора трактуем как нижнюю границу.
                default:
                    if (comparison < 0)
                    {
                        return false;
                    }

                    break;
            }
        }

        return true;
    }

    /// <summary>
    /// Разбивает версию на числовые и буквенные части. Разделителями считаются
    /// <c>.</c>, <c>-</c>, <c>+</c>, <c>_</c> и пробел.
    /// </summary>
    private static IReadOnlyList<VersionPart>? ParseVersionParts(string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
        {
            return null;
        }

        var parts = new List<VersionPart>();
        var hasDigits = false;
        var index = 0;

        while (index < text.Length)
        {
            var current = text[index];

            if (IsVersionSeparator(current))
            {
                index++;
                continue;
            }

            if (char.IsDigit(current))
            {
                var start = index;
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }

                hasDigits = true;
                var numberText = text[start..index];

                parts.Add(long.TryParse(
                        numberText,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var number)
                    ? VersionPart.FromNumber(number)
                    : VersionPart.FromText(numberText));

                continue;
            }

            var textStart = index;
            while (index < text.Length &&
                   !char.IsDigit(text[index]) &&
                   !IsVersionSeparator(text[index]))
            {
                index++;
            }

            parts.Add(VersionPart.FromText(text[textStart..index]));
        }

        return hasDigits ? parts : null;
    }

    private static bool IsVersionSeparator(char value) =>
        value is '.' or '-' or '+' or '_' or ' ';

    private static int CompareVersionParts(
        IReadOnlyList<VersionPart> left,
        IReadOnlyList<VersionPart> right)
    {
        var length = Math.Max(left.Count, right.Count);

        for (var index = 0; index < length; index++)
        {
            var hasLeft = index < left.Count;
            var hasRight = index < right.Count;

            if (!hasLeft || !hasRight)
            {
                var extra = hasLeft ? left[index] : right[index];

                // Отсутствующая числовая часть равна нулю,
                // буквенный суффикс считается пре-релизом и меньше релиза.
                if (extra.IsNumber)
                {
                    if (extra.Number == 0)
                    {
                        continue;
                    }

                    return hasLeft ? 1 : -1;
                }

                return hasLeft ? -1 : 1;
            }

            var a = left[index];
            var b = right[index];

            if (a.IsNumber && b.IsNumber)
            {
                if (a.Number != b.Number)
                {
                    return a.Number.CompareTo(b.Number);
                }

                continue;
            }

            if (a.IsNumber)
            {
                return 1;
            }

            if (b.IsNumber)
            {
                return -1;
            }

            var comparison = StringComparer.OrdinalIgnoreCase.Compare(a.Text, b.Text);
            if (comparison != 0)
            {
                return comparison < 0 ? -1 : 1;
            }
        }

        return 0;
    }

    private readonly struct VersionPart
    {
        private VersionPart(bool isNumber, long number, string text)
        {
            IsNumber = isNumber;
            Number = number;
            Text = text;
        }

        public bool IsNumber { get; }

        public long Number { get; }

        public string Text { get; }

        public static VersionPart FromNumber(long number) => new(true, number, "");

        public static VersionPart FromText(string text) =>
            new(false, 0, text.ToLowerInvariant());
    }
}

/// <summary>Результат анализа папки mods.</summary>
public sealed class ModAnalysisReport
{
    public List<ModMetadata> Mods { get; } = new();

    /// <summary>Одинаковый modId в разных файлах.</summary>
    public List<DuplicateModId> DuplicateModIds { get; } = new();

    /// <summary>Одинаковые имена файлов без учёта регистра.</summary>
    public List<string> DuplicateFileNames { get; } = new();

    /// <summary>Обязательные зависимости, которых нет среди включённых модов (или версия не подходит).</summary>
    public List<ModDependencyIssue> MissingDependencies { get; } = new();

    /// <summary>Зависимости, которые есть в папке, но отключены.</summary>
    public List<ModDependencyIssue> DisabledDependencies { get; } = new();

    public List<string> Warnings { get; } = new();

    public bool IsHealthy =>
        DuplicateModIds.Count == 0 &&
        DuplicateFileNames.Count == 0 &&
        MissingDependencies.Count == 0;
}

/// <summary>Мод, у которого несколько файлов с одним modId.</summary>
public sealed class DuplicateModId
{
    public string ModId { get; set; } = "";

    public List<string> FileNames { get; set; } = new();
}

/// <summary>Проблема зависимости: отсутствует, отключена или не подходит версия.</summary>
public sealed class ModDependencyIssue
{
    public string ModId { get; set; } = "";

    public string VersionRange { get; set; } = "";

    /// <summary>ModId мода, который требует зависимость.</summary>
    public string RequiredBy { get; set; } = "";

    /// <summary>Файл мода, который требует зависимость.</summary>
    public string RequiredByFileName { get; set; } = "";

    /// <summary>Найденная версия, если файл зависимости существует.</summary>
    public string? FoundVersion { get; set; }

    /// <summary>Файл найденной зависимости.</summary>
    public string? FoundFileName { get; set; }
}
