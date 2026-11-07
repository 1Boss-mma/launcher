namespace SamaraLauncher.Core;

/// <summary>
/// Ожидаемая ошибка лаунчера: показывается пользователю без стека.
/// </summary>
public sealed class LauncherException : Exception
{
    public LauncherException(string message) : base(message)
    {
    }

    public LauncherException(string message, Exception inner) : base(message, inner)
    {
    }
}
