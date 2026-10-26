namespace MeetingRecorder;

/// <summary>
/// Where the app keeps its data: the folder given by <c>--data-dir</c> (App deployment standard v2.2, section 4.1).
/// There is no default; every mode that reads or writes data must set it first.
/// </summary>
static class AppPaths
{
    public const string DataDirFlag = "--data-dir";

    private static string? s_dataDir;

    public static string DataDir => s_dataDir ?? throw new InvalidOperationException("The data folder was not set.");

    public static string SettingsPath => Path.Combine(DataDir, "settings.json");

    public static string WarningLogPath => Path.Combine(DataDir, "warnings.log");

    public static string MeetingLogPath => Path.Combine(DataDir, "meetings.log");

    public static string AudioLogPath => Path.Combine(DataDir, "audio.log");

    /// <summary>Sets <see cref="DataDir"/> from the command line. Returns false when the argument is missing or empty.</summary>
    public static bool TryInitialize(string[] args)
    {
        int i = Array.IndexOf(args, DataDirFlag);
        if (i < 0 || i + 1 >= args.Length || string.IsNullOrWhiteSpace(args[i + 1]))
            return false;
        s_dataDir = Path.GetFullPath(args[i + 1]);
        return true;
    }

    /// <summary>The <c>--data-dir</c> argument for a child process, quoted.</summary>
    public static string Argument => $"{DataDirFlag} \"{DataDir.TrimEnd('\\')}\"";
}
