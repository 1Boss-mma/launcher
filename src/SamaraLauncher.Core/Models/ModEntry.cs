namespace SamaraLauncher.Core.Models;

public sealed class ModEntry
{
    public string FileName { get; set; } = "";

    public string FullPath { get; set; } = "";

    public long Size { get; set; }

    public bool Enabled { get; set; }

    public DateTimeOffset ModifiedAt { get; set; }
}
