namespace MeetingRecorder;

/// <summary>
/// Everything the app needs before it can work, run on every start (App deployment standard v2.2, section 4.2).
/// The installer deliberately does none of this, so fresh installs, reinstalls and sign-ins share one path.
/// </summary>
static class AppInitializer
{
    public const int CurrentSchemaVersion = 2;

    /// <summary>
    /// Creates the data folder and, when it is new, copies the data from the locations used before standard v2.0.
    /// Idempotent. Returns the problems the user needs to know about; the app starts regardless.
    /// </summary>
    /// <remarks>Runs before the settings are loaded, so the copied settings are the ones used.</remarks>
    public static List<string> PrepareDataFolder()
    {
        var problems = new List<string>();
        bool isNew = false;
        TryRun(problems, "Could not create the data folder", () =>
        {
            isNew = !Directory.Exists(AppPaths.DataDir) || !Directory.EnumerateFileSystemEntries(AppPaths.DataDir).Any();
            Directory.CreateDirectory(AppPaths.DataDir);
        });
        if (!isNew)
            return problems;
        TryRun(problems, "Could not copy the settings from the previous install", () => CopyLegacyFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetingRecorder", "settings.json"),
            AppPaths.SettingsPath));
        TryRun(problems, "Could not copy the warnings log from the previous install", () => CopyLegacyFile(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder", "warnings.log"),
            AppPaths.WarningLogPath));
        return problems;
    }

    /// <summary>Creates default settings, migrates older ones and creates the recordings folder. Idempotent.</summary>
    public static List<string> Run(AppSettings settings, string? settingsLoadError)
    {
        var problems = new List<string>();
        if (settingsLoadError != null)
            problems.Add(settingsLoadError);

        if (settingsLoadError == null && (!File.Exists(AppPaths.SettingsPath) || settings.SchemaVersion < CurrentSchemaVersion))
        {
            // Version 0 -> 1 only introduced SchemaVersion itself.
            // Version 1 -> 2: AutoRecordTeams became AutoRecord["teams"], one switch per meeting app.
            if (settings.AutoRecordTeams is { } teams)
                settings.AutoRecord.TryAdd("teams", teams);
            settings.AutoRecordTeams = null;
            settings.SchemaVersion = CurrentSchemaVersion;
            TryRun(problems, "Could not write the default settings", settings.Save);
        }

        TryRun(problems, "Could not create the recordings folder", () => Directory.CreateDirectory(settings.RecordingsRoot));
        return problems;
    }

    /// <summary>Copies a file into the new data folder; the old file is left in place.</summary>
    private static void CopyLegacyFile(string legacyPath, string path)
    {
        if (File.Exists(legacyPath))
            File.Copy(legacyPath, path);
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
