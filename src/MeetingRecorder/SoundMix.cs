using System.Runtime.InteropServices;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// The sound of a video: the system audio and the microphone files of the recording mixed into one 16-bit stereo
/// track, starting <c>startFrame</c> frames into them (negative: that much silence first). It ends when both files do.
/// </summary>
sealed class SoundMix : IWaveProvider, IDisposable
{
    private readonly WaveStream? _system;
    private readonly WaveStream? _microphone;
    private long _silenceFrames;
    private byte[] _systemBuffer = [];
    private byte[] _microphoneBuffer = [];

    public WaveFormat WaveFormat { get; } = AudioTrack.Format(2);

    public SoundMix(WaveStream? system, WaveStream? microphone, long startFrame)
    {
        if (system != null && (system.WaveFormat.Channels != 2 || system.WaveFormat.BitsPerSample != 16 || system.WaveFormat.SampleRate != AudioTrack.SampleRate))
            throw new ArgumentException($"System audio is {system.WaveFormat}, not {WaveFormat}.");
        if (microphone != null && (microphone.WaveFormat.Channels != 1 || microphone.WaveFormat.BitsPerSample != 16 || microphone.WaveFormat.SampleRate != AudioTrack.SampleRate))
            throw new ArgumentException($"Microphone is {microphone.WaveFormat}, not 16-bit mono at {AudioTrack.SampleRate} Hz.");
        _system = system;
        _microphone = microphone;
        if (startFrame < 0)
        {
            _silenceFrames = -startFrame;
            startFrame = 0;
        }
        if (_system != null)
            _system.Position = Math.Min(_system.Length, startFrame * _system.WaveFormat.BlockAlign);
        if (_microphone != null)
            _microphone.Position = Math.Min(_microphone.Length, startFrame * _microphone.WaveFormat.BlockAlign);
    }

    public int Read(Span<byte> buffer)
    {
        int frames = buffer.Length / WaveFormat.BlockAlign;
        buffer[..(frames * WaveFormat.BlockAlign)].Clear();
        Span<short> output = MemoryMarshal.Cast<byte, short>(buffer[..(frames * WaveFormat.BlockAlign)]);

        int done = 0;
        if (_silenceFrames > 0)
        {
            done = (int)Math.Min(_silenceFrames, frames);
            _silenceFrames -= done;
        }
        if (done == frames)
            return frames * WaveFormat.BlockAlign;

        int wanted = frames - done;
        int systemFrames = ReadFrames(_system, ref _systemBuffer, wanted);
        int microphoneFrames = ReadFrames(_microphone, ref _microphoneBuffer, wanted);
        ReadOnlySpan<short> system = MemoryMarshal.Cast<byte, short>(_systemBuffer.AsSpan(0, systemFrames * 4));
        ReadOnlySpan<short> microphone = MemoryMarshal.Cast<byte, short>(_microphoneBuffer.AsSpan(0, microphoneFrames * 2));
        int mixed = Math.Max(systemFrames, microphoneFrames);
        for (int i = 0; i < mixed; i++)
        {
            int mic = i < microphoneFrames ? microphone[i] : 0;
            int left = (i < systemFrames ? system[2 * i] : 0) + mic;
            int right = (i < systemFrames ? system[2 * i + 1] : 0) + mic;
            output[2 * (done + i)] = (short)Math.Clamp(left, short.MinValue, short.MaxValue);
            output[2 * (done + i) + 1] = (short)Math.Clamp(right, short.MinValue, short.MaxValue);
        }
        return (done + mixed) * WaveFormat.BlockAlign;
    }

    private static int ReadFrames(WaveStream? stream, ref byte[] buffer, int frames)
    {
        if (stream == null)
            return 0;
        int bytes = frames * stream.WaveFormat.BlockAlign;
        if (buffer.Length < bytes)
            buffer = new byte[bytes];
        int total = 0;
        while (total < bytes)
        {
            int read = stream.Read(buffer.AsSpan(total, bytes - total));
            if (read <= 0)
                break;
            total += read;
        }
        return total / stream.WaveFormat.BlockAlign;
    }

    public void Dispose()
    {
        _system?.Dispose();
        _microphone?.Dispose();
    }
}
