using SamaraLauncher.Core.Diagnostics;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static async Task<int> DoctorAsync(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveDoctorInstance(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        var json = WantsJson(args);
        var fix = args.Has("fix");

        using var cancellation = new CliCancellation();
        var progress = json ? null : new ConsoleProgress();
        var service = new DoctorService();

        var report = await service.RunAsync(instance, progress, cancellation.Token);

        if (!json && !fix)
        {
            Console.WriteLine(report.ToText());
            Console.WriteLine();
        }
        else if (!json)
        {
            Console.WriteLine($"Исходно: {report.Summary}");
            Console.WriteLine();
        }

        if (fix)
        {
            var fixedCount = await new DoctorFixService().FixAsync(
                instance,
                report,
                progress,
                cancellation.Token);

            if (json)
            {
                Console.Error.WriteLine($"Исправлено пунктов: {fixedCount}.");
            }
            else
            {
                Console.WriteLine($"Исправлено пунктов: {fixedCount}. Повторная проверка...");
                Console.WriteLine();
            }

            report = await service.RunAsync(instance, progress, cancellation.Token);
        }

        if (json)
        {
            Console.WriteLine(report.ToJson());
        }
        else
        {
            if (fix)
            {
                Console.WriteLine(report.ToText());
                Console.WriteLine();
            }

            Console.WriteLine($"Summary: {report.Summary}");
        }

        return report.Errors > 0 ? 1 : 0;
    }

    /// <summary>
    /// Doctor принимает и --instance &lt;id&gt;, и старый позиционный id:
    /// SamaraLauncher.Cli doctor &lt;id&gt; --fix.
    /// </summary>
    private static InstanceProfile? ResolveDoctorInstance(
        LauncherConfig config,
        ParsedArgs args,
        out int exitCode)
    {
        exitCode = 0;
        var id = args.GetLast("instance") ?? args.Positional.Skip(1).FirstOrDefault();

        if (string.IsNullOrWhiteSpace(id))
        {
            if (config.Instances.Count == 1)
            {
                return config.Instances[0];
            }

            Console.Error.WriteLine(config.Instances.Count == 0
                ? "Инстансов нет. Сначала добавьте: add samara --game-dir <путь>"
                : "Укажите инстанс: --instance <id>");

            exitCode = config.Instances.Count == 0 ? 1 : 2;
            return null;
        }

        var instance = config.Find(id);

        if (instance is null)
        {
            Console.Error.WriteLine($"Инстанс \"{id}\" не найден.");
            exitCode = 1;
        }

        return instance;
    }
}
