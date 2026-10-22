using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// Records one WASAPI endpoint (system loopback or a microphone) to a 16-bit PCM WAV file.
/// </summary>
/// <remarks>
/// WAV costs no encoding CPU while recording, and the Windows audio engine converts to 16-bit PCM for us.
/// Gaps are padded with silence using the packet timestamps because loopback capture delivers no packets
/// while nothing is playing, which would otherwise shorten the file and drift it out of sync with the video.
/// </remarks>
sealed class AudioFileRecorder
{
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");
    private const int SampleRate = 48000;
    private const double MaxGapSeconds = 0.2;
    private const int BufferMilliseconds = 200;

    private readonly MMDevice _device;
    private readonly WasapiRecorder _capture;
    private readonly WaveFileWriter _writer;
    private readonly bool _convertFloat;
    private readonly int _sourceBytesPerFrame;
    private readonly int _targetBytesPerFrame;
    private readonly int _sampleRate;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _sync = new();
    private byte[] _scratch = [];
    private long _startTicks;
    private long _framesWritten;
    private long? _stopTicks;
    private bool _closed;

    public string DeviceName { get; }

    /// <summary>True when the chosen device was unavailable and the Windows default was used instead.</summary>
    public bool UsedDefaultInstead { get; private set; }

    /// <summary>Set when the device failed mid-recording (e.g. headset unplugged); the file is still closed cleanly.</summary>
    public Exception? Error { get; private set; }

    /// <summary>Raised, possibly on a capture thread, when <see cref="Error"/> is set.</summary>
    public event Action<AudioFileRecorder>? Failed;

    private AudioFileRecorder(MMDevice device, bool loopback, string path)
    {
        _device = device;
        DeviceName = device.FriendlyName;

        // Voice needs one channel; system audio keeps stereo.
        var builder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithBufferLength(BufferMilliseconds)
            .WithFormat(new WaveFormat(SampleRate, 16, loopback ? 2 : 1));
        if (loopback)
            builder = builder.WithLoopbackCapture();
        _capture = builder.Build();

        WaveFormat source = _capture.WaveFormat;
        _sampleRate = source.SampleRate;
        _sourceBytesPerFrame = source.BlockAlign;
        // Fallback for drivers that ignore the requested format and hand back the float mix format.
        _convertFloat = source.BitsPerSample == 32 && (source.Encoding == WaveFormatEncoding.IeeeFloat ||
            (source is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat));

        WaveFormat target = _convertFloat ? new WaveFormat(source.SampleRate, 16, source.Channels) : source;
        _targetBytesPerFrame = target.BlockAlign;
        _writer = new WaveFileWriter(path, target);

        _capture.DataAvailable += OnDataAvailable;
        _capture.RecordingStopped += OnRecordingStopped;
    }

    /// <summary>
    /// Opens the configured device, or returns null when the source is disabled or no such device exists.
    /// </summary>
    public static AudioFileRecorder? TryCreate(DataFlow flow, string deviceId, string path)
    {
        if (deviceId == AppSettings.DisabledDevice)
            return null;

        using var enumerator = new MMDeviceEnumerator();
        MMDevice? device = null;
        if (deviceId != AppSettings.DefaultDevice)
        {
            try
            {
                device = enumerator.GetDevice(deviceId);
                if (device.State != DeviceState.Active)
                    device = null;
            }
            catch (COMException)
            {
            }
        }
        bool usedDefaultInstead = device == null && deviceId != AppSettings.DefaultDevice;
        if (device == null && !enumerator.TryGetDefaultAudioEndpoint(flow, Role.Console, out device))
            return null;

        return new AudioFileRecorder(device!, flow == DataFlow.Render, path) { UsedDefaultInstead = usedDefaultInstead };
    }

    public void Start()
    {
        _startTicks = QpcTicks();
        _capture.StartRecording();
    }

    /// <summary>Stops capturing; the returned task completes once the WAV file is finalized.</summary>
    public Task StopAsync()
    {
        lock (_sync)
            _stopTicks ??= QpcTicks();
        if (_capture.CaptureState is CaptureState.Starting or CaptureState.Capturing)
            _capture.StopRecording();
        else
            OnRecordingStopped(this, new StoppedEventArgs());
        return _stopped.Task;
    }

    private void OnDataAvailable(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        lock (_sync)
        {
            if (_closed)
                return;

            int frames = buffer.Length / _sourceBytesPerFrame;
            long expectedStart = qpcPosition > 0 && (flags & AudioClientBufferFlags.TimestampError) == 0
                ? FramesAt(qpcPosition)
                : FramesAt(QpcTicks()) - frames;
            if (expectedStart - _framesWritten > _sampleRate * MaxGapSeconds)
                WriteSilence(expectedStart - _framesWritten);

            if ((flags & AudioClientBufferFlags.Silent) != 0)
            {
                WriteSilence(frames);
                return;
            }

            if (_convertFloat)
            {
                int samples = buffer.Length / sizeof(float);
                if (_scratch.Length < samples * sizeof(short))
                    _scratch = new byte[samples * sizeof(short)];
                ReadOnlySpan<float> input = MemoryMarshal.Cast<byte, float>(buffer);
                Span<short> output = MemoryMarshal.Cast<byte, short>(_scratch.AsSpan(0, samples * sizeof(short)));
                for (int i = 0; i < input.Length; i++)
                    output[i] = (short)(Math.Clamp(input[i], -1f, 1f) * short.MaxValue);
                _writer.Write(_scratch, 0, samples * sizeof(short));
            }
            else
            {
                _writer.Write(buffer);
            }
            _framesWritten += frames;
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        lock (_sync)
        {
            if (_closed)
                return;
            _closed = true;
            Error ??= e.Exception;
            if (_stopTicks == null)
                Error ??= new InvalidOperationException("the device stopped delivering audio");
            try
            {
                long expectedEnd = FramesAt(_stopTicks ?? QpcTicks());
                if (expectedEnd > _framesWritten)
                    WriteSilence(expectedEnd - _framesWritten);
                _writer.Dispose();
            }
            catch (IOException ex)
            {
                Error ??= ex;
            }
            finally
            {
                _capture.Dispose();
                _device.Dispose();
            }
        }
        // Report before completing so the failure is handled ahead of anything awaiting StopAsync().
        if (Error != null)
            Failed?.Invoke(this);
        _stopped.TrySetResult();
    }

    private void WriteSilence(long frames)
    {
        var zeros = new byte[Math.Min(frames, _sampleRate) * _targetBytesPerFrame];
        long remaining = frames * _targetBytesPerFrame;
        while (remaining > 0)
        {
            int chunk = (int)Math.Min(remaining, zeros.Length);
            _writer.Write(zeros, 0, chunk);
            remaining -= chunk;
        }
        _framesWritten += frames;
    }

    private long FramesAt(long qpcTicks) => (qpcTicks - _startTicks) * _sampleRate / TimeSpan.TicksPerSecond;

    /// <summary>QueryPerformanceCounter in 100 ns units, the same clock WASAPI uses for packet timestamps.</summary>
    private static long QpcTicks() =>
        (long)(Stopwatch.GetTimestamp() * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
}
