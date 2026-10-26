using NAudio.CoreAudioApi;

namespace MeetingRecorder;

/// <summary>
/// The separate audio files of one recording, the same in every kind of recording:
/// <list type="bullet">
/// <item>audio-system.wav: every app's sound, whatever speaker or headset it plays on (process loopback of every
/// process but this recorder), so a device change can't lose it;</item>
/// <item>audio-app.wav: only the sound of the program that owns the recorded window and every process it started, when
/// there is such a program (not for a display). Windows records a process with its direct children only, so the
/// processes playing deeper down get a part file each (audio-app-2.wav, ...), found every few seconds, which
/// <see cref="RecordingFinisher"/> mixes into audio-app;</item>
/// <item>audio-mic.wav: the microphone in use (<see cref="AudioDevices.ChooseMicrophone"/>), followed when it changes,
/// unless one is chosen in the menu.</item>
/// </list>
/// Every choice, switch and device change goes to the journal and to <see cref="Log"/>.
/// </summary>
/// <remarks>Create, start and stop it on the UI thread; events are raised there.</remarks>
sealed class RecordingAudio
{
    public const string SystemFileName = "audio-system.wav";
    public const string AppFileName = "audio-app.wav";
    public const string MicrophoneFileName = "audio-mic.wav";
    public static readonly string[] FileNames = [SystemFileName, AppFileName, MicrophoneFileName];
    private const int MaxAppParts = 8;

    private const int CheckMilliseconds = 2000;

    private readonly SynchronizationContext _ui;
    private readonly AppSettings _settings;
    private readonly string _folder;
    private readonly AppProcess? _app;
    private readonly RecordingJournal _journal;
    private readonly List<AudioTrack> _tracks = [];
    private readonly List<MMDevice> _devices = [];
    private readonly HashSet<AudioTrack> _reopening = [];
    private readonly HashSet<AudioTrack> _opening = [];
    // What each loopback track records: the process, include or exclude its tree, and the description.
    private readonly Dictionary<AudioTrack, (uint Pid, ProcessLoopbackMode Mode, string Source)> _loopbacks = [];
    private readonly List<uint> _appRoots = [];
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

    public async Task StartAsync()
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
            var system = new AudioTrack("system", "System audio", _folder, SystemFileName, 2);
            _loopbacks[system] = ((uint)Environment.ProcessId, ProcessLoopbackMode.ExcludeTargetProcessTree, "every app, on any output device");
            loopbacks.Add(system);
            if (_app != null)
                loopbacks.AddRange(NewAppParts());
            else
                Log?.Invoke("App audio: not recorded, no single program owns what is recorded.");
        }
        if (_settings.MicrophoneDeviceId != AppSettings.DisabledDevice)
            _microphone = new AudioTrack("mic", "Microphone", _folder, MicrophoneFileName, 1);

        // One start time for every file, so a single offset lines them all up with a video.
        _start = RecordingJournal.Now();
        foreach (var track in loopbacks.Append(_microphone).OfType<AudioTrack>())
            AddTrack(track);
        foreach (var track in loopbacks)
        {
            Record(track, JournalEntry.TrackStart, _loopbacks[track].Source, _start);
            await OpenLoopback(track);
            if (_stopping)
                return;
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

    /// <summary>
    /// App audio part files for the processes of the app that play sound and aren't recorded yet; the first is the app
    /// itself (audio-app.wav).
    /// </summary>
    private List<AudioTrack> NewAppParts()
    {
        var parts = new List<AudioTrack>();
        List<uint> roots;
        try
        {
            roots = ProcessTree.LoopbackRoots(_app!.Id, AudioDevices.PlayingProcesses(), ProcessTree.Parents());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Devices can be mid-change; the next check decides.
            roots = _appRoots.Count == 0 ? [_app!.Id] : [];
        }
        foreach (uint root in roots.Where(r => !_appRoots.Contains(r)))
        {
            if (_appRoots.Count >= MaxAppParts)
                break;
            string file = _appRoots.Count == 0 ? AppFileName : $"{Path.GetFileNameWithoutExtension(AppFileName)}-{_appRoots.Count + 1}.wav";
            var process = ProcessTree.Describe(root);
            string source = _appRoots.Count == 0
                ? $"only {_app} and the processes it started, on any output device"
                : $"{process?.ToString() ?? $"pid {root}"}, a process of {_app} that plays sound, and its child processes (a part of audio-app)";
            _appRoots.Add(root);
            var track = new AudioTrack("app", "App audio", _folder, file, 2);
            _loopbacks[track] = (root, ProcessLoopbackMode.IncludeTargetProcessTree, source);
            parts.Add(track);
        }
        return parts;
    }

    /// <summary>Starts recording the app's processes that started playing sound since the last check.</summary>
    private async Task AddAppParts()
    {
        foreach (var track in NewAppParts())
        {
            if (_stopping)
                return;
            AddTrack(track);
            Record(track, JournalEntry.TrackStart, _loopbacks[track].Source);
            await OpenLoopback(track);
            if (_stopping)
                return;
        }
    }

    /// <summary>Opens a loopback track's process capture; on failure it is retried on each check.</summary>
    private async Task OpenLoopback(AudioTrack track)
    {
        if (!_opening.Add(track))
            return;
        (uint pid, ProcessLoopbackMode mode, string source) = _loopbacks[track];
        try
        {
            var capture = await track.OpenProcessLoopbackAsync(pid, mode);
            if (_stopping)
            {
                capture.Dispose();
                return;
            }
            track.Use(capture, source);
            if (_reopening.Remove(track))
                Record(track, JournalEntry.TrackSource, source + " (recording again)");
        }
        catch (Exception ex)
        {
            if (_reopening.Add(track))
            {
                Record(track, JournalEntry.Event, $"{track.Label} can't be recorded: {ex.Message}. Retrying every {CheckMilliseconds / 1000} s.");
                Warning?.Invoke($"{track.Label} is not being recorded: {ex.Message}");
            }
        }
        finally
        {
            _opening.Remove(track);
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
            _reopening.Add(track);
            Warning?.Invoke($"{track.Label} stopped recording: {error.Message}. Retrying.");
        }
    }

    private void OnTimer()
    {
        if (_stopping)
            return;
        foreach (var track in _reopening.ToList())
            _ = OpenLoopback(track);
        if (_app != null && _loopbacks.Count > 0)
            _ = AddAppParts();
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
