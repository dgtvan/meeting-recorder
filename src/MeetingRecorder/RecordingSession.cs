using NAudio.CoreAudioApi;
using ScreenRecorderLib;

namespace MeetingRecorder;

/// <summary>What to record: one window, or one display ("full screen").</summary>
sealed record RecordingTarget(string Name, IntPtr WindowHandle, string? DisplayDeviceName)
{
    public static RecordingTarget ForWindow(WindowInfo window) => new(window.Title, window.Handle, null);

    public static RecordingTarget ForDisplay(string deviceName) => new("fullscreen", IntPtr.Zero, deviceName);
}

/// <summary>
/// One recording: a new folder holding video.mp4 plus a separate WAV per audio source.
/// </summary>
/// <remarks>All events are raised on the UI thread.</remarks>
sealed class RecordingSession
{
    public const string VideoFileName = "video.mp4";
    public const string SystemAudioFileName = "audio-system.wav";
    public const string MicrophoneFileName = "audio-mic.wav";
    private static readonly string[] FileNames = [VideoFileName, SystemAudioFileName, MicrophoneFileName];

    private readonly SynchronizationContext _ui;
    private readonly AppSettings _settings;
    private readonly RecordingTarget _target;
    private readonly List<AudioFileRecorder> _audio = [];

    private Recorder? _recorder;
    private WindowTracker? _tracker;
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

    /// <summary>Why the recording ended when the user didn't stop it and nothing went wrong, e.g. the window closed.</summary>
    public string? EndNote { get; private set; }

    /// <summary>
    /// Skip Windows Graphics Capture and go straight to the cropped display capture for windows.
    /// Set automatically after the first failure.
    /// </summary>
    public static bool GraphicsCaptureUnavailable { get; set; }

    private RecordingSession(SynchronizationContext ui, AppSettings settings, RecordingTarget target)
    {
        _ui = ui;
        _settings = settings;
        _target = target;
        StartedAt = DateTime.Now;
        FolderPath = CreateFolder(settings.RecordingsRoot, StartedAt, target.Name);
    }

    public static RecordingSession Create(SynchronizationContext ui, AppSettings settings, RecordingTarget target) =>
        new(ui, settings, target);

    public void Start()
    {
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
            window.IsCursorCaptureEnabled = _settings.CaptureMouseCursor;
        else if (source is DisplayRecordingSource display)
            display.IsCursorCaptureEnabled = _settings.CaptureMouseCursor;

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions { RecordingSources = [source] },
            OutputOptions = crop is { } c ? new OutputOptions { SourceRect = ToScreenRect(c) } : null,
            AudioOptions = new AudioOptions { IsAudioEnabled = false },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder
                {
                    BitrateMode = H264BitrateControlMode.Quality,
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

        var recorder = Recorder.CreateRecorder(options);
        _recorder = recorder;
        // Events from a recorder that was replaced by the fallback are ignored.
        recorder.OnStatusChanged += (_, e) => _ui.Post(_ => { if (_recorder == recorder) OnVideoStatusChanged(e.Status); }, null);
        recorder.OnRecordingComplete += (_, _) => _ui.Post(_ => { if (_recorder == recorder) OnVideoEnded(null); }, null);
        recorder.OnRecordingFailed += (_, e) => _ui.Post(_ => { if (_recorder == recorder) OnVideoFailed(e.Error); }, null);
        recorder.Record(Path.Combine(FolderPath, TempFileName(VideoFileName)));
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

    public void Stop()
    {
        if (!_stopRequested && !_finished)
            Stopping?.Invoke();
        _stopRequested = true;
        StopAudio();
        _recorder?.Stop();
    }

    private void OnVideoStatusChanged(RecorderStatus status)
    {
        if (status != RecorderStatus.Recording || _videoStarted || _finished)
            return;

        // Audio starts with the first video frame so the files line up when played side by side.
        _videoStarted = true;
        StartAudio(DataFlow.Render, _settings.SystemAudioDeviceId, SystemAudioFileName, "System audio");
        StartAudio(DataFlow.Capture, _settings.MicrophoneDeviceId, MicrophoneFileName, "Microphone");
        Started?.Invoke(this);
    }

    private void StartAudio(DataFlow flow, string deviceId, string fileName, string label)
    {
        if (deviceId == AppSettings.DisabledDevice)
            return;
        try
        {
            var recorder = AudioFileRecorder.TryCreate(flow, deviceId, Path.Combine(FolderPath, TempFileName(fileName)));
            if (recorder == null)
            {
                Warning?.Invoke($"{label} is not being recorded: no audio device found.");
                return;
            }
            recorder.Failed += failed => _ui.Post(_ =>
                Warning?.Invoke($"{label} stopped recording ({failed.DeviceName}): {failed.Error!.Message}"), null);
            recorder.Start();
            _audio.Add(recorder);
            if (recorder.UsedDefaultInstead)
                Warning?.Invoke($"{label}: the chosen device is not available, recording from {recorder.DeviceName} instead.");
        }
        catch (Exception ex)
        {
            Warning?.Invoke($"{label} is not being recorded: {ex.Message}");
        }
    }

    private Task StopAudio() =>
        _audioStopped ??= Task.WhenAll(_audio.Select(a => a.StopAsync()));

    private async void OnVideoEnded(string? error)
    {
        if (_finished)
            return;
        if (!_stopRequested && _videoStarted)
            Stopping?.Invoke();
        _finished = true;

        await StopAudio();
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
        if (FinalizeFiles(FolderPath) is { } renameError)
            Warning?.Invoke(renameError);
        if (error != null && !Directory.EnumerateFileSystemEntries(FolderPath).Any())
            Directory.Delete(FolderPath);
        Finished?.Invoke(this, error);
    }

    /// <summary>
    /// The name a file has while it is being written, so it can't be opened by mistake before it is complete.
    /// </summary>
    public static string TempFileName(string fileName) => Path.ChangeExtension(fileName, ".tmp");

    /// <summary>
    /// Gives every file in a recording folder its final name. Safe to call again, e.g. after the worker was killed
    /// (a fragmented MP4 cut short is still playable). Returns an error message, or null.
    /// </summary>
    public static string? FinalizeFiles(string folder)
    {
        var errors = new List<string>();
        foreach (string name in FileNames)
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

    private static string CreateFolder(string root, DateTime startedAt, string targetName)
    {
        string baseName = $"{startedAt:yyyy-MM-dd_HH-mm-ss}-{SanitizeFileName(targetName)}";
        string path = Path.Combine(root, baseName);
        for (int i = 2; Directory.Exists(path); i++)
            path = Path.Combine(root, $"{baseName} ({i})");
        Directory.CreateDirectory(path);
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
