namespace SamaraLauncher.Core.Launch;

/// <summary>
/// Проверка rules из version json. Поддерживаются только правила ОС;
/// правила с features (demo, custom resolution, quick play) не применяются.
/// </summary>
internal static class RuleEvaluator
{
    public static bool IsAllowed(IReadOnlyList<Rule>? rules, string osName = "windows")
    {
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;

        foreach (var rule in rules)
        {
            if (rule.Features is { Count: > 0 })
            {
                continue;
            }

            var ruleOs = rule.Os?.Name;
            var matchesOs = string.IsNullOrWhiteSpace(ruleOs)
                            || string.Equals(ruleOs, osName, StringComparison.OrdinalIgnoreCase);

            if (!matchesOs)
            {
                continue;
            }

            if (string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
            }
            else if (string.Equals(rule.Action, "disallow", StringComparison.OrdinalIgnoreCase))
            {
                allowed = false;
            }
        }

        return allowed;
    }
}
