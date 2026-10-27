using NAudio.CoreAudioApi;

namespace MeetingRecorder;

/// <summary>
/// The separate audio files of one recording, the same in every kind of recording:
/// <list type="bullet">
/// <item>audio-system.wav: every app's sound, whatever speaker or headset it plays on: each active output device is
/// recorded (device loopback) into a part file of its own (audio-system.wav, audio-system-2.wav, ...), devices that
/// appear later are found every few seconds, and <see cref="RecordingFinisher"/> mixes the parts into audio-system.
/// Process loopback isn't used: measured during a Teams call, it records none of the call's sound;</item>
/// <item>audio-mic.wav: the microphone in use (<see cref="AudioDevices.ChooseMicrophone"/>), followed when it changes,
/// unless one is chosen in the menu.</item>
/// </list>
/// Every choice, switch and device change goes to the journal and to <see cref="Log"/>.
/// </summary>
/// <remarks>Create, start and stop it on the UI thread; events are raised there.</remarks>
sealed class RecordingAudio
{
    public const string SystemFileName = "audio-system.wav";
    public const string MicrophoneFileName = "audio-mic.wav";
    private const int MaxSystemParts = 8;

    private const int CheckMilliseconds = 2000;

    private readonly SynchronizationContext _ui;
    private readonly AppSettings _settings;
    private readonly string _folder;
    private readonly AppProcess? _app;
    private readonly RecordingJournal _journal;
    private readonly List<AudioTrack> _tracks = [];
    private readonly List<MMDevice> _devices = [];
    private readonly HashSet<AudioTrack> _reopening = [];
    // The output device each system audio part records, and its name.
    private readonly Dictionary<AudioTrack, (string DeviceId, string Name)> _loopbacks = [];
    private long _start;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = CheckMilliseconds };
    private AudioDeviceWatcher? _watcher;
    private AudioTrack? _microphone;
    private string? _microphoneId;
    private string? _pendingMicrophoneId;
    private bool _followMicrophone;
    private bool _stopping;

    /// <summary>Something the user must act on, e.g. a source that can't be recorded.</summary>
    public event Action<string>? Warning;

    /// <summary>A line for the audio log: what is recorded from where, every switch and device change.</summary>
    public event Action<string>? Log;

    /// <summary>The files written, by their names once saved.</summary>
    public IEnumerable<string> FileNamesUsed => _tracks.Select(t => t.FileName);

    public RecordingAudio(SynchronizationContext ui, AppSettings settings, string folder, AppProcess? app, RecordingJournal journal)
    {
        _ui = ui;
        _settings = settings;
        _folder = folder;
        _app = app;
        _journal = journal;
    }

    public void Start()
    {
        try
        {
            _watcher = new AudioDeviceWatcher();
            _watcher.Changed += text =>
            {
                Record(null, JournalEntry.Event, text);
                // A device that comes or goes can be the one to record now.
                if (_followMicrophone)
                    CheckMicrophone(sourceLost: _microphoneId == null);
            };
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            Log?.Invoke($"Device changes are not watched: {ex.Message}");
        }
        Record(null, JournalEntry.Event, $"Audio devices at the start:\n{AudioDevices.Describe()}");

        var loopbacks = new List<AudioTrack>();
        if (_settings.SystemAudioDeviceId != AppSettings.DisabledDevice)
        {
            loopbacks.AddRange(NewSystemParts());
            if (loopbacks.Count == 0)
                Warning?.Invoke("System audio is not being recorded: no output device is active. It is recorded once one is.");
        }
        if (_settings.MicrophoneDeviceId != AppSettings.DisabledDevice)
            _microphone = new AudioTrack("mic", "Microphone", _folder, MicrophoneFileName, 1);

        // One start time for every file, so a single offset lines them all up with a video.
        _start = RecordingJournal.Now();
        foreach (var track in loopbacks.Append(_microphone).OfType<AudioTrack>())
            AddTrack(track);
        foreach (var track in loopbacks)
        {
            Record(track, JournalEntry.TrackStart, SystemSource(track), _start);
            OpenLoopback(track);
        }
        if (_microphone != null)
            StartMicrophone(_start);
        _timer.Tick += (_, _) => OnTimer();
        _timer.Start();
    }

    private void AddTrack(AudioTrack track)
    {
        track.Start(_start);
        track.SourceFailed += (failed, error) => _ui.Post(_ => OnSourceFailed(failed, error), null);
        _tracks.Add(track);
    }

    /// <summary>System audio part files for the active output devices that aren't recorded yet; the first is audio-system.wav.</summary>
    private List<AudioTrack> NewSystemParts()
    {
        var parts = new List<AudioTrack>();
        List<(string Id, string Name)> devices;
        try
        {
            devices = AudioDevices.ActiveOutputs();
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Devices can be mid-change; the next check decides.
            return parts;
        }
        foreach (var (id, name) in devices.Where(d => !_loopbacks.Values.Any(l => l.DeviceId == d.Id)))
        {
            if (_loopbacks.Count >= MaxSystemParts)
                break;
            string file = _loopbacks.Count == 0 ? SystemFileName : $"{Path.GetFileNameWithoutExtension(SystemFileName)}-{_loopbacks.Count + 1}.wav";
            var track = new AudioTrack("system", "System audio", _folder, file, 2);
            _loopbacks[track] = (id, name);
            parts.Add(track);
        }
        return parts;
    }

    private string SystemSource(AudioTrack track)
    {
        string name = _loopbacks[track].Name;
        return track.FileName == SystemFileName
            ? $"everything played on {name}"
            : $"everything played on {name}, another output device (a part of audio-system)";
    }

    /// <summary>Starts recording the output devices that became active since the last check.</summary>
    private void AddSystemParts()
    {
        foreach (var track in NewSystemParts())
        {
            AddTrack(track);
            Record(track, JournalEntry.TrackStart, SystemSource(track));
            OpenLoopback(track);
        }
    }

    /// <summary>
    /// Opens a system audio part's device loopback. A device that isn't active (unplugged) is retried on each check
    /// without a warning: devices come and go, and the other parts record what plays meanwhile.
    /// </summary>
    private void OpenLoopback(AudioTrack track)
    {
        (string id, string name) = _loopbacks[track];
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (AudioDevices.Active(enumerator, id) is not { } device)
            {
                _reopening.Add(track);
                return;
            }
            _devices.Add(device);
            track.Use(track.OpenLoopback(device), $"everything played on {name}");
            if (_reopening.Remove(track))
                Record(track, JournalEntry.TrackSource, $"everything played on {name} (recording again)");
        }
        catch (Exception ex)
        {
            if (_reopening.Add(track))
            {
                Record(track, JournalEntry.Event, $"{track.Label} on {name} can't be recorded: {ex.Message}. Retrying every {CheckMilliseconds / 1000} s.");
                Warning?.Invoke($"{track.Label} on {name} is not being recorded: {ex.Message}");
            }
        }
    }

    private void StartMicrophone(long start)
    {
        string id = _settings.MicrophoneDeviceId;
        if (id != AppSettings.DefaultDevice)
        {
            using var enumerator = new MMDeviceEnumerator();
            if (AudioDevices.Active(enumerator, id) is { } chosen)
            {
                string name = chosen.FriendlyName;
                chosen.Dispose();
                if (UseMicrophone(new MicrophoneChoice(id, name, "chosen in the tray menu"), start))
                    return;
            }
            Warning?.Invoke("Microphone: the one chosen in the menu is not available, recording the one in use instead.");
        }
        _followMicrophone = true;
        if (AudioDevices.ChooseMicrophone(_app, null) is not { } choice || !UseMicrophone(choice, start))
        {
            Record(_microphone!, JournalEntry.TrackStart, "nothing yet: no microphone could be opened", start);
            Warning?.Invoke("Microphone is not being recorded: no microphone could be opened. It is recorded once one can be.");
        }
    }

    private void OnSourceFailed(AudioTrack track, Exception error)
    {
        if (_stopping)
            return;
        Record(track, JournalEntry.Event, $"{track.Label} source stopped: {error.Message}");
        if (track == _microphone)
        {
            _microphoneId = null;
            // A microphone chosen in the menu that disappears is replaced by the one in use, rather than nothing.
            _followMicrophone = true;
            CheckMicrophone(sourceLost: true);
        }
        else
        {
            // An output device unplugged: recorded again if it comes back.
            _reopening.Add(track);
        }
    }

    private void OnTimer()
    {
        if (_stopping)
            return;
        foreach (var track in _reopening.ToList())
            OpenLoopback(track);
        if (_settings.SystemAudioDeviceId != AppSettings.DisabledDevice)
            AddSystemParts();
        if (_followMicrophone)
            CheckMicrophone(sourceLost: _microphoneId == null);
    }

    /// <summary>
    /// Switches the microphone when another one should be recorded. A change must be seen on two checks in a row, so a
    /// meeting app that reopens its microphone doesn't cause a switch back and forth; a lost one is replaced at once.
    /// </summary>
    private void CheckMicrophone(bool sourceLost)
    {
        MicrophoneChoice? choice;
        try
        {
            choice = AudioDevices.ChooseMicrophone(_app, _microphoneId);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Devices can be mid-change; the next check decides.
            return;
        }
        if (choice == null || choice.DeviceId == _microphoneId)
        {
            _pendingMicrophoneId = null;
            return;
        }
        if (!sourceLost && _pendingMicrophoneId != choice.DeviceId)
        {
            _pendingMicrophoneId = choice.DeviceId;
            return;
        }
        _pendingMicrophoneId = null;
        UseMicrophone(choice, startedAt: null);
    }

    /// <summary>
    /// Records from this microphone from now on. <paramref name="startedAt"/> is set for the track's first source: the
    /// journal then gets the track's start.
    /// </summary>
    private bool UseMicrophone(MicrophoneChoice choice, long? startedAt)
    {
        var track = _microphone!;
        string level = AudioDevices.DescribeLevel(choice.DeviceId);
        string source = $"{choice.DeviceName} ({choice.Reason}{(level.Length > 0 ? "; " + level : "")})";
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(choice.DeviceId);
            _devices.Add(device);
            track.Use(track.OpenDevice(device), source);
            _microphoneId = choice.DeviceId;
            string text = source + $". Microphones in use: {AudioDevices.DescribeMicrophoneUse()}";
            if (startedAt is { } start)
                Record(track, JournalEntry.TrackStart, text, start);
            else
                Record(track, JournalEntry.TrackSource, text);
            return true;
        }
        catch (Exception ex)
        {
            Record(track, JournalEntry.Event, $"Microphone {choice.DeviceName} can't be recorded: {ex.Message}");
            return false;
        }
    }

    private void Record(AudioTrack? track, string kind, string text, long? qpc = null)
    {
        var entry = new JournalEntry { Kind = kind, Track = track?.Kind, File = track?.FileName, Text = text };
        _journal.Write(qpc is { } at ? entry with { Qpc = at } : entry);
        string prefix = kind switch
        {
            JournalEntry.TrackStart => $"{track!.Label} ({track.FileName}): recording ",
            JournalEntry.TrackSource => $"{track!.Label}: now recording ",
            _ => "",
        };
        Log?.Invoke(prefix + text);
    }

    /// <summary>Stops every track; completes once the WAV files are closed.</summary>
    public async Task StopAsync()
    {
        if (_stopping)
            return;
        _stopping = true;
        _timer.Dispose();
        _watcher?.Dispose();
        await Task.WhenAll(_tracks.Select(t => t.StopAsync()));
        foreach (var track in _tracks)
        {
            var (sound, peak, minutes) = track.Statistics();
            _journal.Write(new JournalEntry
            {
                Kind = JournalEntry.TrackEnd, Track = track.Kind, File = track.FileName,
                SoundSeconds = Math.Round(sound, 1), PeakDb = peak, MinutePeaks = minutes,
            });
            Log?.Invoke($"{track.Label} ({track.FileName}) ended: sound in {sound:0} s, loudest {peak:0.0} dBFS.");
        }
        foreach (var device in _devices)
            device.Dispose();
    }
}
