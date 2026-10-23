using System.Text.Json;

namespace MeetingRecorder;

/// <summary>
/// User choices persisted between runs in %APPDATA%\MeetingRecorder\settings.json.
/// </summary>
sealed class AppSettings
{
    /// <summary>Audio device id meaning "follow the Windows default device".</summary>
    public const string DefaultDevice = "";

    /// <summary>Audio device id meaning "don't record this source".</summary>
    public const string DisabledDevice = "off";

    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MeetingRecorder", "settings.json");

    private static readonly string DefaultRecordingsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Meeting Recordings");

    /// <summary>Version of the settings layout; <see cref="AppInitializer"/> migrates older files once.</summary>
    public int SchemaVersion { get; set; }

    public string RecordingsRoot
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? DefaultRecordingsRoot : Path.GetFullPath(value);
    } = DefaultRecordingsRoot;

    public string SystemAudioDeviceId { get; set; } = DefaultDevice;

    public string MicrophoneDeviceId { get; set; } = DefaultDevice;

    public int VideoFramerate { get; set; } = 15;

    /// <summary>H.264 quality from 1 to 100; lower means smaller files.</summary>
    public int VideoQuality { get; set; } = 60;

    /// <summary>
    /// Video taller than this many pixels is scaled down (keeping its aspect ratio) before encoding; 0 = never.
    /// </summary>
    public int MaxVideoHeight { get; set; } = 1080;

    /// <summary>Convert the audio to AAC (.m4a) once a recording stops; false keeps the WAV files.</summary>
    public bool CompressAudio { get; set; } = true;

    public bool CaptureMouseCursor { get; set; } = true;

    /// <summary>Use the GPU's H.264 encoder when available; falls back to software encoding otherwise.</summary>
    public bool UseHardwareEncoder { get; set; } = true;

    /// <summary>Start recording the meeting window when a Teams call or meeting starts, and stop when it ends.</summary>
    public bool AutoRecordTeams { get; set; } = true;

    public static AppSettings Load() => Load(out _);

    /// <summary>
    /// Loads the settings, or defaults when the file is missing. <paramref name="error"/> is set when the file
    /// exists but can't be read, in which case it must not be overwritten.
    /// </summary>
    public static AppSettings Load(out string? error)
    {
        error = null;
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"Settings could not be read ({SettingsPath}): {ex.Message}. Using defaults; the file was left as it is.";
        }
        return new AppSettings();
    }

    /// <summary>Writes the settings file. Throws on I/O failure.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
