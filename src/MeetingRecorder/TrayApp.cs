using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace MeetingRecorder;

/// <summary>
/// The whole UI: a tray icon whose colour shows the state (grey idle, red recording, amber saving)
/// and a context menu to start, stop and browse recordings.
/// </summary>
/// <remarks>
/// Several recordings can run at once, each of a different window or display; the recording icon shows how many.
/// Anything the user must act on (a failed recording, a source that stopped, low disk space) switches the icon
/// to a warning that stays until the user opens the details, so it isn't missed when notifications are muted.
/// Calls in meeting apps are auto-recorded by an <see cref="AutoRecorder"/> per app in <see cref="MeetingApps.All"/>.
/// </remarks>
sealed class TrayApp : ApplicationContext, IAutoRecordHost
{
    private const int MaxRecentRecordings = 10;
    private const int MaxMenuTextLength = 70;
    private const long LowDiskSpaceBytes = 2L << 30;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromMinutes(2);

    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _recordingsItem;
    private readonly ToolStripMenuItem _changeRootItem;
    private readonly ToolStripMenuItem _systemAudioItem;
    private readonly ToolStripMenuItem _microphoneItem;
    private readonly ToolStripMenuItem _autoRecordItem;
    private readonly ToolStripMenuItem _problemItem;
    private readonly ToolStripSeparator _problemSeparator = new() { Visible = false };
    private readonly System.Windows.Forms.Timer _watchdog = new() { Interval = 5000 };
    private readonly System.Windows.Forms.Timer _animation = new() { Interval = 125 };
    private readonly System.Windows.Forms.Timer _autoRecordTimer = new() { Interval = AutoRecorder.CheckMilliseconds };
    private readonly List<AutoRecorder> _autoRecorders;
    private int _animationFrame;
    private readonly List<string> _problems = [];
    private readonly EventWaitHandle _exitRequest = new(false, EventResetMode.AutoReset, @"Local\MeetingRecorder-Exit");
    private readonly EventWaitHandle _exitRefused = new(false, EventResetMode.AutoReset, @"Local\MeetingRecorder-ExitRefused");
    private readonly SynchronizationContext _ui;
    private readonly List<ActiveRecording> _recordings = [];
    private bool _lowDiskReported;
    private string? _lastFolder;
    private bool _exitAfterStop;

    public TrayApp()
    {
        List<string> dataProblems = AppInitializer.PrepareDataFolder();
        _settings = AppSettings.Load(out string? settingsLoadError);
        // Before anything reads the settings, so they are migrated.
        dataProblems.AddRange(AppInitializer.Run(_settings, settingsLoadError));
        _autoRecorders = MeetingApps.All.Select(app => new AutoRecorder(app, this)).ToList();
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

        // With several recordings this item opens a submenu instead, so a click on it must not stop them all.
        _stopItem = new ToolStripMenuItem("Stop recording", null, (sender, _) =>
        {
            if (sender is ToolStripMenuItem { HasDropDownItems: false })
                StopAll();
        });

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

        _autoRecordItem = new ToolStripMenuItem("Auto-record meetings")
        {
            ToolTipText = "Records a call's windows and audio in the ticked apps when the call starts, and stops when it ends.",
        };
        foreach (var recorder in _autoRecorders)
        {
            _autoRecordItem.DropDownItems.Add(new ToolStripMenuItem(recorder.App.DisplayName, null,
                (sender, _) => ToggleAutoRecord(recorder, (ToolStripMenuItem)sender!))
            {
                Checked = _settings.IsAutoRecordOn(recorder.App),
            });
        }

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
            _autoRecordItem,
            _systemAudioItem,
            _microphoneItem,
            new ToolStripSeparator(),
            new ToolStripMenuItem("Open warnings log", null, (_, _) => OpenWarningLog()),
            new ToolStripMenuItem("Exit", null, (_, _) => Exit()),
        ]);

        _tray = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _tray.MouseDoubleClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
                return;
            if (_recordings.Any(r => r.IsCapturing))
                StopAll();
            else if (_problems.Count > 0)
                ShowProblems();
            else if (_recordings.Count == 0)
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
        _autoRecordTimer.Tick += (_, _) =>
        {
            foreach (var recorder in _autoRecorders.Where(r => _settings.IsAutoRecordOn(r.App)))
                recorder.Check();
        };
        _autoRecordTimer.Enabled = _autoRecorders.Any(r => _settings.IsAutoRecordOn(r.App));
        _animation.Tick += (_, _) =>
        {
            // A common multiple of every animation's frame count, so each one loops cleanly.
            _animationFrame = (_animationFrame + 1) % (TrayIcons.Processing.Length * TrayIcons.RecordingFrames);
            RefreshIcon();
        };
        Application.ThreadException += (_, e) => AddProblem($"Unexpected error: {e.Exception.Message}");

        RefreshState();
        foreach (string problem in dataProblems)
            AddProblem(problem);

        new Thread(() =>
        {
            while (_exitRequest.WaitOne())
                _ui.Post(_ => OnExitRequested(), null);
        }) { IsBackground = true, Name = "Exit request listener" }.Start();
    }

    /// <summary>Updates the menu, timers and icon after a recording starts, changes phase or finishes.</summary>
    private void RefreshState()
    {
        bool idle = _recordings.Count == 0;
        _startItem.Enabled = !_exitAfterStop;
        _systemAudioItem.Enabled = _microphoneItem.Enabled = _changeRootItem.Enabled = idle;
        _watchdog.Enabled = !idle;
        bool animate = _recordings.Any(r => r.Phase is RecordingPhase.Recording or RecordingPhase.Saving);
        if (animate != _animation.Enabled)
        {
            _animationFrame = 0;
            _animation.Enabled = animate;
        }
        BuildStopMenu();
        RefreshIcon();
    }

    private void RefreshIcon()
    {
        bool hasProblem = _problems.Count > 0;
        // A call's audio counts only while none of its windows is being recorded, e.g. between the pre-join screen
        // and the meeting window.
        int recording = Math.Max(_recordings.Count(r => r.Phase == RecordingPhase.Recording && !r.IsAudio),
            _recordings.Any(r => r.Phase == RecordingPhase.Recording) ? 1 : 0);
        _tray.Icon = recording > 0 ? Frame(TrayIcons.Recording(recording, hasProblem))
            : _recordings.Any(r => r.Phase == RecordingPhase.Starting) ? TrayIcons.Starting
            : _recordings.Count > 0 ? Frame(TrayIcons.Processing)
            : hasProblem ? TrayIcons.Warning
            : TrayIcons.Idle;

        string text = _recordings.Count == 0 ? "Idle" : string.Join("\n", _recordings.Select(r => r.Phase switch
        {
            RecordingPhase.Starting => $"Starting: {r.Process.TargetName}",
            RecordingPhase.Recording => $"Recording since {r.Process.StartedAt:HH:mm}: {r.Process.TargetName}",
            _ => $"Saving (not finished yet): {r.Process.TargetName}",
        }));
        if (hasProblem)
            text = "NEEDS ATTENTION (open the menu)\n" + text;
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

    private Icon Frame(Icon[] frames) => frames[_animationFrame % frames.Length];

    /// <summary>One recording: "Stop recording" stops it. Several: a submenu to stop each one, or all.</summary>
    private void BuildStopMenu()
    {
        _stopItem.Enabled = _recordings.Any(r => r.IsCapturing);
        var capturing = _recordings.Where(r => r.IsCapturing && !r.IsAudio).ToList();
        _stopItem.DropDownItems.Clear();
        if (capturing.Count < 2)
            return;
        foreach (var recording in capturing)
        {
            _stopItem.DropDownItems.Add(new ToolStripMenuItem(Truncate(recording.Process.TargetName, MaxMenuTextLength), null,
                (_, _) => StopByHand(recording)) { ToolTipText = recording.Process.TargetName });
        }
        _stopItem.DropDownItems.Add(new ToolStripSeparator());
        _stopItem.DropDownItems.Add(new ToolStripMenuItem("Stop all", null, (_, _) => StopAll()));
    }

    /// <summary>
    /// Records something the user must act on: logged, and the tray icon shows a warning until they look at it.
    /// Warnings are not kept across restarts; the log has them.
    /// </summary>
    private void AddProblem(string text)
    {
        WarningLog.Append(text);
        _problems.Add($"{DateTime.Now:HH:mm}  {text}");
        RefreshIcon();
        _tray.ShowBalloonTip(10000, "Meeting Recorder needs attention", text, ToolTipIcon.Warning);
    }

    private void ShowProblems()
    {
        if (_problems.Count == 0)
            return;
        string details = string.Join("\n\n", _problems);
        MessageBox.Show(details + $"\n\nThe warning icon clears when you close this message. All warnings are kept in {WarningLog.LogPath}.",
            "Meeting Recorder: needs attention", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        _problems.Clear();
        RefreshIcon();
    }

    private void CheckHealth()
    {
        foreach (var recording in _recordings.ToList())
        {
            TimeSpan inPhase = DateTime.UtcNow - recording.PhaseSince;
            if (recording.Phase == RecordingPhase.Starting && inPhase > StartTimeout)
                recording.Process.Kill($"Recording did not start within {StartTimeout.TotalSeconds:0} seconds.");
            else if (recording.Phase == RecordingPhase.Saving && inPhase > SaveLimit(recording))
                recording.Process.Kill($"Saving did not finish within {SaveLimit(recording).TotalMinutes:0} minutes; the files may be incomplete.");
        }
        if (_recordings.Any(r => r.Phase == RecordingPhase.Recording))
            CheckDiskSpace();
    }

    /// <summary>Converting the audio after stop takes about a second per minute recorded, so long recordings get longer.</summary>
    private static TimeSpan SaveLimit(ActiveRecording recording) =>
        SaveTimeout + TimeSpan.FromSeconds(Math.Max(0, (recording.PhaseSince.ToLocalTime() - recording.Process.StartedAt).TotalMinutes) * 5);

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

    private void ToggleAutoRecord(AutoRecorder recorder, ToolStripMenuItem item)
    {
        bool on = !_settings.IsAutoRecordOn(recorder.App);
        _settings.AutoRecord[recorder.App.Id] = on;
        item.Checked = on;
        recorder.Reset();
        _autoRecordTimer.Enabled = _autoRecorders.Any(r => _settings.IsAutoRecordOn(r.App));
        try
        {
            _settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AddProblem($"Could not save the auto-record setting: {ex.Message}");
        }
    }

    private void BuildStartMenu()
    {
        var items = _startItem.DropDownItems;
        items.Clear();

        var screens = Screen.AllScreens;
        if (screens.Length == 1)
        {
            items.Add(StartItem("Full screen", RecordingTarget.ForDisplay(screens[0].DeviceName)));
        }
        else
        {
            for (int i = 0; i < screens.Length; i++)
            {
                var screen = screens[i];
                string label = $"Full screen: display {i + 1} ({screen.Bounds.Width}x{screen.Bounds.Height}){(screen.Primary ? ", main" : "")}";
                items.Add(StartItem(label, RecordingTarget.ForDisplay(screen.DeviceName)));
            }
        }

        items.Add(new ToolStripSeparator());
        var windows = WindowList.GetRecordableWindows();
        if (windows.Count == 0)
            items.Add(new ToolStripMenuItem("(no windows found)") { Enabled = false });
        foreach (var window in windows)
        {
            var item = StartItem(Truncate(window.Title, MaxMenuTextLength), RecordingTarget.ForWindow(window));
            item.ToolTipText = window.Title;
            items.Add(item);
        }
    }

    /// <summary>A "Start recording" entry; ticked and disabled while that target is already being recorded.</summary>
    private ToolStripMenuItem StartItem(string label, RecordingTarget target)
    {
        bool busy = IsBeingRecorded(target);
        return new ToolStripMenuItem(label, null, (_, _) => StartRecording(target)) { Checked = busy, Enabled = !busy };
    }

    private bool IsBeingRecorded(RecordingTarget target) => _recordings.Any(r => r.IsCapturing
        && (target.WindowHandle != IntPtr.Zero
            ? r.Target.WindowHandle == target.WindowHandle
            : target.DisplayDeviceName != null && r.Target.DisplayDeviceName == target.DisplayDeviceName));

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

        foreach (var folder in recent)
        {
            var active = _recordings.FirstOrDefault(r => string.Equals(folder.FullName.TrimEnd('\\'),
                r.Process.FolderPath?.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            if (active != null)
            {
                string status = active.Phase == RecordingPhase.Saving ? "saving" : "recording";
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
        if (_recordings.Count > 0)
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

    /// <summary>
    /// Starts recording <paramref name="target"/> next to any recordings already running, into a folder of its own or,
    /// for an auto-recorded <paramref name="call"/>, into the call's folder. Returns false when it isn't started: the
    /// target is already being recorded, its window is gone, the app is exiting, or it failed.
    /// </summary>
    private bool StartRecording(RecordingTarget target, MeetingCall? call = null)
    {
        if (_exitAfterStop || IsBeingRecorded(target))
            return false;
        if (target.WindowHandle != IntPtr.Zero && !WindowList.IsAlive(target.WindowHandle))
        {
            if (call == null)
                ShowBalloon("Window is gone", "That window was closed. Pick another one.", ToolTipIcon.Info);
            return false;
        }

        ActiveRecording? recording = null;
        try
        {
            string videoFile = call?.NextVideoFile ?? RecordingSession.VideoFileName;
            var output = call == null ? new RecordingOutput() : new RecordingOutput(call.Folder, videoFile, AudioFiles: target.IsAudioOnly);
            var process = new RecordingProcess(_ui, target, output);
            var added = recording = new ActiveRecording(process, target, call, videoFile);
            if (call != null && target.IsAudioOnly)
                call.Audio = added;
            else
                call?.Videos.Add(added);
            process.Started += _ =>
            {
                if (added.Phase != RecordingPhase.Starting)
                    return;
                added.Enter(RecordingPhase.Recording);
                RefreshState();
            };
            process.Warning += (_, text) => AddProblem(text);
            process.Stopping += _ =>
            {
                if (!added.IsCapturing)
                    return;
                added.Enter(RecordingPhase.Saving);
                RefreshState();
            };
            process.Finished += (_, error) => OnRecordingFinished(added, error);
            if (_recordings.Count == 0)
                _lowDiskReported = false;
            _recordings.Add(added);
            RefreshState();
            process.Start();
            CheckDiskSpace();
            return true;
        }
        catch (Exception ex)
        {
            if (recording != null)
            {
                _recordings.Remove(recording);
                call?.Videos.Remove(recording);
                if (call?.Audio == recording)
                    call.Audio = null;
            }
            RefreshState();
            AddProblem($"Could not start recording: {ex.Message}");
            return false;
        }
    }

    private void StopRecording(ActiveRecording recording)
    {
        if (!recording.IsCapturing)
            return;
        recording.Enter(RecordingPhase.Saving);
        RefreshState();
        recording.Process.Stop();
    }

    /// <summary>Stops one recording from the menu; stopping the last window of a call ends the call.</summary>
    private void StopByHand(ActiveRecording recording)
    {
        StopRecording(recording);
        recording.Call?.Recorder.OnStoppedByHand(recording);
    }

    private void StopAll()
    {
        foreach (var recorder in _autoRecorders)
            recorder.EndCall("stopped from the tray");
        foreach (var recording in _recordings.Where(r => r.IsCapturing).ToList())
            StopRecording(recording);
    }

    private void OnRecordingFinished(ActiveRecording recording, string? error)
    {
        RecordingProcess session = recording.Process;
        _recordings.Remove(recording);
        RefreshState();
        if (recording.Call is { } call)
        {
            call.Recorder.OnRecordingFinished(recording, error);
            ExitIfDone();
            return;
        }

        _lastFolder = session.FolderPath ?? _lastFolder;
        // The worker renames its files itself; this covers a worker that was killed or crashed first.
        if (session.FolderPath != null && Directory.Exists(session.FolderPath)
            && RecordingSession.FinalizeFiles(session.FolderPath) is { } renameError)
            AddProblem(renameError);

        if (_exitAfterStop)
        {
            ExitIfDone();
            return;
        }

        if (error != null)
            AddProblem($"Recording failed: {error}");
        else if (_problems.Count == 0)
            ShowBalloon("Recording saved",
                Join(session.EndNote ?? "", "Click to open " + Path.GetFileName(session.FolderPath ?? "")), ToolTipIcon.Info);
    }

    IReadOnlyCollection<ActiveRecording> IAutoRecordHost.Recordings => _recordings;

    bool IAutoRecordHost.IsExiting => _exitAfterStop;

    string IAutoRecordHost.EnsureRecordingsRoot() => EnsureRoot();

    bool IAutoRecordHost.StartRecording(RecordingTarget target, MeetingCall call) => StartRecording(target, call);

    void IAutoRecordHost.StopRecording(ActiveRecording recording) => StopRecording(recording);

    void IAutoRecordHost.AddProblem(string text) => AddProblem(text);

    void IAutoRecordHost.ShowBalloon(string title, string text, ToolTipIcon icon) => ShowBalloon(title, text, icon);

    void IAutoRecordHost.OnCallSaved(string folder)
    {
        _lastFolder = folder;
        if (!_exitAfterStop && _problems.Count == 0)
            ShowBalloon("Recording saved", "Click to open " + Path.GetFileName(folder), ToolTipIcon.Info);
    }

    /// <summary>
    /// Answers a close request from install/uninstall (App deployment standard v2.2, section 4.4): exits when idle;
    /// while a recording is starting, running or saving it refuses and carries on.
    /// </summary>
    private void OnExitRequested()
    {
        if (_recordings.Count == 0)
        {
            ExitNow();
            return;
        }
        _exitRefused.Set();
        ShowBalloon("Install postponed",
            "An install tried to close Meeting Recorder while recording and was refused. Run it again once the recording is saved.",
            ToolTipIcon.Info);
    }

    private void Exit()
    {
        _exitAfterStop = true;
        StopAll();
        RefreshState();
        ExitIfDone();
    }

    /// <summary>Exits once the last recording is saved, after Exit was chosen while recording.</summary>
    private void ExitIfDone()
    {
        if (_exitAfterStop && _recordings.Count == 0)
            ExitNow();
    }

    private void ExitNow()
    {
        _watchdog.Dispose();
        _animation.Dispose();
        _autoRecordTimer.Dispose();
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

    private void OpenWarningLog()
    {
        if (File.Exists(WarningLog.LogPath))
            OpenFile(WarningLog.LogPath);
        else
            ShowBalloon("No warnings logged", "Nothing has needed attention so far.", ToolTipIcon.Info);
    }

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
