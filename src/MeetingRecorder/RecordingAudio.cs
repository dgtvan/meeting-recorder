using NAudio.CoreAudioApi;
using NAudio.Wave;

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
    // How often each file's place on the recording's clock goes to the audio log while recording.
    private const int AlignmentLogSeconds = 60;
    // A file this far from the recording's clock is worth telling the user about: the files are then out of step with
    // each other by enough to hear.
    private const double OutOfStepSeconds = 0.25;
    // Windows' meter for an output device counts as playing from here up, and a part file as silent from here down:
    // the gap between the two is what tells a device that plays nothing from one whose sound the loopback misses.
    private const double DevicePlayingDb = -60;
    private const double PartSilentDb = -80;
    /// <summary>
    /// Below this, Windows' meter saw no sound at all on the device over a minute, rather than quiet sound: a system
    /// audio file that stayed silent while every device read this low recorded the silence that was really played, so
    /// nothing was lost (an in-room meeting plays nothing on the PC).
    /// </summary>
    public const double DeviceSilentDb = -90;
    // Minutes of a device playing into a silent part file before the user is told; one minute can be a device that
    // started to play as the minute ended.
    private const int SilentPartMinutes = 2;
    // Checks in a row with Windows' meter saying the device plays while the part file records nothing, before a fresh
    // loopback is opened: long enough that a quiet moment in a call can't trigger it, short enough to lose a few
    // seconds of the call rather than the rest of the meeting.
    private const int DeadChecks = 3;
    // Fresh loopbacks in a row without the part file hearing anything again. Past this the stream isn't the problem:
    // the sound reaches the device by a path loopback can't hear, which the per-minute report warns about. The count
    // starts over as soon as the file hears the device again, so a device that hiccups repeatedly is always recovered.
    private const int MaxReopens = 5;

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
    // Windows' own meters for those devices, read on every check.
    private readonly Dictionary<AudioTrack, DeviceMeter> _meters = [];
    private long _start;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = CheckMilliseconds };
    private AudioDeviceWatcher? _watcher;
    private AudioTrack? _microphone;
    private string? _microphoneId;
    private string? _pendingMicrophoneId;
    private bool _followMicrophone;
    private bool _stopping;
    private int _ticks;

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
        Record(null, JournalEntry.Event, "Audio devices at the start:", AudioDevices.Describe());

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
            Record(track, JournalEntry.TrackStart, SystemSource(track), qpc: _start);
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
            var capture = track.OpenLoopback(device);
            track.Use(capture, $"everything played on {name}");
            if (_meters.TryGetValue(track, out var meter))
                meter.Use(device);
            else
                _meters[track] = new DeviceMeter(device);
            Log?.Invoke($"{track.Label} ({track.FileName}): {name} plays {Describe(DeviceFormat(device))}, recorded as "
                + Describe(capture.WaveFormat));
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
            Record(_microphone!, JournalEntry.TrackStart, "nothing yet: no microphone could be opened", qpc: start);
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
        foreach (var meter in _meters.Values)
            meter.Sample();
        CheckLoopbacksAlive();
        if (++_ticks % (AlignmentLogSeconds * 1000 / CheckMilliseconds) == 0)
            LogAlignment();
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
        var details = new List<string> { $"Chosen because: {choice.Reason}" };
        if (AudioDevices.DescribeLevel(choice.DeviceId) is { Length: > 0 } level)
            details.Add($"Level: {level}");
        var inUse = AudioDevices.DescribeMicrophoneUse();
        details.AddRange(inUse.Count == 0 ? ["Microphones in use: none"] : inUse.Select(u => $"Microphones in use: {u}"));
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var device = enumerator.GetDevice(choice.DeviceId);
            _devices.Add(device);
            track.Use(track.OpenDevice(device), choice.DeviceName);
            _microphoneId = choice.DeviceId;
            if (startedAt is { } start)
                Record(track, JournalEntry.TrackStart, choice.DeviceName, details, start);
            else
                Record(track, JournalEntry.TrackSource, choice.DeviceName, details);
            return true;
        }
        catch (Exception ex)
        {
            Record(track, JournalEntry.Event, $"Microphone {choice.DeviceName} can't be recorded: {ex.Message}");
            return false;
        }
    }

    private void Record(AudioTrack? track, string kind, string text, List<string>? details = null, long? qpc = null)
    {
        var entry = new JournalEntry { Kind = kind, Track = track?.Kind, File = track?.FileName, Text = text, Details = details };
        _journal.Write(qpc is { } at ? entry with { Qpc = at } : entry);
        string prefix = kind switch
        {
            JournalEntry.TrackStart => $"{track!.Label} ({track.FileName}): recording ",
            JournalEntry.TrackSource => $"{track!.Label}: now recording ",
            _ => "",
        };
        Log?.Invoke(prefix + text + (details is { Count: > 0 } ? Environment.NewLine + AudioDevices.Indent(details) : ""));
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
            var alignment = track.Alignment();
            // Why a silent file is silent only shows next to Windows' own meters, so the report travels with it.
            var playback = _meters.TryGetValue(track, out var tracked) ? tracked.Report(_loopbacks[track].Name) : null;
            string? played = playback is { Minutes: > 0 } ? playback.Describe() : null;
            // A clean file needs no alignment bullets in README.md, but why it is silent still belongs there.
            List<string> details = alignment.Clean ? [] : alignment.Describe();
            if (played != null)
                details.Add(played);
            var logLines = alignment.Describe();
            if (played != null)
                logLines.Add(played);
            _journal.Write(new JournalEntry
            {
                Kind = JournalEntry.TrackEnd, Track = track.Kind, File = track.FileName,
                SoundSeconds = Math.Round(sound, 1), PeakDb = peak, MinutePeaks = minutes,
                Alignment = alignment, Playback = playback, Details = details.Count > 0 ? details : null,
            });
            Log?.Invoke($"{track.Label} ({track.FileName}) ended: sound in {sound:0} s, loudest {peak:0.0} dBFS, "
                + $"{alignment.RecordedSeconds:0.##} s of audio in {alignment.Packets} packets."
                + Environment.NewLine + AudioDevices.Indent(logLines));
            // The sound of the recording is only as good as its files lining up; say so rather than leave it to be
            // noticed as an echo in the video's sound.
            if (Math.Abs(alignment.OffsetSeconds) > OutOfStepSeconds)
                Warning?.Invoke($"{track.Label} ({track.FileName}) ended {alignment.OffsetSeconds:+0.##;-0.##} s from the "
                    + "recording's clock, so its sound may be out of step with the other files. The recording's README.md "
                    + "and audio.log say what happened.");
        }
        foreach (var device in _devices)
            device.Dispose();
    }

    /// <summary>
    /// Writes where each file sits on the recording's clock to the audio log, so a recording whose sound turns out to
    /// be out of step shows when a file started to move and what was done about it, not just the totals at the end.
    /// </summary>
    private void LogAlignment()
    {
        foreach (var track in _tracks)
        {
            var lines = track.Alignment().Describe().ToList();
            if (_meters.TryGetValue(track, out var meter))
                lines.AddRange(CheckDevicePlayed(track, meter));
            Log?.Invoke($"{track.Label} ({track.FileName}) so far:" + Environment.NewLine + AudioDevices.Indent(lines));
        }
    }

    /// <summary>
    /// Checks on every tick that each system audio part still carries what its output device plays, and opens a fresh
    /// loopback on the same device when it doesn't. A loopback stream can stop carrying a device's sound without
    /// failing: the device is reconfigured underneath it (a headset's own software engaging for a call, a format
    /// change), and the stream then delivers silence, or stops delivering at all, while the device plays on. Nothing
    /// is raised for it, so only Windows' own meter for the device can tell that apart from a device that plays
    /// nothing. Measured on a meeting watched in a browser tab: three seconds of the call were recorded and the rest
    /// was silence, the stream having stopped delivering altogether a third of the way in.
    /// </summary>
    private void CheckLoopbacksAlive()
    {
        foreach (var track in _loopbacks.Keys.ToList())
        {
            if (!_meters.TryGetValue(track, out var meter))
                continue;
            var (delivered, peak) = track.Delivered();
            // Nothing to judge while the stream is down and already being retried on every check.
            if (_reopening.Contains(track))
            {
                meter.Strikes = 0;
                continue;
            }
            // A fresh stream that carries the device's sound again has done its job, and the next failure gets its
            // own attempts; a device that falls quiet proves nothing either way, so it leaves the count alone.
            bool heard = delivered > 0 && peak > PartSilentDb;
            if (heard)
                meter.Reopens = 0;
            // A device that plays nothing delivers nothing: only sound the file misses means a dead stream.
            if (heard || meter.LastDb < DevicePlayingDb)
            {
                meter.Strikes = 0;
                continue;
            }
            if (++meter.Strikes < DeadChecks)
                continue;
            meter.Strikes = 0;
            if (meter.Reopens >= MaxReopens)
                continue;
            meter.Reopens++;
            meter.ReopenCount++;
            string name = _loopbacks[track].Name;
            string missing = delivered == 0 ? "has stopped delivering audio" : $"delivers silence ({peak:0.0} dBFS)";
            Record(track, JournalEntry.Event, $"{name} plays sound (Windows' own meter reads {meter.LastDb:0.0} dBFS) "
                + $"that {track.FileName} {missing}: recording it again from a fresh loopback of the same device "
                + $"(attempt {meter.ReopenCount}).");
            OpenLoopback(track);
        }
    }

    /// <summary>
    /// Compares what Windows says the device played over the last minute with what the part file recorded, and warns
    /// while the recording still runs when the device plays and the file stays silent: the sound then reaches the
    /// device by a path loopback can't hear, and the call's other side is being lost. Lines for the audio log.
    /// </summary>
    private List<string> CheckDevicePlayed(AudioTrack track, DeviceMeter meter)
    {
        string name = _loopbacks[track].Name;
        var played = meter.TakeMinute();
        double recorded = track.PeakSince();
        if (played is not { } sound)
            return [$"Windows' meter for {name} could not be read this minute; this file recorded {recorded:0.0} dBFS"];

        string app = sound.App == null ? "no app's sound" : $"loudest app {sound.App} at {AudioDevices.Decibels(sound.AppPeak):0.0} dBFS";
        double device = AudioDevices.Decibels(sound.Device);
        meter.Count(device, AudioDevices.Decibels(sound.AppPeak), playing: device >= DevicePlayingDb, unheard: device >= DevicePlayingDb && recorded <= PartSilentDb);
        var lines = new List<string> { $"Windows' meter for {name} this minute: {device:0.0} dBFS, {app}; this file recorded {recorded:0.0} dBFS" };
        if (device < DevicePlayingDb || recorded > PartSilentDb)
        {
            meter.SilentMinutes = 0;
            return lines;
        }
        meter.SilentMinutes++;
        lines.Add($"{name} is playing but this file records silence ({meter.SilentMinutes} minute(s) so far)");
        // A stream that stopped carrying the device's sound has already been given fresh loopbacks by
        // CheckLoopbacksAlive, seconds after it went quiet. A minute of silence after that is the other kind of
        // failure, one a new stream can't fix, and it is the user's to act on.
        if (meter.SilentMinutes >= SilentPartMinutes && !meter.Warned)
        {
            meter.Warned = true;
            string again = meter.ReopenCount > 0
                ? $", even after opening its loopback again {meter.ReopenCount} time(s)"
                : "";
            Record(track, JournalEntry.Event, $"{name} plays sound that the recording can't hear{again}: Windows' meter "
                + $"reads {device:0.0} dBFS ({app}) while {track.FileName} records silence. The sound reaches the device "
                + "by a path this loopback can't hear.");
            Warning?.Invoke($"{track.Label} is missing what {name} plays: Windows says it is playing ({device:0.0} dBFS) "
                + $"but the recording hears silence{again}, so the other participants are not being recorded. Switch "
                + "the meeting app's speaker to another device to record them.");
        }
        return lines;
    }

    private static WaveFormat? DeviceFormat(MMDevice device)
    {
        try
        {
            using var client = device.CreateAudioClient();
            return client.MixFormat;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Describe(WaveFormat? format) => format == null
        ? "an unknown format"
        : $"{format.SampleRate} Hz, {format.BitsPerSample}-bit {format.Encoding}, {format.Channels} ch";

    /// <summary>Windows' own peak meters for one output device, read on every check and reported once a minute.</summary>
    private sealed class DeviceMeter(MMDevice device)
    {
        private MMDevice _device = device;
        private (float Device, AppProcess? App, float AppPeak)? _loudest;

        /// <summary>Minutes in a row the device played while the part file recorded silence.</summary>
        public int SilentMinutes { get; set; }

        /// <summary>Checks in a row with the device playing while the part file recorded nothing.</summary>
        public int Strikes { get; set; }

        /// <summary>Fresh loopbacks opened since the part file last heard this device; it stops trying past MaxReopens.</summary>
        public int Reopens { get; set; }

        /// <summary>Fresh loopbacks opened over the whole recording, for the messages.</summary>
        public int ReopenCount { get; set; }

        /// <summary>Windows' meter for the device at the last check (dBFS), -100 when it couldn't be read.</summary>
        public double LastDb { get; private set; } = -100;

        /// <summary>Whether the user has been told about this device; once is enough per recording.</summary>
        public bool Warned { get; set; }

        private int _minutes;
        private int _playingMinutes;
        private int _unheardMinutes;
        private double _loudestDb = -100;
        private double _loudestAppDb = -100;

        /// <summary>Reads the meters of this device object from now on, keeping what has been seen so far.</summary>
        public void Use(MMDevice reopened) => _device = reopened;

        /// <summary>Keeps one minute's comparison, for the report the recording ends with.</summary>
        public void Count(double deviceDb, double appDb, bool playing, bool unheard)
        {
            _minutes++;
            if (playing)
                _playingMinutes++;
            if (unheard)
                _unheardMinutes++;
            _loudestDb = Math.Max(_loudestDb, deviceDb);
            _loudestAppDb = Math.Max(_loudestAppDb, appDb);
        }

        /// <summary>What Windows' meters said this device played over the whole recording.</summary>
        public PlaybackReport Report(string name) =>
            new(name, _minutes, _playingMinutes, _unheardMinutes, Math.Round(_loudestDb, 1), Math.Round(_loudestAppDb, 1));

        public void Sample()
        {
            if (AudioDevices.Playing(_device) is not { } now)
            {
                // A reading that can't be taken says nothing about the device, and must not pass for sound played.
                LastDb = -100;
                return;
            }
            LastDb = AudioDevices.Decibels(now.Device);
            if (_loudest is not { } best || now.Device > best.Device)
                _loudest = now;
        }

        /// <summary>The loudest reading since the last call, and starts the next minute.</summary>
        public (float Device, AppProcess? App, float AppPeak)? TakeMinute()
        {
            var loudest = _loudest;
            _loudest = null;
            return loudest;
        }
    }
}
