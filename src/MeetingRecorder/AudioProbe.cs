using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// Diagnostic (<c>--audio-probe [seconds]</c>): for every active output device, compares what our loopback capture
/// hears with what Windows says the device is playing (the endpoint meter and each app's own session meter), second
/// by second. It answers the question a recording with silent system audio leaves open: did the device play nothing,
/// or did it play something our loopback can't hear (a stream that bypasses the audio engine, e.g. exclusive mode)?
/// Writes no files and captures no screen.
/// </summary>
static class AudioProbe
{
    public const string Flag = "--audio-probe";
    private const int DefaultSeconds = 30;
    private const int SampleMilliseconds = 100;
    private const double Floor = -100;

    public static int Run(string[] args, TextWriter output)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out int given) && given > 0 ? given : DefaultSeconds;
        List<Probe> probes;
        using var enumerator = new MMDeviceEnumerator();
        probes = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => new Probe(d)).ToList();
        if (probes.Count == 0)
        {
            output.WriteLine("no active output device");
            return 1;
        }
        foreach (var probe in probes)
            probe.Start(output);
        output.WriteLine($"Probing for {seconds} s. loop = what our loopback capture hears, meter = Windows' own meter "
            + "for the device (dBFS, -100 = silence).");

        var started = Environment.TickCount64;
        int printed = 0;
        while (Environment.TickCount64 - started < seconds * 1000L)
        {
            Thread.Sleep(SampleMilliseconds);
            foreach (var probe in probes)
                probe.Sample();
            int elapsed = (int)((Environment.TickCount64 - started) / 1000);
            if (elapsed > printed)
            {
                printed = elapsed;
                var line = new StringBuilder($"t={printed,3}s ");
                foreach (var probe in probes)
                    line.Append($" | {probe.ShortName} loop {Db(probe.TakeSecondLoopback()),6:0.0} meter {Db(probe.TakeSecondMeter()),6:0.0}");
                output.WriteLine(line.ToString());
            }
        }

        output.WriteLine();
        foreach (var probe in probes)
        {
            probe.Stop();
            output.WriteLine(probe.Summary());
        }
        foreach (var probe in probes)
            probe.Dispose();
        return 0;
    }

    private static double Db(float peak) => peak <= 0 ? Floor : Math.Max(Floor, 20 * Math.Log10(peak));

    /// <summary>One output device: its loopback capture, its endpoint meter and the meter of every app playing on it.</summary>
    private sealed class Probe : IDisposable
    {
        private readonly MMDevice _device;
        private readonly string _name;
        private WasapiRecorder? _capture;
        private string? _error;
        private WaveFormat? _captureFormat;
        private float _loopbackPeak;
        private float _loopbackSecond;
        private float _meterPeak;
        private float _meterSecond;
        private long _packets;
        private long _frames;
        private readonly Dictionary<string, SessionPeak> _sessions = [];

        public Probe(MMDevice device)
        {
            _device = device;
            _name = device.FriendlyName;
        }

        public string ShortName => _name.Length <= 22 ? _name : _name[..22];

        public void Start(TextWriter output)
        {
            string format;
            try
            {
                using var client = _device.CreateAudioClient();
                format = Describe(client.MixFormat);
            }
            catch (Exception ex)
            {
                format = $"unknown ({ex.Message})";
            }
            try
            {
                _capture = new WasapiRecorderBuilder()
                    .WithDevice(_device)
                    .WithBufferLength(50)
                    .WithFormat(AudioTrack.Format(2))
                    .WithLoopbackCapture()
                    .Build();
                _captureFormat = _capture.WaveFormat;
                _capture.DataAvailable += (ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, long _, long _) =>
                    OnData(buffer, flags);
                _capture.StartRecording();
            }
            catch (Exception ex)
            {
                _error = ex.Message;
            }
            output.WriteLine($"{_name}: device format {format}; loopback "
                + (_error == null ? $"opened as {Describe(_captureFormat!)}" : $"failed: {_error}"));
        }

        private void OnData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags)
        {
            _packets++;
            var format = _captureFormat!;
            _frames += buffer.Length / Math.Max(1, format.BlockAlign);
            if ((flags & AudioClientBufferFlags.Silent) != 0)
                return;
            float peak = 0;
            if (format.BitsPerSample == 16)
            {
                for (int i = 0; i + 1 < buffer.Length; i += 2)
                {
                    short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
                    peak = Math.Max(peak, Math.Abs(sample / 32768f));
                }
            }
            else if (format.BitsPerSample == 32)
            {
                for (int i = 0; i + 3 < buffer.Length; i += 4)
                {
                    float sample = BitConverter.ToSingle(buffer[i..(i + 4)]);
                    peak = Math.Max(peak, Math.Abs(sample));
                }
            }
            _loopbackPeak = Math.Max(_loopbackPeak, peak);
            _loopbackSecond = Math.Max(_loopbackSecond, peak);
        }

        /// <summary>Reads Windows' own meters for the device and for every app playing on it.</summary>
        public void Sample()
        {
            try
            {
                float meter = _device.AudioMeterInformation.MasterPeakValue;
                _meterPeak = Math.Max(_meterPeak, meter);
                _meterSecond = Math.Max(_meterSecond, meter);
                var sessions = _device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    uint pid = session.GetProcessID;
                    string who = pid == 0 ? "system sounds" : ProcessTree.Describe(pid)?.ToString() ?? $"pid {pid}";
                    float peak = session.AudioMeterInformation.MasterPeakValue;
                    if (!_sessions.TryGetValue(who, out var seen))
                        _sessions[who] = seen = new SessionPeak();
                    seen.Peak = Math.Max(seen.Peak, peak);
                    seen.States.Add(session.State.ToString().Replace("AudioSessionState", ""));
                }
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
                // Devices and sessions come and go while probing; the next sample decides.
            }
        }

        public float TakeSecondLoopback()
        {
            float value = _loopbackSecond;
            _loopbackSecond = 0;
            return value;
        }

        public float TakeSecondMeter()
        {
            float value = _meterSecond;
            _meterSecond = 0;
            return value;
        }

        public void Stop()
        {
            try
            {
                _capture?.StopRecording();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
            }
        }

        public string Summary()
        {
            var text = new StringBuilder($"{_name}: loopback loudest {Db(_loopbackPeak):0.0} dBFS in {_packets} packets "
                + $"({_frames / (double)AudioTrack.SampleRate:0.##} s of audio); Windows' meter loudest {Db(_meterPeak):0.0} dBFS.");
            if (_error != null)
                text.Append($" Loopback failed: {_error}.");
            foreach (var (who, seen) in _sessions.OrderByDescending(s => s.Value.Peak))
                text.Append($"{Environment.NewLine}  session {who}: loudest {Db(seen.Peak):0.0} dBFS, {string.Join("/", seen.States)}");
            return text.ToString();
        }

        public void Dispose()
        {
            _capture?.Dispose();
            _device.Dispose();
        }

        private static string Describe(WaveFormat format) =>
            $"{format.SampleRate} Hz, {format.BitsPerSample}-bit {format.Encoding}, {format.Channels} ch";

        private sealed class SessionPeak
        {
            public float Peak;
            public SortedSet<string> States { get; } = [];
        }
    }
}
