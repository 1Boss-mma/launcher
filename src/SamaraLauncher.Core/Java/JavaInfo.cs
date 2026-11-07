namespace SamaraLauncher.Core.Java;

public sealed record JavaInfo(string Path, string Version, int MajorVersion)
{
    public bool IsSupported => MajorVersion >= 17;

    /// <summary>
    /// Подходит ли Java для требуемой major-версии. allowNewer=false требует
    /// точного совпадения (нужно для старых версий Minecraft на Java 8).
    /// </summary>
    public bool IsCompatible(int requiredMajor, bool allowNewer = true) =>
        allowNewer ? MajorVersion >= requiredMajor : MajorVersion == requiredMajor;
}
