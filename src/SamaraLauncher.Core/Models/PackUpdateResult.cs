namespace SamaraLauncher.Core.Models;

public sealed class PackUpdateResult
{
    public bool Success { get; set; }

    public bool Changed { get; set; }

    public string? BackupPath { get; set; }

    public List<string> ChangedFiles { get; set; } = new();

    public List<string> RestoredProtectedFiles { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public string Output { get; set; } = "";
}
