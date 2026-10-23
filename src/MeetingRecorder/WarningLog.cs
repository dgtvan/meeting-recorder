namespace MeetingRecorder;

/// <summary>
/// Every warning the tray shows, appended to %LOCALAPPDATA%\MeetingRecorder\warnings.log, so a warning isn't lost
/// when the app restarts (each start begins with no warnings; earlier ones belong to the previous session).
/// </summary>
static class WarningLog
{
    private const long MaxBytes = 1 << 20;

    public static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder", "warnings.log");

    /// <summary>Appends one entry; logging problems are ignored so they never hide the warning itself.</summary>
    public static void Append(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            var file = new FileInfo(LogPath);
            if (file.Exists && file.Length > MaxBytes)
                File.Move(LogPath, Path.ChangeExtension(LogPath, ".old.log"), overwrite: true);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {text.ReplaceLineEndings(" | ")}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
