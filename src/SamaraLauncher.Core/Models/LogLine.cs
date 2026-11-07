namespace SamaraLauncher.Core.Models;

public sealed record LogLine(DateTimeOffset Timestamp, string Text, bool IsError = false);
