using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// Records one audio file (16-bit PCM WAV, 48 kHz) from a source that can be replaced while recording, e.g. when the
/// microphone in use changes. The file keeps one timeline: every packet is written where its timestamp puts it on the
/// recording's clock, so the file stays in sync with the other files and with the video. A pause (a source switch,
/// nothing playing) is filled with silence; audio that would land before what is already written is dropped, so the
/// file can never run ahead of the clock and drift away from the other files.
/// </summary>
/// <remarks>
/// WAV costs no encoding CPU while recording and survives a crash; <see cref="RecordingFinisher"/> converts it to AAC.
/// Loopback capture delivers no packets while nothing is playing, which would otherwise shorten the file.
/// Appending every packet instead would let a file drift: a timestamp that runs ahead of the clock (loopback audio
/// can be stamped with the moment it will be played, which moves when the app re-buffers) fills silence that is never
/// taken back, and everything after it sits too late in the file for good. Measured on a Teams call, that put the
/// system audio 0.6 s behind the microphone, and the mixed video sound played the two as an echo.
/// </remarks>
sealed class AudioTrack
{
    public const int SampleRate = 48000;
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    // How far a file may sit from the recording's clock before a packet's timestamp is honoured by filling silence or
    // dropping audio: short enough that the files mix without an audible echo, long enough to ignore packet jitter.
    private const double MaxOffsetSeconds = 0.05;
    // A source that delivers nothing for longer than this has paused (nothing playing, a device switch), rather than
    // being late with a packet; it is all a packet without a timestamp can be judged by.
    private const double MaxPauseSeconds = 0.2;
    private const int BufferMilliseconds = 200;
    // -60 dBFS: quieter than this is background hiss, not sound.
    private const int SoundThreshold = 33;

    private readonly WaveFileWriter _writer;
    private readonly int _channels;
    private readonly int _bytesPerFrame;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private readonly List<int> _minutePeaks = [];
    private Segment? _current;
    private byte[] _scratch = [];
    private long _startTicks;
    private long _framesWritten;
    private long _soundFrames;
    private int _peak;
    private int _peakSince;
    private long _checkFrames;
    private int _checkPeak;
    private long? _stopTicks;
    private bool _closed;
    private readonly AlignmentLog _alignment = new();

    /// <summary>"system" or "mic": which track of the recording this is.</summary>
    public string Kind { get; }

    /// <summary>"System audio" or "Microphone", for messages.</summary>
    public string Label { get; }

    public string FileName { get; }

    /// <summary>What is being recorded now, e.g. a device name.</summary>
    public string? Source { get; private set; }

    /// <summary>Raised, on a capture thread, when the current source stops delivering audio, e.g. a headset unplugged. The file stays open.</summary>
    public event Action<AudioTrack, Exception>? SourceFailed;

    public AudioTrack(string kind, string label, string folder, string fileName, int channels)
    {
        Kind = kind;
        Label = label;
        FileName = fileName;
        _channels = channels;
        _bytesPerFrame = channels * sizeof(short);
        _writer = new WaveFileWriter(Path.Combine(folder, RecordingSession.TempFileName(fileName)), Format(channels));
    }

    /// <summary>The format every source of a track is opened with.</summary>
    public static WaveFormat Format(int channels) => new(SampleRate, 16, channels);

    /// <summary>Opens a capture device (a microphone) in this track's format.</summary>
    public WasapiRecorder OpenDevice(MMDevice device) => new WasapiRecorderBuilder()
        .WithDevice(device)
        .WithBufferLength(BufferMilliseconds)
        .WithFormat(Format(_channels))
        .Build();

    /// <summary>Opens everything an output device plays (loopback) in this track's format.</summary>
    public WasapiRecorder OpenLoopback(MMDevice device) => new WasapiRecorderBuilder()
        .WithDevice(device)
        .WithBufferLength(BufferMilliseconds)
        .WithFormat(Format(_channels))
        .WithLoopbackCapture()
        .Build();

    /// <summary>Sets the time the file starts at, on the <see cref="RecordingJournal.Now"/> clock. Call once, before the first source.</summary>
    public void Start(long ticks) => _startTicks = ticks;

    /// <summary>
    /// Records from <paramref name="capture"/> from now on, replacing the current source. Throws when it can't start;
    /// the current source is then kept.
    /// </summary>
    public void Use(WasapiRecorder capture, string source)
    {
        var segment = new Segment(capture);
        WaveFormat format = capture.WaveFormat;
        segment.ConvertFloat = format.BitsPerSample == 32 && (format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat));
        if (format.SampleRate != SampleRate || format.Channels != _channels || (format.BitsPerSample != 16 && !segment.ConvertFloat))
        {
            capture.Dispose();
            throw new InvalidOperationException($"the device records {format}, not {Format(_channels)}");
        }
        segment.SourceBytesPerFrame = format.BlockAlign;
        capture.DataAvailable += (ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long _, long qpc) => OnData(segment, buffer, flags, qpc);
        capture.RecordingStopped += (_, e) => OnSegmentStopped(segment, e.Exception);

        Segment? previous;
        lock (_sync)
        {
            if (_closed || _stopTicks != null)
            {
                capture.Dispose();
                return;
            }
            capture.StartRecording();
            previous = _current;
            _current = segment;
            Source = source;
        }
        if (previous != null && !previous.Stop())
            previous.Dispose();
    }

    /// <summary>Stops recording; the task completes once the WAV file is finalized.</summary>
    public Task StopAsync()
    {
        Segment? current;
        lock (_sync)
        {
            _stopTicks ??= RecordingJournal.Now();
            current = _current;
        }
        if (current == null || !current.Stop())
            Close();
        return _stopped.Task;
    }

    /// <summary>Seconds that had sound in them, the loudest moment (dBFS) and the loudest moment of each minute.</summary>
    public (double SoundSeconds, double PeakDb, double[] MinutePeaks) Statistics()
    {
        lock (_sync)
            return (_soundFrames / (double)SampleRate, Decibels(_peak), _minutePeaks.Select(Decibels).ToArray());
    }

    /// <summary>
    /// What the source delivered since the last call: frames, counting silence the source itself delivered, and the
    /// loudest moment in them (dBFS). Read on every check to tell a live source from one that has gone silent or
    /// stopped delivering altogether; <see cref="PeakSince"/> is the same question over a minute, for the reports.
    /// </summary>
    public (long Frames, double PeakDb) Delivered()
    {
        lock (_sync)
        {
            var delivered = (_checkFrames, Decibels(_checkPeak));
            (_checkFrames, _checkPeak) = (0, 0);
            return delivered;
        }
    }

    /// <summary>
    /// The loudest moment (dBFS) written since the last call, for the per-minute reports; a file that is recording
    /// silence reads -100 however loud it was before.
    /// </summary>
    public double PeakSince()
    {
        lock (_sync)
        {
            double peak = Decibels(_peakSince);
            _peakSince = 0;
            return peak;
        }
    }

    /// <summary>
    /// How the file sits on the recording's clock and everything that was needed to keep it there, so a recording
    /// whose sound turns out to be out of step can be worked out afterwards from the log alone.
    /// </summary>
    public AlignmentReport Alignment()
    {
        lock (_sync)
            return _alignment.Report(FileName, _framesWritten - FramesAt(_stopTicks ?? RecordingJournal.Now()), Where(_framesWritten));
    }

    /// <summary>Where a frame count falls on the recording's clock, e.g. "00:04:13.2", for a message.</summary>
    private static string Where(long frames) => TimeSpan.FromSeconds(frames / (double)SampleRate).ToString(@"hh\:mm\:ss\.f");

    private static double Decibels(int peak) => peak <= 0 ? -100 : Math.Round(20 * Math.Log10(peak / 32768.0), 1);

    private void OnData(Segment segment, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long qpcPosition)
    {
        lock (_sync)
        {
            // A replaced source can still deliver a packet or two while it stops.
            if (_closed || segment != _current)
                return;

            int frames = buffer.Length / segment.SourceBytesPerFrame;
            // The packet holds audio from before now, so its first frame can be no later than this.
            long latestStart = FramesAt(RecordingJournal.Now()) - frames;
            bool timestamped = qpcPosition > 0 && (flags & AudioClientBufferFlags.TimestampError) == 0;
            _alignment.Packet(timestamped, (flags & AudioClientBufferFlags.DataDiscontinuity) != 0,
                (flags & AudioClientBufferFlags.Silent) != 0, frames);
            _checkFrames += frames;

            long expectedStart;
            if (timestamped)
            {
                expectedStart = FramesAt(qpcPosition);
                if (expectedStart > latestStart)
                {
                    _alignment.Clamped(expectedStart - latestStart, Where(expectedStart));
                    expectedStart = latestStart;
                }
            }
            else
            {
                // Without a timestamp only the clock can say where the audio belongs, and it can be trusted for a
                // long pause but not for the exact moment: it moves with how late the packet was delivered, so
                // following it closely would drop audio from a burst that arrives after a stall. The packet goes
                // straight after the audio before it unless the source clearly paused.
                expectedStart = latestStart - _framesWritten > SampleRate * MaxPauseSeconds ? latestStart : _framesWritten;
            }

            long offset = expectedStart - _framesWritten;
            int dropped = 0;
            if (offset > SampleRate * MaxOffsetSeconds)
            {
                _alignment.Filled(offset, Where(_framesWritten));
                WriteSilence(offset);
            }
            else if (-offset > SampleRate * MaxOffsetSeconds)
            {
                // The file has run ahead of the clock, e.g. silence was filled for a packet that arrived late. This
                // packet's audio belongs where the file already is, so the overlap goes rather than the alignment.
                dropped = (int)Math.Min(frames, -offset);
                _alignment.Dropped(-offset, dropped, Where(_framesWritten));
                frames -= dropped;
                if (frames == 0)
                    return;
            }

            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                WriteSilence(frames);
                return;
            }

            ReadOnlySpan<byte> pcm = buffer[(dropped * segment.SourceBytesPerFrame)..];
            if (segment.ConvertFloat)
            {
                int samples = pcm.Length / sizeof(float);
                if (_scratch.Length < samples * sizeof(short))
                    _scratch = new byte[samples * sizeof(short)];
                ReadOnlySpan<float> input = MemoryMarshal.Cast<byte, float>(pcm);
                Span<short> output = MemoryMarshal.Cast<byte, short>(_scratch.AsSpan(0, samples * sizeof(short)));
                for (int i = 0; i < input.Length; i++)
                    output[i] = (short)(Math.Clamp(input[i], -1f, 1f) * short.MaxValue);
                pcm = _scratch.AsSpan(0, samples * sizeof(short));
            }
            Measure(MemoryMarshal.Cast<byte, short>(pcm), frames);
            _writer.Write(pcm);
            _framesWritten += frames;
        }
    }

    private void Measure(ReadOnlySpan<short> samples, int frames)
    {
        int peak = 0;
        foreach (short sample in samples)
            peak = Math.Max(peak, Math.Abs((int)sample));
        if (peak > SoundThreshold)
            _soundFrames += frames;
        _peak = Math.Max(_peak, peak);
        _peakSince = Math.Max(_peakSince, peak);
        _checkPeak = Math.Max(_checkPeak, peak);
        int minute = (int)(_framesWritten / (SampleRate * 60L));
        while (_minutePeaks.Count <= minute)
            _minutePeaks.Add(0);
        _minutePeaks[minute] = Math.Max(_minutePeaks[minute], peak);
    }

    private void OnSegmentStopped(Segment segment, Exception? error)
    {
        bool closing;
        lock (_sync)
        {
            if (segment != _current)
            {
                segment.Dispose();
                return;
            }
            closing = _stopTicks != null;
            if (!closing)
            {
                _current = null;
                Source = null;
            }
        }
        if (closing)
        {
            Close();
            return;
        }
        segment.Dispose();
        SourceFailed?.Invoke(this, error ?? new InvalidOperationException("the device stopped delivering audio"));
    }

    private void Close()
    {
        Segment? current;
        lock (_sync)
        {
            if (_closed)
                return;
            _closed = true;
            current = _current;
            _current = null;
            try
            {
                long expectedEnd = FramesAt(_stopTicks ?? RecordingJournal.Now());
                if (expectedEnd > _framesWritten)
                    WriteSilence(expectedEnd - _framesWritten);
                _writer.Dispose();
            }
            catch (IOException)
            {
            }
        }
        current?.Dispose();
        _stopped.TrySetResult();
    }

    private void WriteSilence(long frames)
    {
        var zeros = new byte[Math.Min(frames, SampleRate) * _bytesPerFrame];
        long remaining = frames * _bytesPerFrame;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, zeros.Length);
            _writer.Write(zeros, 0, chunk);
            remaining -= chunk;
        }
        _framesWritten += frames;
    }

    private long FramesAt(long ticks) => (ticks - _startTicks) * SampleRate / TimeSpan.TicksPerSecond;

    /// <summary>
    /// Counts what the capture delivered and every correction made to keep the file on the recording's clock, so the
    /// log says not only that a file moved but when, by how much and which way. Touched under the track's lock.
    /// </summary>
    private sealed class AlignmentLog
    {
        private long _packets;
        private long _untimedPackets;
        private long _discontinuities;
        private long _silentPackets;
        private long _frames;
        private long _filledFrames;
        private int _fills;
        private long _largestFill;
        private string? _largestFillAt;
        private long _droppedFrames;
        private int _drops;
        private long _largestOverlap;
        private string? _largestOverlapAt;
        private int _clamps;
        private long _largestClamp;
        private string? _largestClampAt;

        public void Packet(bool timestamped, bool discontinuity, bool silent, int frames)
        {
            _packets++;
            _frames += frames;
            if (!timestamped)
                _untimedPackets++;
            if (discontinuity)
                _discontinuities++;
            if (silent)
                _silentPackets++;
        }

        /// <summary>A timestamp that claimed audio from after now, by <paramref name="frames"/> frames.</summary>
        public void Clamped(long frames, string at)
        {
            _clamps++;
            if (frames > _largestClamp)
                (_largestClamp, _largestClampAt) = (frames, at);
        }

        public void Filled(long frames, string at)
        {
            _fills++;
            _filledFrames += frames;
            if (frames > _largestFill)
                (_largestFill, _largestFillAt) = (frames, at);
        }

        /// <summary>
        /// Audio that arrived for a moment the file had passed: <paramref name="overlap"/> frames behind the file,
        /// of which <paramref name="dropped"/> were in the packet and dropped.
        /// </summary>
        public void Dropped(long overlap, long dropped, string at)
        {
            _drops++;
            _droppedFrames += dropped;
            if (overlap > _largestOverlap)
                (_largestOverlap, _largestOverlapAt) = (overlap, at);
        }

        public AlignmentReport Report(string file, long offsetFrames, string at) => new(
            file, Seconds(offsetFrames), at, _packets, _untimedPackets, _discontinuities, _silentPackets, Seconds(_frames),
            Seconds(_filledFrames), _fills, Seconds(_largestFill), _largestFillAt,
            Seconds(_droppedFrames), _drops, Seconds(_largestOverlap), _largestOverlapAt,
            _clamps, Seconds(_largestClamp), _largestClampAt);

        private static double Seconds(long frames) => Math.Round(frames / (double)SampleRate, 3);
    }

    private sealed class Segment(WasapiRecorder capture)
    {
        public WasapiRecorder Capture { get; } = capture;
        public bool ConvertFloat { get; set; }
        public int SourceBytesPerFrame { get; set; }

        /// <summary>
        /// Disposes the capture on another thread: disposing waits for its capture thread, which may be the thread
        /// running this (a capture can raise its stopped event there).
        /// </summary>
        public void Dispose() => Task.Run(Capture.Dispose);

        /// <summary>Asks the capture to stop; false when it wasn't running, so no stopped event will follow.</summary>
        public bool Stop()
        {
            if (Capture.CaptureState is not (CaptureState.Starting or CaptureState.Capturing))
                return false;
            Capture.StopRecording();
            return true;
        }
    }
}

/// <summary>
/// Where a track's file sits on the recording's clock and what it took to keep it there. A positive
/// <paramref name="OffsetSeconds"/> means the file holds more audio than the clock has passed, so its sound plays
/// that much later than it happened; a negative one means it holds less.
/// </summary>
/// <param name="At">Where the file had reached when this was taken, on the recording's clock.</param>
/// <param name="OverlapSeconds">The furthest behind the file a packet's audio arrived, dropped to keep the rest in step.</param>
/// <param name="ClampedSeconds">The furthest into the future a timestamp claimed its audio was captured.</param>
sealed record AlignmentReport(
    string File, double OffsetSeconds, string At, long Packets, long UntimedPackets, long Discontinuities, long SilentPackets,
    double RecordedSeconds, double FilledSeconds, int Fills, double LargestFillSeconds, string? LargestFillAt,
    double DroppedSeconds, int Drops, double OverlapSeconds, string? OverlapAt,
    int ClampedTimestamps, double ClampedSeconds, string? ClampedAt)
{
    /// <summary>True when nothing had to be corrected and the file is where the clock says it should be.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Clean => Fills == 0 && Drops == 0 && ClampedTimestamps == 0 && Discontinuities == 0 && UntimedPackets == 0;

    /// <summary>One line per fact, for the log, the journal and README.md.</summary>
    public List<string> Describe()
    {
        if (Packets == 0)
            return ["Nothing has been delivered from this source; the file is filled with silence when the recording stops"];
        var lines = new List<string> { $"{OffsetSeconds:+0.###;-0.###;0} s from the recording's clock at {At}" };
        if (Fills > 0)
            lines.Add($"Filled {FilledSeconds:0.###} s of silence for the {Fills} pause(s) when the source delivered nothing, "
                + $"longest {LargestFillSeconds:0.###} s from {LargestFillAt}");
        if (Drops > 0)
            lines.Add($"Dropped {DroppedSeconds:0.###} s of audio that arrived for a moment the file had passed, {Drops} time(s), "
                + $"furthest behind {OverlapSeconds:0.###} s at {OverlapAt}");
        if (ClampedTimestamps > 0)
            lines.Add($"Held back {ClampedTimestamps} timestamp(s) that claimed audio from the future, "
                + $"up to {ClampedSeconds:0.###} s ahead at {ClampedAt}");
        if (Discontinuities > 0)
            lines.Add($"Windows flagged {Discontinuities} packet(s) as not following the one before "
                + "(expected after a pause; otherwise audio was lost before it reached us)");
        if (UntimedPackets > 0)
            lines.Add($"{UntimedPackets} of {Packets} packets had no usable timestamp and were placed after the audio before them");
        if (SilentPackets > 0)
            lines.Add($"{SilentPackets} of {Packets} packets were marked silent");
        return lines;
    }
}
