using NAudio.CoreAudioApi;
using NAudio.Wave;
using ScreenRecorderLib;

namespace MeetingRecorder;

/// <summary>
/// What to record: one window, or one display ("full screen"). <see cref="SourceFile"/> replays a video file
/// instead, for repeatable performance tests that don't capture the screen.
/// </summary>
sealed record RecordingTarget(string Name, IntPtr WindowHandle, string? DisplayDeviceName, string? SourceFile = null)
{
    public static RecordingTarget ForWindow(WindowInfo window) => new(window.Title, window.Handle, null);

    public static RecordingTarget ForDisplay(string deviceName) => new("fullscreen", IntPtr.Zero, deviceName);
}

/// <summary>
/// One recording: a new folder holding video.mp4 plus a separate audio file per source, recorded as WAV and
/// converted to AAC (.m4a) after the recording stops unless <see cref="AppSettings.CompressAudio"/> is off.
/// </summary>
/// <remarks>All events are raised on the UI thread.</remarks>
sealed class RecordingSession
{
    public const string VideoFileName = "video.mp4";
    public const string SystemAudioFileName = "audio-system.wav";
    public const string MicrophoneFileName = "audio-mic.wav";
    private const string CompressedAudioExtension = ".m4a";
    private static readonly string[] FileNames = [VideoFileName, SystemAudioFileName, MicrophoneFileName];

    // The lowest bitrates the Windows AAC encoder offers; transparent for meeting audio.
    private const int StereoAacBitrate = 128_000;
    private const int MonoAacBitrate = 96_000;

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
            window.IsCursorCaptureEnabled = _settings.CaptureMouseCursor;
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
        if (_settings.CompressAudio)
        {
            // One after the other: converting both at once measured slower.
            string?[] compressErrors = await Task.Run(() => ((string[])[SystemAudioFileName, MicrophoneFileName])
                .Select(name => CompressAudio(FolderPath, name)).ToArray());
            foreach (string? compressError in compressErrors.Where(e => e != null))
                Warning?.Invoke(compressError!);
        }
        if (FinalizeFiles(FolderPath) is { } renameError)
            Warning?.Invoke(renameError);
        if (error != null && !Directory.EnumerateFileSystemEntries(FolderPath).Any())
            Directory.Delete(FolderPath);
        Finished?.Invoke(this, error);
    }

    /// <summary>
    /// The name a file has while it is being written, so it can't be opened by mistake before it is complete.
    /// </summary>
    public static string TempFileName(string fileName) => fileName + ".tmp";

    private static string CompressedName(string wavName) => Path.ChangeExtension(wavName, CompressedAudioExtension);

    /// <summary>
    /// Converts one finished WAV in the folder to AAC, then deletes the WAV. A WAV that can't be converted is kept.
    /// Returns the problem as a warning message, or null.
    /// </summary>
    private static string? CompressAudio(string folder, string wavName)
    {
        string wav = Path.Combine(folder, TempFileName(wavName));
        string target = Path.Combine(folder, CompressedName(wavName));
        string temp = TempFileName(target);
        if (!File.Exists(wav))
            return null;
        try
        {
            using (var reader = new WaveFileReader(wav))
            {
                if (reader.Length == 0)
                    return null;
                // The stream overload writes MP4 regardless of the file name; the path overload goes by the extension.
                using var output = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                MediaFoundationEncoder.EncodeToAac(reader, output,
                    reader.WaveFormat.Channels == 1 ? MonoAacBitrate : StereoAacBitrate);
            }
            File.Move(temp, target);
            File.Delete(wav);
            return null;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return $"{wavName} could not be compressed and was kept as WAV: {ex.Message}";
        }
    }

    /// <summary>
    /// Gives every file in a recording folder its final name. Safe to call again, e.g. after the worker was killed
    /// (a fragmented MP4 cut short is still playable; a half-written .m4a is dropped and its WAV kept).
    /// Returns an error message, or null.
    /// </summary>
    public static string? FinalizeFiles(string folder)
    {
        var errors = new List<string>();
        foreach (string name in FileNames)
        {
            string temp = Path.Combine(folder, TempFileName(name));
            if (name != VideoFileName)
                TryDelete(Path.Combine(folder, TempFileName(CompressedName(name))));
            if (!File.Exists(temp))
                continue;
            try
            {
                // The compressed copy is complete once it has its final name; only deleting the WAV was cut short.
                if (name != VideoFileName && File.Exists(Path.Combine(folder, CompressedName(name))))
                    File.Delete(temp);
                else
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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
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
