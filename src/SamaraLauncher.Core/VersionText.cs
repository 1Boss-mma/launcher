using SamaraLauncher.Core.Channels;

namespace SamaraLauncher.Core;

/// <summary>
/// Сравнение «человеческих» версий, которые не обязаны быть строгим SemVer:
/// 47.4.28 &gt; 47.4.9, 1.20.1-forge-47.4.28, 1.0.0-beta.2 &lt; 1.0.0-beta.10.
/// </summary>
internal static class VersionText
{
    public static int Compare(string? left, string? right)
    {
        if (SemVer.TryParse(left, out var leftSemVer) &&
            SemVer.TryParse(right, out var rightSemVer))
        {
            return leftSemVer.CompareTo(rightSemVer);
        }

        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        var count = Math.Min(leftTokens.Count, rightTokens.Count);

        for (var index = 0; index < count; index++)
        {
            var leftToken = leftTokens[index];
            var rightToken = rightTokens[index];

            if (leftToken.IsNumber && rightToken.IsNumber)
            {
                var leftNumber = TrimLeadingZeros(leftToken.Value);
                var rightNumber = TrimLeadingZeros(rightToken.Value);
                var byLength = leftNumber.Length.CompareTo(rightNumber.Length);
                if (byLength != 0)
                {
                    return byLength;
                }

                var byDigits = string.CompareOrdinal(leftNumber, rightNumber);
                if (byDigits != 0)
                {
                    return byDigits;
                }
            }
            else if (leftToken.IsNumber)
            {
                return 1;
            }
            else if (rightToken.IsNumber)
            {
                return -1;
            }
            else
            {
                var byText = string.Compare(
                    leftToken.Value,
                    rightToken.Value,
                    StringComparison.OrdinalIgnoreCase);

                if (byText != 0)
                {
                    return byText;
                }
            }
        }

        if (leftTokens.Count == rightTokens.Count)
        {
            return 0;
        }

        // Более короткая версия без текстового суффикса считается релизом:
        // 1.0.0 > 1.0.0-beta, но 1.0.0.1 > 1.0.0.
        var longer = leftTokens.Count > rightTokens.Count ? leftTokens : rightTokens;
        var next = longer[count];
        var leftIsLonger = leftTokens.Count > rightTokens.Count;

        if (next.IsNumber)
        {
            return leftIsLonger ? 1 : -1;
        }

        return leftIsLonger ? -1 : 1;
    }

    public static bool IsNewer(string? candidate, string? current) => Compare(candidate, current) > 0;

    private static string TrimLeadingZeros(string value)
    {
        var index = 0;
        while (index < value.Length - 1 && value[index] == '0')
        {
            index++;
        }

        return index == 0 ? value : value[index..];
    }

    private static List<Token> Tokenize(string? value)
    {
        var result = new List<Token>();
        var text = (value ?? "").Trim();

        if (text.Length > 1 && (text[0] == 'v' || text[0] == 'V') && char.IsDigit(text[1]))
        {
            text = text[1..];
        }

        var hasDigits = false;

        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                if (result.Count == 0 || !result[^1].IsNumber)
                {
                    result.Add(new Token(true, ""));
                }

                var last = result[^1];
                result[^1] = last with { Value = last.Value + ch };
                hasDigits = true;
            }
            else if (char.IsLetter(ch) || ch == '_')
            {
                if (result.Count == 0 || result[^1].IsNumber)
                {
                    result.Add(new Token(false, ""));
                }

                var last = result[^1];
                result[^1] = last with { Value = last.Value + ch };
            }
        }

        return hasDigits || result.Count > 0 ? result : new List<Token> { new(false, text) };
    }

    private readonly record struct Token(bool IsNumber, string Value);
}
