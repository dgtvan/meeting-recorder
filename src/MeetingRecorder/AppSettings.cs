using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingRecorder;

/// <summary>
/// User choices persisted between runs in settings.json in the data folder.
/// </summary>
sealed class AppSettings
{
    /// <summary>Audio device id meaning "choose automatically".</summary>
    public const string DefaultDevice = "";

    /// <summary>Audio device id meaning "don't record this source".</summary>
    public const string DisabledDevice = "off";

    private static readonly string DefaultRecordingsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Meeting Recordings");

    /// <summary>Version of the settings layout; <see cref="AppInitializer"/> migrates older files once.</summary>
    public int SchemaVersion { get; set; }

    public string RecordingsRoot
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? DefaultRecordingsRoot : Path.GetFullPath(value);
    } = DefaultRecordingsRoot;

    /// <summary>
    /// <see cref="DisabledDevice"/> turns the system audio off; anything else records every app's
    /// sound on any output device. Named after the device it once chose.
    /// </summary>
    public string SystemAudioDeviceId { get; set; } = DefaultDevice;

    /// <summary>
    /// The microphone to record: <see cref="DefaultDevice"/> picks the one in use and follows it
    /// (<see cref="AudioDevices.ChooseMicrophone"/>); a device id records that one while it's connected.
    /// </summary>
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

    /// <summary>
    /// Auto-record turned on or off per meeting app, by <see cref="IMeetingApp.Id"/>; an app not listed is on.
    /// Auto-record records a call's windows and audio when the call starts and stops when it ends.
    /// </summary>
    public Dictionary<string, bool> AutoRecord { get; set; } = [];

    /// <summary>Schema 1's Teams-only switch; <see cref="AppInitializer"/> moves it to <see cref="AutoRecord"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? AutoRecordTeams { get; set; }

    public bool IsAutoRecordOn(IMeetingApp app) => !AutoRecord.TryGetValue(app.Id, out bool on) || on;

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
            if (File.Exists(AppPaths.SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsPath)) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            error = $"Settings could not be read ({AppPaths.SettingsPath}): {ex.Message}. Using defaults; the file was left as it is.";
        }
        return new AppSettings();
    }

    /// <summary>Writes the settings file. Throws on I/O failure.</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SettingsPath)!);
        File.WriteAllText(AppPaths.SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
