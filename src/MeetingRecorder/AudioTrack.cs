using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// Records one audio file (16-bit PCM WAV, 48 kHz) from a source that can be replaced while recording, e.g. when the
/// microphone in use changes. The file keeps one timeline: gaps (a source switch, nothing playing) are filled with
/// silence by the packet timestamps, so it stays in sync with the video.
/// </summary>
/// <remarks>
/// WAV costs no encoding CPU while recording and survives a crash; <see cref="RecordingFinisher"/> converts it to AAC.
/// Loopback capture delivers no packets while nothing is playing, which would otherwise shorten the file.
/// </remarks>
sealed class AudioTrack
{
    public const int SampleRate = 48000;
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private const double MaxGapSeconds = 0.2;
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
    private long? _stopTicks;
    private bool _closed;

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

    private static double Decibels(int peak) => peak <= 0 ? -100 : Math.Round(20 * Math.Log10(peak / 32768.0), 1);

    private void OnData(Segment segment, ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long qpcPosition)
    {
        lock (_sync)
        {
            // A replaced source can still deliver a packet or two while it stops.
            if (_closed || segment != _current)
                return;

            int frames = buffer.Length / segment.SourceBytesPerFrame;
            long expectedStart = qpcPosition > 0 && (flags & AudioClientBufferFlags.TimestampError) == 0
                ? FramesAt(qpcPosition)
                : FramesAt(RecordingJournal.Now()) - frames;
            if (expectedStart - _framesWritten > SampleRate * MaxGapSeconds)
                WriteSilence(expectedStart - _framesWritten);

            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                WriteSilence(frames);
                return;
            }

            ReadOnlySpan<byte> pcm = buffer;
            if (segment.ConvertFloat)
            {
                int samples = buffer.Length / sizeof(float);
                if (_scratch.Length < samples * sizeof(short))
                    _scratch = new byte[samples * sizeof(short)];
                ReadOnlySpan<float> input = MemoryMarshal.Cast<byte, float>(buffer);
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
