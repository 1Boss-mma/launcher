using System.Buffers.Binary;
using System.Text;
using SamaraLauncher.Core.Models;

namespace SamaraLauncher.Tests;

/// <summary>Минимальные level.dat и серверные папки для тестов миров.</summary>
internal static class WorldTestHelpers
{
    public static string CreateWorld(
        string parent,
        string folderName,
        string displayName,
        long seed = 0,
        long lastPlayed = 0)
    {
        var directory = Path.Combine(parent, folderName);
        Directory.CreateDirectory(directory);
        WriteLevelDat(directory, displayName, seed, lastPlayed);
        return directory;
    }

    public static void WriteLevelDat(
        string worldDir,
        string displayName,
        long seed = 0,
        long lastPlayed = 0)
    {
        Directory.CreateDirectory(worldDir);

        using var stream = File.Create(Path.Combine(worldDir, "level.dat"));
        using var writer = new BinaryWriter(stream, new UTF8Encoding(false));

        writer.Write((byte)10); // root compound
        WriteString(writer, "");

        writer.Write((byte)10); // Data
        WriteString(writer, "Data");

        writer.Write((byte)8); // LevelName
        WriteString(writer, "LevelName");
        WriteString(writer, displayName);

        writer.Write((byte)4); // LastPlayed
        WriteString(writer, "LastPlayed");
        WriteLong(writer, lastPlayed);

        writer.Write((byte)4); // RandomSeed
        WriteString(writer, "RandomSeed");
        WriteLong(writer, seed);

        writer.Write((byte)0); // end Data
        writer.Write((byte)0); // end root
    }

    public static HostedServerProfile CreateServer(
        string serverDir,
        string levelName = "world",
        string id = "srv")
    {
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(
            Path.Combine(serverDir, "server.properties"),
            "level-name=" + levelName + "\n");

        return new HostedServerProfile
        {
            Id = id,
            Name = "Test Server",
            ServerDir = serverDir,
            Port = 25565
        };
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, checked((ushort)bytes.Length));
        writer.BaseStream.Write(length);
        writer.BaseStream.Write(bytes);
    }

    private static void WriteLong(BinaryWriter writer, long value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(buffer, value);
        writer.BaseStream.Write(buffer);
    }
}
