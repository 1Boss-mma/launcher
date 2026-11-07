using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SamaraLauncher.Core;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static readonly JsonSerializerOptions CliJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static bool WantsJson(ParsedArgs args) => args.Has("json");

    private static void PrintJson<T>(T value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, CliJsonOptions));

    /// <summary>Подкоманда — второй позиционный аргумент (первый — имя команды).</summary>
    private static string? GetSubcommand(ParsedArgs args) =>
        args.Positional.Skip(1).FirstOrDefault()?.ToLowerInvariant();

    /// <summary>Аргументы после подкоманды: SamaraLauncher.Cli mods enable &lt;файл&gt;.</summary>
    private static List<string> GetSubcommandArguments(ParsedArgs args) =>
        args.Positional.Skip(2).ToList();

    private static string ExpandPath(string raw)
    {
        try
        {
            return Path.GetFullPath(
                Environment.ExpandEnvironmentVariables(raw.Trim().Trim('"')));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new LauncherException($"Некорректный путь: {raw}", ex);
        }
    }

    private static bool IsPathInside(string path, string root)
    {
        var normalizedRoot = root.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);

        if (string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return path.StartsWith(
            normalizedRoot + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
            // Уборка временной папки не должна скрывать исходную ошибку.
        }
        catch (UnauthorizedAccessException)
        {
            // То же.
        }
    }

    /// <summary>Интерактивное подтверждение. Без консольного ввода считаем отказом.</summary>
    private static bool Confirm(string question)
    {
        if (Console.IsInputRedirected)
        {
            Console.Error.WriteLine("Нет интерактивного ввода — подтвердите действие флагом --yes.");
            return false;
        }

        Console.Write(question + " [y/N] ");
        var answer = Console.ReadLine()?.Trim();

        return string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
               || string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase)
               || string.Equals(answer, "д", StringComparison.OrdinalIgnoreCase)
               || string.Equals(answer, "да", StringComparison.OrdinalIgnoreCase);
    }
}
