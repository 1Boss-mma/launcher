namespace SamaraLauncher.Tests;

/// <summary>Временная папка теста, удаляемая при Dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Root = Path.Combine(
            Path.GetTempPath(),
            "samara-launcher-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // Временная папка останется до очистки системы.
        }
        catch (UnauthorizedAccessException)
        {
            // Не заслоняем результат теста.
        }
    }
}
