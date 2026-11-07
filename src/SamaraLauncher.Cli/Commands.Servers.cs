using SamaraLauncher.Core.Models;
using SamaraLauncher.Core.Servers;

namespace SamaraLauncher.Cli;

internal static partial class Program
{
    private static async Task<int> ServersAsync(LauncherConfig config, ParsedArgs args)
    {
        var instance = ResolveInstanceOption(config, args, out var exitCode);
        if (instance is null)
        {
            return exitCode;
        }

        var subcommand = GetSubcommand(args);
        if (subcommand is null)
        {
            PrintServersHelp();
            return 2;
        }

        var rest = GetSubcommandArguments(args);
        var servers = new ServerListService();

        switch (subcommand)
        {
            case "list":
            {
                var list = servers.Load(instance);

                if (WantsJson(args))
                {
                    PrintJson(list.Select(server => new
                    {
                        server.Name,
                        server.Host,
                        server.Port,
                        address = server.Address,
                        server.IsFavorite,
                        server.Notes
                    }).ToList());
                    return 0;
                }

                Console.WriteLine($"servers.dat: {servers.GetServersDatPath(instance)}");

                if (list.Count == 0)
                {
                    Console.WriteLine("Серверов нет.");
                    return 0;
                }

                for (var index = 0; index < list.Count; index++)
                {
                    var server = list[index];
                    var favorite = server.IsFavorite ? "*" : " ";
                    Console.WriteLine(
                        $"{index + 1,2}. [{favorite}] {server.Name,-28} {server.Address}");
                }

                Console.WriteLine();
                Console.WriteLine($"Серверов: {list.Count}.");
                return 0;
            }

            case "ping":
            {
                var address = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(address))
                {
                    Console.Error.WriteLine("Укажите адрес: servers ping <host[:port]>");
                    return 2;
                }

                using var cancellation = new CliCancellation(announce: false);
                var result = await new ServerPinger().PingAsync(
                    address,
                    ServerEntry.DefaultPort,
                    null,
                    cancellation.Token);

                if (WantsJson(args))
                {
                    PrintJson(new
                    {
                        address,
                        result.Online,
                        result.LatencyMs,
                        result.VersionName,
                        result.Protocol,
                        result.PlayersOnline,
                        result.PlayersMax,
                        result.Motd,
                        players = result.Players.Select(player => player.Name).ToList(),
                        result.Error
                    });
                    return result.Online ? 0 : 1;
                }

                if (!result.Online)
                {
                    Console.Error.WriteLine(result.Error ?? $"Сервер {address} недоступен.");
                    return 1;
                }

                Console.WriteLine($"Сервер: {address}");
                Console.WriteLine($"Статус: онлайн, {result.LatencyMs} мс");
                Console.WriteLine($"Версия: {result.VersionName} (протокол {result.Protocol})");
                Console.WriteLine($"Игроки: {result.PlayersOnline}/{result.PlayersMax}");

                if (!string.IsNullOrWhiteSpace(result.Motd))
                {
                    Console.WriteLine($"MOTD: {result.Motd}");
                }

                if (result.Players.Count > 0)
                {
                    Console.WriteLine(
                        "В списке: " + string.Join(", ", result.Players.Select(player => player.Name)));
                }

                return 0;
            }

            case "add":
            {
                var name = rest.ElementAtOrDefault(0);
                var address = rest.ElementAtOrDefault(1);

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(address))
                {
                    Console.Error.WriteLine("Укажите имя и адрес: servers add <name> <host[:port]>");
                    return 2;
                }

                servers.AddOrUpdate(instance, new ServerEntry
                {
                    Name = name,
                    Host = address
                });

                if (WantsJson(args))
                {
                    PrintJson(new { name, address });
                    return 0;
                }

                Console.WriteLine($"Сервер добавлен: {name} — {address}");
                return 0;
            }

            case "remove":
            {
                var address = rest.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(address))
                {
                    Console.Error.WriteLine("Укажите адрес: servers remove <host[:port]>");
                    return 2;
                }

                if (!servers.Remove(instance, address))
                {
                    Console.Error.WriteLine($"Сервер не найден: {address}");
                    return 1;
                }

                if (WantsJson(args))
                {
                    PrintJson(new { address, removed = true });
                    return 0;
                }

                Console.WriteLine($"Сервер удалён: {address}");
                return 0;
            }

            case "move":
            {
                var fromValue = rest.ElementAtOrDefault(0);
                var toValue = rest.ElementAtOrDefault(1);

                if (!int.TryParse(fromValue, out var from) ||
                    !int.TryParse(toValue, out var to) ||
                    from < 1 ||
                    to < 1)
                {
                    Console.Error.WriteLine(
                        "Укажите позиции (нумерация с 1): servers move <from> <to>");
                    return 2;
                }

                var list = servers.Load(instance);

                if (from > list.Count || to > list.Count)
                {
                    Console.Error.WriteLine($"В списке серверов: {list.Count}. Позиции должны быть от 1 до {list.Count}.");
                    return 1;
                }

                servers.Move(instance, from - 1, to - 1);

                if (WantsJson(args))
                {
                    PrintJson(new { from, to, moved = true });
                    return 0;
                }

                Console.WriteLine($"Сервер перемещён: {from} -> {to}.");
                return 0;
            }

            default:
                Console.Error.WriteLine($"Неизвестная подкоманда servers: {subcommand}");
                PrintServersHelp();
                return 2;
        }
    }

    private static void PrintServersHelp()
    {
        Console.WriteLine("""
            Использование:
              servers list [--instance <id>] [--json]
              servers ping <host[:port]> [--instance <id>] [--json]
              servers add <name> <host[:port]> [--instance <id>] [--json]
              servers remove <host[:port]> [--instance <id>] [--json]
              servers move <from> <to> [--instance <id>] [--json]

            Серверы хранятся в servers.dat инстанса; позиции для move — с 1.
            """);
    }
}
