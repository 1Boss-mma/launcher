namespace SamaraLauncher.Core.Models;

/// <summary>
/// latest.json на сервере обновлений. Пример:
/// { "version": "0.2.1", "url": "https://host/launcher/Launcher-0.2.1.exe",
///   "sha256": "...", "notes": "...", "mandatory": false }
/// </summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";

    public string Url { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public string Notes { get; set; } = "";

    public bool Mandatory { get; set; }
}
