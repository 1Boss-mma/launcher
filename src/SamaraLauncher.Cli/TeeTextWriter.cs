using System.Text;

namespace SamaraLauncher.Cli;

/// <summary>
/// Пишет одновременно в консоль и в файл лога.
/// </summary>
internal sealed class TeeTextWriter : TextWriter
{
    private readonly TextWriter[] _writers;
    private readonly object _sync = new();

    public TeeTextWriter(params TextWriter[] writers)
    {
        _writers = writers;
    }

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        lock (_sync)
        {
            foreach (var writer in _writers)
            {
                writer.Write(value);
            }
        }
    }

    public override void Write(string? value)
    {
        lock (_sync)
        {
            foreach (var writer in _writers)
            {
                writer.Write(value);
            }
        }
    }

    public override void WriteLine(string? value)
    {
        lock (_sync)
        {
            foreach (var writer in _writers)
            {
                writer.WriteLine(value);
            }
        }
    }

    public override void Flush()
    {
        lock (_sync)
        {
            foreach (var writer in _writers)
            {
                writer.Flush();
            }
        }
    }
}
