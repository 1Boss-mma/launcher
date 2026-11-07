using System.IO.Compression;
using System.Text;
using SamaraLauncher.Core;
using SamaraLauncher.Core.Worlds;
using Xunit;

namespace SamaraLauncher.Tests;

public sealed class NbtReaderTests
{
    [Fact]
    public void FromBytes_ParsesUncompressedRootCompound()
    {
        var level = NbtReader.FromBytes(CreateLevelDat());

        Assert.Equal("Test World", level.GetString("LevelName"));
        Assert.Equal(3955, level.GetInt("DataVersion"));
        Assert.False(level.GetBool("raining"));
    }

    [Fact]
    public void FromBytes_ParsesGzipCompressedData()
    {
        var compressed = Compress(CreateLevelDat(), useGzip: true);

        var level = NbtReader.FromBytes(compressed);

        Assert.Equal("Test World", level.GetString("LevelName"));
    }

    [Fact]
    public void FromBytes_ParsesZlibCompressedData()
    {
        var compressed = Compress(CreateLevelDat(), useGzip: false);

        var level = NbtReader.FromBytes(compressed);

        Assert.Equal("Test World", level.GetString("LevelName"));
    }

    [Fact]
    public void FromBytes_ThrowsForEmptyData()
    {
        Assert.Throws<LauncherException>(() => NbtReader.FromBytes(Array.Empty<byte>()));
    }

    [Fact]
    public void FromBytes_ThrowsWhenRootTagIsNotCompound()
    {
        var data = new byte[] { (byte)NbtTagType.String, 0, 0 };

        Assert.Throws<LauncherException>(() => NbtReader.FromBytes(data));
    }

    [Fact]
    public void FromBytes_ThrowsForTruncatedData()
    {
        var data = new byte[] { (byte)NbtTagType.Compound, 0, 0, (byte)NbtTagType.Int, 0 };

        Assert.Throws<LauncherException>(() => NbtReader.FromBytes(data));
    }

    [Fact]
    public void NbtCompound_Getters_ConvertValuesAndUseFallbacks()
    {
        var compound = new NbtCompound
        {
            ["name"] = "world",
            ["count"] = 42,
            ["seed"] = "123456789012",
            ["flag"] = (sbyte)1,
            ["ratio"] = 2.5d
        };

        Assert.Equal("world", compound.GetString("name"));
        Assert.Equal("42", compound.GetString("count"));
        Assert.Equal(42, compound.GetInt("count"));
        Assert.Equal(123456789012L, compound.GetLong("seed"));
        Assert.True(compound.GetBool("flag"));
        Assert.Equal(2.5f, compound.GetFloat("ratio"));

        Assert.Equal("fallback", compound.GetString("missing", "fallback"));
        Assert.Equal(7, compound.GetInt("missing", 7));
        Assert.False(compound.GetBool("missing"));
        Assert.Equal(1.5f, compound.GetFloat("missing", 1.5f));
        Assert.Null(compound.GetCompound("name"));
    }

    private static byte[] CreateLevelDat()
    {
        using var stream = new MemoryStream();

        WriteByte(stream, (byte)NbtTagType.Compound);
        WriteInt16(stream, 0);

        WriteStringTag(stream, "LevelName", "Test World");
        WriteIntTag(stream, "DataVersion", 3955);
        WriteByteTag(stream, "raining", 0);

        WriteByte(stream, (byte)NbtTagType.End);
        return stream.ToArray();
    }

    private static byte[] Compress(byte[] data, bool useGzip)
    {
        using var output = new MemoryStream();

        using (Stream compressor = useGzip
                   ? new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true)
                   : new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static void WriteStringTag(Stream stream, string name, string value)
    {
        WriteByte(stream, (byte)NbtTagType.String);
        WriteName(stream, name);
        WriteInt16(stream, (short)Encoding.UTF8.GetByteCount(value));
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteIntTag(Stream stream, string name, int value)
    {
        WriteByte(stream, (byte)NbtTagType.Int);
        WriteName(stream, name);
        WriteByte(stream, (byte)(value >> 24));
        WriteByte(stream, (byte)(value >> 16));
        WriteByte(stream, (byte)(value >> 8));
        WriteByte(stream, (byte)value);
    }

    private static void WriteByteTag(Stream stream, string name, sbyte value)
    {
        WriteByte(stream, (byte)NbtTagType.Byte);
        WriteName(stream, name);
        WriteByte(stream, unchecked((byte)value));
    }

    private static void WriteName(Stream stream, string name)
    {
        WriteInt16(stream, (short)Encoding.UTF8.GetByteCount(name));
        var bytes = Encoding.UTF8.GetBytes(name);
        stream.Write(bytes, 0, bytes.Length);
    }

    private static void WriteInt16(Stream stream, short value)
    {
        WriteByte(stream, (byte)(value >> 8));
        WriteByte(stream, (byte)value);
    }

    private static void WriteByte(Stream stream, byte value) => stream.WriteByte(value);
}
