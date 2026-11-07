using SamaraLauncher.Core.Java;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static async Task<int> JavaAsync(LauncherConfig config, ParsedArgs args)
    {
        var subcommand = GetSubcommand(args);
        if (subcommand is null)
        {
            PrintJavaHelp();
            return 2;
        }

        var rest = GetSubcommandArguments(args);
        var java = new JavaManager();

        switch (subcommand)
        {
            case "list":
            {
                var list = java.Discover();

                if (WantsJson(args))
                {
                    PrintJson(list.Select(info => new
                    {
                        info.Path,
                        info.Version,
                        info.MajorVersion,
                        info.IsSupported
                    }).ToList());
                    return 0;
                }

                if (list.Count == 0)
                {
                    Console.WriteLine("Java не найдена. Установите: java install 17");
                    return 0;
                }

                Console.WriteLine($"Найдено Java: {list.Count}");

                foreach (var info in list)
                {
                    var mark = info.IsSupported ? "OK" : "!!";
                    Console.WriteLine(
                        $"[{mark}] Java {info.Version} (major {info.MajorVersion}) — {info.Path}");
                }

                return 0;
            }

            case "find":
            {
                var instance = ResolveInstanceOption(config, args, out var exitCode);
                if (instance is null)
                {
                    return exitCode;
                }

                var info = java.FindFor(instance, 17);

                if (WantsJson(args))
                {
                    if (info is null)
                    {
                        PrintJson(new { found = false, requiredMajor = 17 });
                        return 1;
                    }

                    PrintJson(new
                    {
                        found = true,
                        requiredMajor = 17,
                        info.Path,
                        info.Version,
                        info.MajorVersion
                    });
                    return 0;
                }

                if (info is null)
                {
                    Console.Error.WriteLine(
                        $"Java 17+ для инстанса \"{instance.Id}\" не найдена. " +
                        $"Установите: java install 17 --instance {instance.Id}");
                    return 1;
                }

                Console.WriteLine($"Java для \"{instance.Id}\": {info.Version} (major {info.MajorVersion})");
                Console.WriteLine($"Путь: {info.Path}");
                return 0;
            }

            case "install":
            {
                var majorValue = rest.FirstOrDefault();
                if (!int.TryParse(majorValue, out var major) || major is not (8 or 17 or 21))
                {
                    Console.Error.WriteLine("Укажите версию Java: java install <8|17|21> [--instance <id>]");
                    return 2;
                }

                var instance = ResolveInstanceOption(config, args, out var exitCode);
                if (instance is null)
                {
                    return exitCode;
                }

                using var cancellation = new CliCancellation();
                var json = WantsJson(args);
                var info = await java.EnsureAsync(
                    instance,
                    major,
                    json ? null : new ConsoleProgress(),
                    cancellation.Token);

                if (json)
                {
                    PrintJson(new
                    {
                        instanceId = instance.Id,
                        requestedMajor = major,
                        info.Path,
                        info.Version,
                        info.MajorVersion
                    });
                    return 0;
                }

                Console.WriteLine();
                Console.WriteLine($"Java {major} готова для \"{instance.Id}\": {info.Version}");
                Console.WriteLine($"Путь: {info.Path}");
                return 0;
            }

            default:
                Console.Error.WriteLine($"Неизвестная подкоманда java: {subcommand}");
                PrintJavaHelp();
                return 2;
        }
    }

    private static void PrintJavaHelp()
    {
        Console.WriteLine("""
            Использование:
              java list [--json]
              java find [--instance <id>] [--json]
              java install <8|17|21> [--instance <id>] [--json]

            java list     найти Java в runtime инстансов, JAVA_HOME, PATH и Eclipse Adoptium.
            java find     подобрать Java 17+ для инстанса.
            java install  скачать Eclipse Temurin JRE нужной версии в runtime инстанса.
            """);
    }
}
