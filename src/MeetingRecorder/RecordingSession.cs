using ScreenRecorderLib;

namespace MeetingRecorder;

/// <summary>
/// What to record: one window, or one display ("full screen"). <see cref="SourceFile"/> replays a video file
/// instead, for repeatable performance tests that don't capture the screen.
/// </summary>
sealed record RecordingTarget(string Name, IntPtr WindowHandle, string? DisplayDeviceName, string? SourceFile = null)
{
    /// <summary>Records no video, only the separate audio files: the audio of an auto-recorded call, which has a video per window.</summary>
    public bool IsAudioOnly { get; init; }

    /// <summary>
    /// The program whose microphone is recorded: the meeting app of a call's audio. For a window it is found from the
    /// window when not set; a display has none.
    /// </summary>
    public uint? AppProcessId { get; init; }

    /// <summary>What is recorded, for the journal and README.md.</summary>
    public string Description => IsAudioOnly ? Name
        : SourceFile != null ? $"the video file {SourceFile} (a test)"
        : WindowHandle != IntPtr.Zero ? $"the window \"{Name}\""
        : $"the whole display {DisplayDeviceName}";

    public static RecordingTarget ForWindow(WindowInfo window) => new(window.Title, window.Handle, null);

    public static RecordingTarget ForDisplay(string deviceName) => new("fullscreen", IntPtr.Zero, deviceName);

    public static RecordingTarget ForAudio(string name, uint? appProcessId) =>
        new(name, IntPtr.Zero, null) { IsAudioOnly = true, AppProcessId = appProcessId };
}

/// <summary>
/// Where a recording writes: a new folder by default, or <see cref="Folder"/> when it shares one with the other
/// recordings of an auto-recorded call, which record a video each and leave the separate audio files to one audio-only recording.
/// </summary>
sealed record RecordingOutput(string? Folder = null, string VideoFileName = RecordingSession.VideoFileName, bool AudioFiles = true);

/// <summary>
/// One recording: video.mp4, recorded without sound, plus the separate audio files (<see cref="RecordingAudio"/>) as
/// WAV, and a journal of what was recorded from where. <see cref="RecordingFinisher"/> then mixes the sound into the
/// video, compresses the audio and writes README.md.
/// </summary>
/// <remarks>All events are raised on the UI thread.</remarks>
sealed class RecordingSession
{
    public const string VideoFileName = "video.mp4";

    private readonly SynchronizationContext _ui;
    private readonly AppSettings _settings;
    private readonly RecordingTarget _target;
    private readonly RecordingOutput _output;
    private readonly RecordingJournal _journal;

    private Recorder? _recorder;
    private WindowTracker? _tracker;
    private RecordingAudio? _audio;
    private Task? _audioStopped;
    private bool _videoStarted;
    private bool _stopRequested;
    private bool _finished;

    public string FolderPath { get; }
    public string TargetName => _target.Name;
    public DateTime StartedAt { get; }

    /// <summary>Raised once video and audio are capturing.</summary>
    public event Action<RecordingSession>? Started;

    /// <summary>Raised once when the recording starts ending (stopped, or ended on its own) and the files are being saved.</summary>
    public event Action? Stopping;

    /// <summary>Raised once every file is closed and has its final name. The argument is an error message, or null on success.</summary>
    public event Action<RecordingSession, string?>? Finished;

    /// <summary>
    /// Raised as soon as something goes wrong that the user should act on, e.g. a source is not being recorded.
    /// The recording itself may carry on.
    /// </summary>
    public event Action<string>? Warning;

    /// <summary>A line for the audio log: which sources are recorded, every switch and device change.</summary>
    public event Action<string>? Log;

    /// <summary>Why the recording ended when the user didn't stop it and nothing went wrong, e.g. the window closed.</summary>
    public string? EndNote { get; private set; }

    /// <summary>
    /// Skip Windows Graphics Capture and go straight to the cropped display capture for windows.
    /// Set automatically after the first failure.
    /// </summary>
    public static bool GraphicsCaptureUnavailable { get; set; }

    private RecordingSession(SynchronizationContext ui, AppSettings settings, RecordingTarget target, RecordingOutput output)
    {
        _ui = ui;
        _settings = settings;
        _target = target;
        _output = output;
        StartedAt = DateTime.Now;
        FolderPath = output.Folder is { } folder
            ? Directory.CreateDirectory(folder).FullName
            : CreateFolder(settings.RecordingsRoot, StartedAt, target.Name);
        _journal = new RecordingJournal(FolderPath, target.IsAudioOnly ? "audio" : output.VideoFileName);
    }

    public static RecordingSession Create(SynchronizationContext ui, AppSettings settings, RecordingTarget target,
        RecordingOutput? output = null) => new(ui, settings, target, output ?? new RecordingOutput());

    /// <summary>The files this recording writes.</summary>
    private IEnumerable<string> OwnFileNames =>
        (_target.IsAudioOnly ? [] : (string[])[_output.VideoFileName]).Concat(_audio?.FileNamesUsed ?? []);

    /// <summary>The program behind what is recorded, whose microphone is recorded; null for a display.</summary>
    private AppProcess? App { get; set; }

    public void Start()
    {
        App = _target.AppProcessId is { } pid ? ProcessTree.Describe(pid)
            : _target.WindowHandle != IntPtr.Zero ? ProcessTree.ForWindow(_target.WindowHandle)
            : null;
        _journal.Write(new JournalEntry
        {
            Kind = JournalEntry.Recording, Target = _target.Name, Text = _target.Description,
            Auto = _output.Folder != null, App = App?.ToString(),
        });
        if (_target.IsAudioOnly)
        {
            StartCapturing();
            return;
        }
        if (_target.SourceFile != null)
        {
            StartVideo(new VideoRecordingSource(_target.SourceFile), null);
            return;
        }
        if (_target.WindowHandle == IntPtr.Zero)
        {
            StartVideo(new DisplayRecordingSource(_target.DisplayDeviceName), null);
            return;
        }

        WindowList.RestoreIfMinimized(_target.WindowHandle);
        if (GraphicsCaptureUnavailable)
            StartCroppedWindowVideo();
        else
            StartVideo(new WindowRecordingSource(_target.WindowHandle), null);
    }

    /// <summary>
    /// Fallback window capture: record the window's display and crop to the window, following it as it moves.
    /// Anything covering the window is recorded too.
    /// </summary>
    private void StartCroppedWindowVideo()
    {
        _tracker = new WindowTracker(_target.WindowHandle);
        if (_tracker.GetCrop() is not { } crop)
            throw new InvalidOperationException("The window is not visible on any display.");

        _tracker.WindowClosed += () =>
        {
            if (!_stopRequested && !_finished)
            {
                EndNote = "Stopped because the window was closed.";
                Stop();
            }
        };
        _tracker.CropChanged += newCrop => _recorder?.GetDynamicOptionsBuilder()
            .SetDynamicOutputOptions(new DynamicOutputOptions { SourceRect = ToScreenRect(newCrop) })
            .Apply();
        StartVideo(new DisplayRecordingSource(_tracker.Screen.DeviceName), crop);
    }

    private void StartVideo(RecordingSourceBase source, Rectangle? crop)
    {
        if (source is WindowRecordingSource window)
        {
            window.IsCursorCaptureEnabled = _settings.CaptureMouseCursor;
            // Windows' own capture border flashes as it hides and redraws it; RecordingOverlay draws a steady one.
            // Turning it off needs Windows 11.
            if (Environment.OSVersion.Version.Build >= 22000)
                window.IsBorderRequired = false;
        }
        else if (source is DisplayRecordingSource display)
            display.IsCursorCaptureEnabled = _settings.CaptureMouseCursor;

        OutputOptions? output = null;
        if (crop is { } c)
            output = new OutputOptions { SourceRect = ToScreenRect(c) };
        if (ScaledFrameSize(crop?.Size ?? SourceSize(source), _settings.MaxVideoHeight) is { } scaled)
        {
            output ??= new OutputOptions();
            output.OutputFrameSize = scaled;
            output.Stretch = StretchMode.Uniform;
        }

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = [source] },
            OutputOptions = output,
            // The sound is added once the recording is saved, from the audio files, which follow device changes.
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.Quality,
                    // High gave no smaller files than Main on a GPU encoder in quality mode; Main plays everywhere.
                    EncoderProfile = H264Profile.Main,
                },
                Quality = _settings.VideoQuality,
                Framerate = _settings.VideoFramerate,
                // Variable frame rate: static screens (slides, idle meetings) produce almost no encoder work.
                IsFixedFramerate = false,
                IsHardwareEncodingEnabled = _settings.UseHardwareEncoder,
                // Fragmented MP4 stays playable even if the app or PC dies mid-recording.
                IsFragmentedMp4Enabled = true,
            },
            MouseOptions = new MouseOptions { IsMousePointerEnabled = _settings.CaptureMouseCursor },
        };

        Record(options);
    }

    private void Record(RecorderOptions options)
    {
        var recorder = Recorder.CreateRecorder(options);
        _recorder = recorder;
        // Events from a recorder that was replaced by the fallback are ignored.
        recorder.OnStatusChanged += (_, e) => _ui.Post(_ => { if (_recorder == recorder) OnVideoStatusChanged(e.Status); }, null);
        recorder.OnRecordingComplete += (_, _) => _ui.Post(_ => { if (_recorder == recorder) OnVideoEnded(null); }, null);
        recorder.OnRecordingFailed += (_, e) => _ui.Post(_ => { if (_recorder == recorder) OnVideoFailed(e.Error); }, null);
        recorder.Record(Path.Combine(FolderPath, TempFileName(_output.VideoFileName)));
    }

    private void OnVideoFailed(string error)
    {
        bool canFallBack = _target.WindowHandle != IntPtr.Zero && _tracker == null && !_videoStarted && !_stopRequested;
        if (!canFallBack)
        {
            OnVideoEnded(error);
            return;
        }

        // Windows Graphics Capture can be missing or disabled by policy; it fails before the first frame.
        GraphicsCaptureUnavailable = true;
        _recorder!.Dispose();
        _recorder = null;
        try
        {
            StartCroppedWindowVideo();
        }
        catch (Exception ex)
        {
            OnVideoEnded($"{error}\nFallback capture also failed: {ex.Message}");
        }
    }

    private static ScreenRect ToScreenRect(Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    private static Size? SourceSize(RecordingSourceBase source)
    {
        try
        {
            var size = Recorder.GetOutputDimensionsForRecordingSources([source]).CombinedOutputSize;
            return new Size((int)size.Width, (int)size.Height);
        }
        catch (Exception)
        {
            // Unknown size: record unscaled rather than fail.
            return null;
        }
    }

    /// <summary>
    /// The frame size to encode when the source is taller than <paramref name="maxHeight"/>: same aspect ratio,
    /// even dimensions for H.264. Null when no scaling is needed or the source size is unknown.
    /// </summary>
    private static ScreenSize? ScaledFrameSize(Size? source, int maxHeight)
    {
        if (source is not { } s || maxHeight <= 0 || s.Height <= maxHeight)
            return null;
        int height = maxHeight & ~1;
        int width = (int)Math.Round(s.Width * (double)height / s.Height) & ~1;
        return new ScreenSize(width, height);
    }

    public void Stop()
    {
        if (!_stopRequested && !_finished)
            Stopping?.Invoke();
        _stopRequested = true;
        StopAudio();
        _recorder?.Stop();
        if (_target.IsAudioOnly)
            OnVideoEnded(null);
    }

    private void OnVideoStatusChanged(RecorderStatus status)
    {
        if (status != RecorderStatus.Recording || _videoStarted || _finished)
            return;

        // Audio starts with the first video frame so the files line up when played side by side.
        StartCapturing();
    }

    private void StartCapturing()
    {
        _videoStarted = true;
        if (!_target.IsAudioOnly)
            _journal.Write(new JournalEntry
            {
                Kind = JournalEntry.VideoStart, File = _output.VideoFileName, Target = _target.Name, Text = _target.Description, App = App?.ToString(),
            });
        if (_output.AudioFiles)
        {
            _audio = new RecordingAudio(_ui, _settings, FolderPath, App, _journal);
            _audio.Warning += text => Warning?.Invoke(text);
            _audio.Log += text => Log?.Invoke(text);
            StartAudio(_audio);
        }
        Started?.Invoke(this);
    }

    private void StartAudio(RecordingAudio audio)
    {
        try
        {
            audio.Start();
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"The audio is not being recorded: {ex.Message}");
        }
    }

    private Task StopAudio() => _audioStopped ??= _audio?.StopAsync() ?? Task.CompletedTask;

    private async void OnVideoEnded(string? error)
    {
        if (_finished)
            return;
        if (!_stopRequested && _videoStarted)
            Stopping?.Invoke();
        _finished = true;

        try
        {
            await StopAudio();
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"The audio files may be incomplete: {ex.Message}");
        }
        if (!_target.IsAudioOnly && _videoStarted)
            _journal.Write(new JournalEntry { Kind = JournalEntry.VideoEnd, File = _output.VideoFileName, Error = error });
        if (error == null && !_stopRequested)
        {
            if (_target.WindowHandle != IntPtr.Zero && !WindowList.IsAlive(_target.WindowHandle))
                EndNote = "Stopped because the window was closed.";
            else
                Warning?.Invoke("The recording stopped on its own before you stopped it.");
        }

        _tracker?.Dispose();
        _recorder?.Dispose();
        _recorder = null;
        if (FinalizeFiles(FolderPath, OwnFileNames) is { } renameError)
            Warning?.Invoke(renameError);
        if (error != null)
            DeleteFolderIfEmpty();
        Finished?.Invoke(this, error);
    }

    /// <summary>
    /// The name of the <paramref name="number"/>th video in a folder with a video per window: video.mp4, video-2.mp4, ...
    /// </summary>
    public static string NumberedVideoFileName(int number) =>
        number == 1 ? VideoFileName : $"{Path.GetFileNameWithoutExtension(VideoFileName)}-{number}{Path.GetExtension(VideoFileName)}";

    /// <summary>Removes the recording folder when a recording failed before writing anything.</summary>
    public void DeleteFolderIfEmpty()
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(FolderPath).Any())
                Directory.Delete(FolderPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The name a file has while it is being written, so it can't be opened by mistake before it is complete.
    /// </summary>
    public static string TempFileName(string fileName) => fileName + ".tmp";

    /// <summary>
    /// Gives the files <paramref name="names"/> in a recording folder their final names; every file in it when null,
    /// which must wait until nothing writes to the folder any more. Safe to call again, e.g. after the worker was
    /// killed (a fragmented MP4 cut short is still playable, and so is a WAV). Returns an error message, or null.
    /// </summary>
    public static string? FinalizeFiles(string folder, IEnumerable<string>? names = null)
    {
        names ??= Directory.EnumerateFiles(folder, TempFileName("*" + Path.GetExtension(VideoFileName)))
            .Concat(Directory.EnumerateFiles(folder, TempFileName("*.wav")))
            .Select(path => Path.GetFileNameWithoutExtension(path));
        var errors = new List<string>();
        foreach (string name in names.ToList())
        {
            string temp = Path.Combine(folder, TempFileName(name));
            if (!File.Exists(temp))
                continue;
            try
            {
                File.Move(temp, Path.Combine(folder, name));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
        return errors.Count == 0 ? null
            : $"Could not finish saving {Path.GetFileName(folder)} ({string.Join("; ", errors)}). Rename the .tmp files by hand.";
    }

    /// <summary>Creates a new recording folder, <c>yyyy-MM-dd_HH-mm-ss-&lt;target name&gt;</c>, and returns its path.</summary>
    public static string CreateFolder(string root, DateTime startedAt, string targetName)
    {
        string path = UniqueFolderPath(root, startedAt, targetName);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Renames a recording folder after <paramref name="targetName"/> unless it already is, and returns its path.</summary>
    public static string RenameFolder(string folder, DateTime startedAt, string targetName)
    {
        if (Path.GetFileName(folder).StartsWith(FolderName(startedAt, targetName), StringComparison.Ordinal))
            return folder;
        string path = UniqueFolderPath(Path.GetDirectoryName(folder)!, startedAt, targetName);
        Directory.Move(folder, path);
        return path;
    }

    private static string FolderName(DateTime startedAt, string targetName) =>
        $"{startedAt:yyyy-MM-dd_HH-mm-ss}-{SanitizeFileName(targetName)}";

    private static string UniqueFolderPath(string root, DateTime startedAt, string targetName)
    {
        string baseName = FolderName(startedAt, targetName);
        string path = Path.Combine(root, baseName);
        for (int i = 2; Directory.Exists(path); i++)
            path = Path.Combine(root, $"{baseName} ({i})");
        return path;
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray();
        string cleaned = string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (cleaned.Length > 60)
            cleaned = cleaned[..60];
        cleaned = cleaned.TrimEnd(' ', '.');
        return cleaned.Length == 0 ? "window" : cleaned;
    }
}
