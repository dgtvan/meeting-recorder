namespace MeetingRecorder;

/// <summary>
/// Everything the app needs before it can work, run on every start (App deployment standard v1.2, section 7.1).
/// The installer deliberately does none of this, so fresh installs, self-updates and sign-ins share one path.
/// </summary>
static class AppInitializer
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>
    /// Creates missing folders and default settings, and migrates older settings. Idempotent.
    /// Returns the problems the user needs to know about; the app starts regardless.
    /// </summary>
    public static List<string> Run(AppSettings settings, string? settingsLoadError)
    {
        var problems = new List<string>();
        if (settingsLoadError != null)
            problems.Add(settingsLoadError);

        if (settingsLoadError == null && (!File.Exists(AppSettings.SettingsPath) || settings.SchemaVersion < CurrentSchemaVersion))
        {
            // Version 0 -> 1 only introduced SchemaVersion itself; later layout changes add their steps here.
            settings.SchemaVersion = CurrentSchemaVersion;
            TryRun(problems, "Could not write the default settings", settings.Save);
        }

        TryRun(problems, "Could not create the recordings folder", () => Directory.CreateDirectory(settings.RecordingsRoot));
        TryRun(problems, "Could not create the app data folder", () => Directory.CreateDirectory(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder")));
        return problems;
    }

    private static void TryRun(List<string> problems, string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            problems.Add($"{what}: {ex.Message}");
        }
    }
}
