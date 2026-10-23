using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace MeetingRecorder;

/// <summary>
/// The whole UI: a tray icon whose colour shows the state (grey idle, red recording, amber saving)
/// and a context menu to start, stop and browse recordings.
/// </summary>
/// <remarks>
/// Anything the user must act on (a failed recording, a source that stopped, low disk space) switches the icon
/// to a warning that stays until the user opens the details, so it isn't missed when notifications are muted.
/// </remarks>
sealed class TrayApp : ApplicationContext
{
    private const int MaxRecentRecordings = 10;
    private const int MaxMenuTextLength = 70;
    private const long LowDiskSpaceBytes = 2L << 30;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromMinutes(2);

    private enum State { Idle, Starting, Recording, Processing }

    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _recordingsItem;
    private readonly ToolStripMenuItem _changeRootItem;
    private readonly ToolStripMenuItem _systemAudioItem;
    private readonly ToolStripMenuItem _microphoneItem;
    private readonly ToolStripMenuItem _problemItem;
    private readonly ToolStripSeparator _problemSeparator = new() { Visible = false };
    private readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _spinner = new() { Interval = 125 };
    private int _spinnerFrame;
    private readonly List<string> _problems = [];
    private readonly SelfUpdater _updater = new();
    private readonly SynchronizationContext _ui;
    private State _state = State.Idle;
    private DateTime _stateSince = DateTime.UtcNow;
    private bool _lowDiskReported;
    private string? _failedBuildReported;
    private RecordingProcess? _session;
    private string? _lastFolder;
    private bool _exitAfterStop;

    public TrayApp()
    {
        _settings = AppSettings.Load(out string? settingsLoadError);
        _menu = new ContextMenuStrip();
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _problemItem = new ToolStripMenuItem("", TrayIcons.Warning.ToBitmap(), (_, _) => ShowProblems())
        {
            Visible = false,
            Font = new Font(_menu.Font, FontStyle.Bold),
        };

        _startItem = new ToolStripMenuItem("Start recording");
        _startItem.DropDownItems.Add(new ToolStripMenuItem("(loading)"));
        _startItem.DropDownOpening += (_, _) => BuildStartMenu();

        _stopItem = new ToolStripMenuItem("Stop recording", null, (_, _) => StopRecording());

        _recordingsItem = new ToolStripMenuItem("Recordings");
        _recordingsItem.DropDownItems.Add(new ToolStripMenuItem("(loading)"));
        _recordingsItem.DropDownOpening += (_, _) => BuildRecordingsMenu();

        _changeRootItem = new ToolStripMenuItem("Change recordings folder...", null, (_, _) => ChangeRecordingsRoot());

        _systemAudioItem = new ToolStripMenuItem("System audio");
        _systemAudioItem.DropDownItems.Add(new ToolStripMenuItem("(loading)"));
        _systemAudioItem.DropDownOpening += (_, _) => BuildDeviceMenu(_systemAudioItem, DataFlow.Render,
            () => _settings.SystemAudioDeviceId, id => _settings.SystemAudioDeviceId = id);

        _microphoneItem = new ToolStripMenuItem("Microphone");
        _microphoneItem.DropDownItems.Add(new ToolStripMenuItem("(loading)"));
        _microphoneItem.DropDownOpening += (_, _) => BuildDeviceMenu(_microphoneItem, DataFlow.Capture,
            () => _settings.MicrophoneDeviceId, id => _settings.MicrophoneDeviceId = id);

        _menu.Items.AddRange(
        [
            _problemItem,
            _problemSeparator,
            _startItem,
            _stopItem,
            new ToolStripSeparator(),
            _recordingsItem,
            new ToolStripMenuItem("Open recordings folder", null, (_, _) => OpenFolder(EnsureRoot())),
            _changeRootItem,
            new ToolStripSeparator(),
            _systemAudioItem,
            _microphoneItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit()),
        ]);

        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            if (_state == State.Recording)
                StopRecording();
            else if (_problems.Count > 0)
                ShowProblems();
            else if (_state == State.Idle)
                OpenFolder(_lastFolder ?? EnsureRoot());
        };
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_problems.Count > 0)
                ShowProblems();
            else if (_lastFolder != null)
                OpenFolder(_lastFolder);
        };

        _watchdog.Tick += (_, _) => CheckHealth();
        _spinner.Tick += (_, _) =>
        {
            _spinnerFrame = (_spinnerFrame + 1) % TrayIcons.Processing.Length;
            RefreshIcon();
        };
        _updater.BuildsChanged += () => _ui.Post(_ => CheckForUpdate(), null);
        Application.ThreadException += (_, e) => AddProblem($"Unexpected error: {e.Exception.Message}");

        SetState(State.Idle);
        foreach (string problem in AppInitializer.Run(_settings, settingsLoadError))
            AddProblem(problem);
        // Also picks up a build published while the app wasn't running, and reports state at startup.
        _ui.Post(_ => CheckForUpdate(), null);
    }

    private void SetState(State state)
    {
        _state = state;
        _stateSince = DateTime.UtcNow;
        _startItem.Enabled = state == State.Idle;
        _stopItem.Enabled = state is State.Recording or State.Starting;
        _systemAudioItem.Enabled = _microphoneItem.Enabled = _changeRootItem.Enabled = state == State.Idle;
        _watchdog.Enabled = state != State.Idle;
        _spinnerFrame = 0;
        _spinner.Enabled = state == State.Processing;
        RefreshIcon();
    }

    private void RefreshIcon()
    {
        bool hasProblem = _problems.Count > 0;
        (Icon icon, string text) = _state switch
        {
            State.Recording => (hasProblem ? TrayIcons.RecordingWarning : TrayIcons.Recording,
                $"Recording since {_session!.StartedAt:HH:mm}: {_session.TargetName}"),
            State.Starting => (TrayIcons.Starting, "Starting recording..."),
            State.Processing => (TrayIcons.Processing[_spinnerFrame], "Saving recording... (not finished yet)"),
            _ => (hasProblem ? TrayIcons.Warning : TrayIcons.Idle, "Idle"),
        };
        if (hasProblem)
            text = "NEEDS ATTENTION (open the menu)\n" + text;
        _tray.Icon = icon;
        // NotifyIcon.Text throws above 127 characters.
        _tray.Text = Truncate("Meeting Recorder\n" + text, 127);

        _problemItem.Visible = _problemSeparator.Visible = hasProblem;
        _problemItem.Text = _problems.Count switch
        {
            0 => "",
            1 => "Needs attention: " + Truncate(_problems[0], MaxMenuTextLength),
            _ => $"Needs attention: {_problems.Count} problems",
        };
    }

    /// <summary>Records something the user must act on and switches the tray icon to a warning until they look at it.</summary>
    private void AddProblem(string text)
    {
        _problems.Add($"{DateTime.Now:HH:mm}  {text}");
        RefreshIcon();
        _tray.ShowBalloonTip(10000, "Meeting Recorder needs attention", text, ToolTipIcon.Warning);
    }

    private void ShowProblems()
    {
        if (_problems.Count == 0)
            return;
        string details = string.Join("\n\n", _problems);
        MessageBox.Show(details + "\n\nThe warning icon clears when you close this message.",
            "Meeting Recorder: needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _problems.Clear();
        RefreshIcon();
        CheckForUpdate();
    }

    private void CheckHealth()
    {
        TimeSpan inState = DateTime.UtcNow - _stateSince;
        if (_state == State.Starting && inState > StartTimeout)
            _session?.Kill($"Recording did not start within {StartTimeout.TotalSeconds:0} seconds.");
        else if (_state == State.Processing && _session != null && inState > SaveLimit(_session))
            _session.Kill($"Saving did not finish within {SaveLimit(_session).TotalMinutes:0} minutes; the files may be incomplete.");
        else if (_state == State.Recording)
            CheckDiskSpace();
    }

    /// <summary>Converting the audio after stop takes about a second per minute recorded, so long recordings get longer.</summary>
    private TimeSpan SaveLimit(RecordingProcess session) =>
        SaveTimeout + TimeSpan.FromSeconds(Math.Max(0, (_stateSince.ToLocalTime() - session.StartedAt).TotalMinutes) * 5);

    private void CheckDiskSpace()
    {
        if (_lowDiskReported)
            return;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(EnsureRoot())!);
            if (drive.AvailableFreeSpace < LowDiskSpaceBytes)
            {
                _lowDiskReported = true;
                AddProblem($"Low disk space: {drive.AvailableFreeSpace / (double)(1L << 30):0.0} GB left on {drive.Name}. "
                    + "Free up space or the recording may fail.");
            }
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
        }
    }

    private void BuildStartMenu()
    {
        var items = _startItem.DropDownItems;
        items.Clear();

        var screens = Screen.AllScreens;
        if (screens.Length == 1)
        {
            items.Add(new ToolStripMenuItem("Full screen", null, (_, _) => StartRecording(RecordingTarget.ForDisplay(screens[0].DeviceName))));
        }
        else
        {
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                string label = $"Full screen: display {i + 1} ({screen.Bounds.Width}x{screen.Bounds.Height}){(screen.Primary ? ", main" : "")}";
                items.Add(new ToolStripMenuItem(label, null, (_, _) => StartRecording(RecordingTarget.ForDisplay(screen.DeviceName))));
            }
        }

        items.Add(new ToolStripSeparator());
        var windows = WindowList.GetRecordableWindows();
        if (windows.Count == 0)
            items.Add(new ToolStripMenuItem("(no windows found)") { Enabled = false });
        foreach (var window in windows)
        {
            items.Add(new ToolStripMenuItem(Truncate(window.Title, MaxMenuTextLength), null,
                (_, _) => StartRecording(RecordingTarget.ForWindow(window))) { ToolTipText = window.Title });
        }
    }

    private void BuildRecordingsMenu()
    {
        var items = _recordingsItem.DropDownItems;
        items.Clear();

        var recent = Directory.Exists(_settings.RecordingsRoot)
            ? new DirectoryInfo(_settings.RecordingsRoot).GetDirectories()
                .OrderByDescending(d => d.CreationTimeUtc)
                .Take(MaxRecentRecordings)
                .ToList()
            : [];

        if (recent.Count == 0)
            items.Add(new ToolStripMenuItem("(no recordings yet)") { Enabled = false });

        string? activeFolder = _state == State.Idle ? null : _session?.FolderPath;
        foreach (var folder in recent)
        {
            if (string.Equals(folder.FullName.TrimEnd('\\'), activeFolder?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                string status = _state == State.Processing ? "saving" : "recording";
                items.Add(new ToolStripMenuItem($"{Truncate(folder.Name, MaxMenuTextLength)} ({status})") { Enabled = false });
                continue;
            }
            var folderItem = new ToolStripMenuItem(Truncate(folder.Name, MaxMenuTextLength));
            folderItem.DropDownItems.Add(new ToolStripMenuItem("Open folder", null, (_, _) => OpenFolder(folder.FullName)));
            folderItem.DropDownItems.Add(new ToolStripSeparator());
            foreach (var file in folder.GetFiles().OrderBy(f => f.Name))
                folderItem.DropDownItems.Add(new ToolStripMenuItem($"{file.Name} ({FormatSize(file.Length)})", null, (_, _) => OpenFile(file.FullName)));
            items.Add(folderItem);
        }
    }

    private void BuildDeviceMenu(ToolStripMenuItem parent, DataFlow flow, Func<string> getId, Action<string> setId)
    {
        var items = parent.DropDownItems;
        items.Clear();
        string current = getId();

        void Add(string label, string id) =>
            items.Add(new ToolStripMenuItem(label, null, (_, _) => SaveDevice(setId, id)) { Checked = current == id });

        Add("Off", AppSettings.DisabledDevice);
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            string defaultName = enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out var defaultDevice)
                ? defaultDevice!.FriendlyName
                : "none";
            defaultDevice?.Dispose();
            Add($"Windows default ({defaultName})", AppSettings.DefaultDevice);
            items.Add(new ToolStripSeparator());
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
            {
                using (device)
                    Add(device.FriendlyName, device.ID);
            }
        }
        catch (Exception ex)
        {
            items.Add(new ToolStripMenuItem($"(cannot list devices: {ex.Message})") { Enabled = false });
        }
    }

    private void SaveDevice(Action<string> setId, string id)
    {
        setId(id);
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddProblem($"Could not save the audio device choice: {ex.Message}");
        }
    }

    private void ChangeRecordingsRoot()
    {
        if (_state != State.Idle)
            return;
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose where new recordings are saved",
            UseDescriptionForTitle = true,
            InitialDirectory = Directory.Exists(_settings.RecordingsRoot) ? _settings.RecordingsRoot : "",
            ShowNewFolderButton = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK || string.IsNullOrEmpty(dialog.SelectedPath))
            return;

        string previous = _settings.RecordingsRoot;
        try
        {
            string probe = Path.Combine(dialog.SelectedPath, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            _settings.RecordingsRoot = dialog.SelectedPath;
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _settings.RecordingsRoot = previous;
            MessageBox.Show($"Recordings can't be saved to {dialog.SelectedPath}:\n{ex.Message}", "Meeting Recorder",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _lowDiskReported = false;
        ShowBalloon("Recordings folder changed", $"New recordings go to {_settings.RecordingsRoot}. Existing ones stay where they are.", ToolTipIcon.Info);
    }

    private void StartRecording(RecordingTarget target)
    {
        if (_state != State.Idle)
            return;
        if (target.WindowHandle != IntPtr.Zero && !WindowList.IsAlive(target.WindowHandle))
        {
            ShowBalloon("Window is gone", "That window was closed. Pick another one.", ToolTipIcon.Info);
            return;
        }

        try
        {
            _session = new RecordingProcess(_ui, target);
            _session.Started += _ => SetState(State.Recording);
            _session.Warning += (_, text) => AddProblem(text);
            _session.Stopping += _ => { if (_state is State.Recording or State.Starting) SetState(State.Processing); };
            _session.Finished += OnSessionFinished;
            _lowDiskReported = false;
            SetState(State.Starting);
            _session.Start();
            CheckDiskSpace();
        }
        catch (Exception ex)
        {
            _session = null;
            SetState(State.Idle);
            AddProblem($"Could not start recording: {ex.Message}");
        }
    }

    private void StopRecording()
    {
        if (_session == null || _state is not (State.Recording or State.Starting))
            return;
        SetState(State.Processing);
        _session.Stop();
    }

    private void OnSessionFinished(RecordingProcess session, string? error)
    {
        _session = null;
        _lastFolder = session.FolderPath ?? _lastFolder;
        SetState(State.Idle);
        // The worker renames its files itself; this covers a worker that was killed or crashed first.
        if (session.FolderPath != null && Directory.Exists(session.FolderPath)
            && RecordingSession.FinalizeFiles(session.FolderPath) is { } renameError)
            AddProblem(renameError);

        if (_exitAfterStop)
        {
            ExitNow();
            return;
        }

        if (error != null)
            AddProblem($"Recording failed: {error}");
        else if (_problems.Count == 0)
            ShowBalloon("Recording saved",
                Join(session.EndNote ?? "", "Click to open " + Path.GetFileName(session.FolderPath ?? "")), ToolTipIcon.Info);
        CheckForUpdate();
    }

    /// <summary>
    /// Installs the latest published build when it differs from this one (newer, or older after a rollback),
    /// but never during a recording or while a warning is unread (restarting would lose it). A build whose
    /// install already failed is not retried, so a broken build can't cause a restart loop.
    /// </summary>
    private void CheckForUpdate()
    {
        if (!_updater.IsEnabled)
            return;
        string? latest = _updater.FindLatestBuild();
        if (latest == null || latest == _updater.InstalledBuildId)
        {
            _updater.WriteState("running");
            return;
        }
        if (_updater.LastAttemptId == latest)
        {
            _updater.WriteState($"failed: build {latest} did not install");
            if (_failedBuildReported != latest)
            {
                _failedBuildReported = latest;
                AddProblem($"Update {latest} could not be installed. Run install.cmd to repair the install.");
            }
            return;
        }
        if (_state != State.Idle)
        {
            _updater.WriteState($"waiting: build {latest} until the recording in progress ends");
            return;
        }
        if (_problems.Count > 0)
        {
            _updater.WriteState($"waiting: build {latest} until the tray warning is read");
            return;
        }
        try
        {
            _updater.LaunchUpdate(latest);
            ExitNow();
        }
        catch (Exception ex)
        {
            _updater.WriteState($"failed: build {latest}: {ex.Message}", latest);
            AddProblem($"Could not install update {latest}: {ex.Message}");
        }
    }

    private void Exit()
    {
        if (_state == State.Idle)
        {
            ExitNow();
            return;
        }
        _exitAfterStop = true;
        StopRecording();
    }

    private void ExitNow()
    {
        _watchdog.Dispose();
        _spinner.Dispose();
        _updater.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _menu.Dispose();
        ExitThread();
    }

    private string EnsureRoot()
    {
        Directory.CreateDirectory(_settings.RecordingsRoot);
        return _settings.RecordingsRoot;
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon) =>
        _tray.ShowBalloonTip(5000, title, string.IsNullOrEmpty(text) ? title : text, icon);

    private static void OpenFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });

    private static void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });

    private static string Join(string first, string second) =>
        string.IsNullOrEmpty(second) ? first : first + "\n" + second;

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..(max - 3)] + "...";

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };
}
