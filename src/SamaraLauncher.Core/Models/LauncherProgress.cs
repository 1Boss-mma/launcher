namespace SamaraLauncher.Core.Models;

public enum ProgressStage
{
    Preparing,
    Java,
    Minecraft,
    Forge,
    Pack,
    Backup,
    Verify,
    Launch,
    Update,
    Done,
    Error
}

/// <summary>Единый прогресс для CLI и WPF.</summary>
public sealed record LauncherProgress(
    ProgressStage Stage,
    string Message,
    long Completed = 0,
    long Total = 0)
{
    public double? Percent => Total > 0 ? Math.Clamp((double)Completed / Total * 100, 0, 100) : null;
}
