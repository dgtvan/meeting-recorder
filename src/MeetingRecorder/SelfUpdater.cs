using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MeetingRecorder;

/// <summary>
/// Finds and installs new builds from the publish folder, per the App deployment standard v1.2 (sections 4 and 7.3–7.5).
/// </summary>
/// <remarks>
/// Builds live in <c>D:\Apps\MeetingRecorder\builds\&lt;build id&gt;\</c>, written by another Windows account that
/// can't touch this user's profile. The app only reads them; it installs one by starting <c>apply-update.cmd</c>
/// from %TEMP% and exiting. Its update state lives in %LOCALAPPDATA%\MeetingRecorder\update-state.txt and is
/// mirrored to <c>pickup\%USERNAME%\status.txt</c> so the publisher can see what happened.
/// </remarks>
sealed partial class SelfUpdater : IDisposable
{
    private const string BuildIdFile = "build-id.txt";
    private const string SourceFile = "update-source.txt";
    private const string ScriptFile = "apply-update.cmd";

    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MeetingRecorder", "update-state.txt");

    private readonly string? _publishRoot;
    private readonly FileSystemWatcher? _watcher;
    private readonly System.Threading.Timer? _debounce;

    /// <summary>Raised on a thread-pool thread, debounced, when something changes under <c>builds\</c>.</summary>
    public event Action? BuildsChanged;

    public SelfUpdater()
    {
        InstalledBuildId = ReadBuildId(AppContext.BaseDirectory);
        string sourceFile = Path.Combine(AppContext.BaseDirectory, SourceFile);
        if (!File.Exists(sourceFile))
            return;
        _publishRoot = File.ReadAllText(sourceFile).Trim();
        if (!Directory.Exists(_publishRoot))
            return;

        // A publish writes many files; react once it has settled.
        _debounce = new System.Threading.Timer(_ => BuildsChanged?.Invoke());
        _watcher = new FileSystemWatcher(_publishRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
        };
        FileSystemEventHandler onChange = (_, e) =>
        {
            if (e.FullPath.StartsWith(BuildsFolder, StringComparison.OrdinalIgnoreCase))
                _debounce.Change(2000, Timeout.Infinite);
        };
        _watcher.Created += onChange;
        _watcher.Changed += onChange;
        _watcher.Deleted += onChange;
        _watcher.Renamed += (_, e) => onChange(this, e);
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>False when the app wasn't installed from a publish folder (e.g. run from a dev build).</summary>
    public bool IsEnabled => _watcher != null;

    public string? InstalledBuildId { get; }

    private string BuildsFolder => Path.Combine(_publishRoot!, "builds") + Path.DirectorySeparatorChar;

    /// <summary>The highest build id whose folder is complete (its build-id.txt matches the folder name).</summary>
    public string? FindLatestBuild()
    {
        if (!IsEnabled || !Directory.Exists(BuildsFolder))
            return null;
        try
        {
            return new DirectoryInfo(BuildsFolder).GetDirectories()
                .Select(d => d.Name)
                .Where(name => BuildIdPattern().IsMatch(name) && ReadBuildId(Path.Combine(BuildsFolder, name)) == name)
                .OrderByDescending(name => name, StringComparer.Ordinal)
                .FirstOrDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The build id of the last install attempt, to avoid retrying a build that failed.</summary>
    public string? LastAttemptId
    {
        get
        {
            try
            {
                string[] lines = File.Exists(StatePath) ? File.ReadAllLines(StatePath) : [];
                return lines.Length >= 3 && lines[2].Length > 0 ? lines[2] : null;
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>Records the update state in the profile and mirrors it to this account's pickup folder.</summary>
    public void WriteState(string state, string? lastAttemptId = null)
    {
        string content = $"{state}\n{InstalledBuildId}\n{lastAttemptId ?? LastAttemptId}\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        if (_publishRoot == null)
            return;
        try
        {
            string pickup = Path.Combine(_publishRoot, "pickup", Environment.UserName);
            Directory.CreateDirectory(pickup);
            File.WriteAllText(Path.Combine(pickup, "status.txt"), content);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Starts the update script for <paramref name="buildId"/>; the caller must exit right after.</summary>
    public void LaunchUpdate(string buildId)
    {
        WriteState("applying", buildId);
        string buildFolder = Path.Combine(BuildsFolder, buildId);
        // Run a copy from %TEMP%: a batch file must not be deleted while it runs, and retention may delete the build.
        string script = Path.Combine(Path.GetTempPath(), "MeetingRecorder-" + ScriptFile);
        File.Copy(Path.Combine(buildFolder, ScriptFile), script, overwrite: true);
        string installFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c \"\"{script}\" {Environment.ProcessId} \"{buildFolder}\" \"{installFolder}\"\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }

    private static string? ReadBuildId(string folder)
    {
        try
        {
            string path = Path.Combine(folder, BuildIdFile);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\d{8}T\d{6}Z-[0-9a-f]{8}$")]
    private static partial Regex BuildIdPattern();
}
