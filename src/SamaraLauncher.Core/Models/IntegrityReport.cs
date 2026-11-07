namespace SamaraLauncher.Core.Models;

public enum FileStateKind
{
    Ok,
    Missing,
    Modified,
    Extra,
    Protected
}

/// <summary>Состояние одного файла сборки.</summary>
public sealed class FileState
{
    public string Path { get; set; } = "";

    public FileStateKind Kind { get; set; }

    /// <summary>Ожидаемый хеш: из packwiz или из overrides.json.</summary>
    public string? ExpectedHash { get; set; }

    public string? ActualHash { get; set; }

    public long Size { get; set; }

    public bool HashMatches { get; set; }

    public string? Reason { get; set; }
}

/// <summary>Результат проверки целостности сборки.</summary>
public sealed class IntegrityReport
{
    public DateTimeOffset ScannedAt { get; set; } = DateTimeOffset.UtcNow;

    public int CheckedFiles { get; set; }

    public List<FileState> Missing { get; set; } = new();

    public List<FileState> Modified { get; set; } = new();

    public List<FileState> Extra { get; set; } = new();

    public List<FileState> Protected { get; set; } = new();

    public bool ProtectedIntact => Protected.All(file => file.HashMatches);

    public bool IsHealthy =>
        Missing.Count == 0 &&
        Modified.Count == 0 &&
        Extra.Count == 0 &&
        ProtectedIntact;
}
