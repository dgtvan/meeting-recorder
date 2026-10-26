namespace MeetingRecorder;

/// <summary>
/// Every warning the tray shows, appended to warnings.log in the data folder, so a warning isn't lost
/// when the app restarts (each start begins with no warnings; earlier ones belong to the previous session).
/// </summary>
static class WarningLog
{
    public static string LogPath => AppPaths.WarningLogPath;

    /// <summary>Appends one entry; logging problems are ignored so they never hide the warning itself.</summary>
    public static void Append(string text) => LogFile.Append(LogPath, text);
}

/// <summary>A timestamped text log in the data folder, rolled over to <c>&lt;name&gt;.old.log</c> at 1 MB.</summary>
static class LogFile
{
    private const long MaxBytes = 1 << 20;

    /// <summary>Appends one entry, its line breaks shown as " | " unless <paramref name="multiline"/>; logging problems are ignored.</summary>
    public static void Append(string path, string text, bool multiline = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var file = new FileInfo(path);
            if (file.Exists && file.Length > MaxBytes)
                File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
            string entry = multiline ? text.TrimEnd() : text.ReplaceLineEndings(" | ");
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {entry}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
