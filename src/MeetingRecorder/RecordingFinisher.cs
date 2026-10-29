using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;

namespace MeetingRecorder;

/// <summary>
/// Finishes a recording folder once every recorder writing to it has exited
/// (<c>MeetingRecorder.exe --finish &lt;folder&gt; [--auto] --data-dir ...</c>): reads the recorders' journals, drops
/// what an auto-recorded call doesn't keep, mixes the system audio and microphone into each video, compresses the
/// audio, writes README.md describing every file (and the same facts as recording.json, for programs), and deletes the
/// journals.
/// </summary>
/// <remarks>
/// Runs in a process of its own, like a recording, so the tray stays small. It answers on stdout, one line per message:
/// "warning &lt;text&gt;" (the user should know), "dropped &lt;text&gt;" (a file or the whole call was deleted, as
/// intended), "log &lt;text&gt;" (for meetings.log), then "folder &lt;final path&gt;" and "finished" or "failed &lt;text&gt;".
/// Safe to run again on a folder, e.g. after it was killed: a folder without journals only gets its files renamed.
/// </remarks>
static class RecordingFinisher
{
    public const string Flag = "--finish";
    public const string ReadmeFileName = "README.md";
    public const string ManifestFileName = "recording.json";

    /// <summary>
    /// A call whose microphone has at least this much sound while the system audio has less than
    /// <see cref="OneSidedSystemSeconds"/> recorded only one side: the other participants are missing.
    /// </summary>
    private const double OneSidedMicSeconds = 30;
    private const double OneSidedSystemSeconds = 5;

    /// <summary>An auto-recorded call, or a video of one, shorter than this is deleted: a pre-join screen, a call left at once.</summary>
    public static readonly TimeSpan MinAutoRecording = TimeSpan.FromSeconds(30);

    /// <summary>A screen share shorter than this isn't cut into the meeting's video: one started and cancelled at once.</summary>
    private static readonly TimeSpan MinShare = TimeSpan.FromSeconds(2);

    private const string CompressedAudioExtension = ".m4a";
    private const string SoundTempSuffix = ".sound.tmp";
    private const string CompositeTempSuffix = ".composite.tmp";
    // The lowest bitrates the Windows AAC encoder offers; transparent for meeting audio.
    private const int StereoAacBitrate = 128_000;
    private const int MonoAacBitrate = 96_000;

    public static int Run(string[] args)
    {
        if (!AppPaths.TryInitialize(args))
        {
            Send("failed", $"Bad arguments: {AppPaths.DataDirFlag} is required");
            return 2;
        }
        int i = Array.IndexOf(args, Flag);
        if (i < 0 || i + 1 >= args.Length || !Directory.Exists(args[i + 1]))
        {
            Send("failed", $"Bad arguments: {Flag} needs an existing folder");
            return 2;
        }
        try
        {
            string folder = Finish(Path.GetFullPath(args[i + 1]), args.Contains("--auto"), AppSettings.Load(), Send);
            Send("folder", folder);
            Send("finished");
            return 0;
        }
        catch (Exception ex)
        {
            Send("failed", ex.Message);
            return 1;
        }
    }

    /// <summary>The command line for finishing a folder.</summary>
    public static string BuildArguments(string folder, bool auto) =>
        $"{Flag} \"{folder.TrimEnd('\\')}\" {AppPaths.Argument}{(auto ? " --auto" : "")}";

    private static void Send(string kind, string? text = null)
    {
        Console.Out.WriteLine(text == null ? kind : $"{kind} {text.ReplaceLineEndings(" | ")}");
        Console.Out.Flush();
    }

    private sealed class Video(string file)
    {
        public string File { get; set; } = file;
        public string? OriginalFile { get; set; }
        public string Target { get; set; } = "";
        public string? Description { get; set; }
        public string? App { get; set; }
        public long Start { get; set; }
        public long? End { get; set; }
        public string? Error { get; set; }
        public string? SoundError { get; set; }
        /// <summary>A screen share: where the tray cut back to the meeting window (<see cref="JournalEntry.ShareEnd"/>).</summary>
        public long? ShareEnd { get; set; }
        /// <summary>The screen shares cut into this video.</summary>
        public List<SharedSpan> Shares { get; } = [];
        public TimeSpan Length => TimeSpan.FromTicks((End ?? Start) - Start);
    }

    private sealed record SharedSpan(long Start, long End, string What);

    private sealed class Track(string kind, string file)
    {
        public string Kind { get; } = kind;
        public string File { get; set; } = file;
        public long Start { get; set; }
        public long? End { get; set; }
        public JournalEntry? Stats { get; set; }
        public List<JournalEntry> Sources { get; } = [];
        /// <summary>Part files mixed into this one (system audio of further output devices).</summary>
        public List<string> Parts { get; } = [];
        public TimeSpan Length => TimeSpan.FromTicks((End ?? Start) - Start);
    }

    /// <summary>Finishes the folder; returns its final path (an auto-recorded call's folder is renamed after its main window).</summary>
    public static string Finish(string folder, bool auto, AppSettings settings, Action<string, string> report)
    {
        // Left by a finisher that was killed: a half-written sound track or compressed file.
        foreach (string stale in Directory.EnumerateFiles(folder, "*" + SoundTempSuffix)
                     .Concat(Directory.EnumerateFiles(folder, "*" + CompositeTempSuffix))
                     .Concat(Directory.EnumerateFiles(folder, RecordingSession.TempFileName("*" + CompressedAudioExtension))))
            TryDelete(stale);
        // Left by a recording stopped before its first frame, whose recorder still held the file.
        foreach (string empty in Directory.EnumerateFiles(folder, RecordingSession.TempFileName("*" + Path.GetExtension(RecordingSession.VideoFileName)))
                     .Where(path => new FileInfo(path).Length == 0))
            TryDelete(empty);
        if (RecordingSession.FinalizeFiles(folder) is { } renameError)
            report("warning", renameError);

        foreach (string wav in Directory.EnumerateFiles(folder, "*.wav"))
        {
            if (RepairWav(wav) is { } repaired)
                report("log", $"{Path.GetFileName(wav)}: {repaired}");
        }

        var entries = RecordingJournal.ReadAll(folder);
        long lastSeen = entries.Count > 0 ? entries.Max(e => e.Qpc) : 0;
        var videos = new List<Video>();
        var tracks = new List<Track>();
        // Part files share a kind, so an entry belongs to the track of its file.
        Track? TrackOf(JournalEntry entry) =>
            tracks.LastOrDefault(t => t.File == entry.File) ?? tracks.LastOrDefault(t => t.Kind == entry.Track);
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case JournalEntry.VideoStart when entry.File != null:
                    videos.Add(new Video(entry.File) { Target = entry.Target ?? "", Description = entry.Text, App = entry.App, Start = entry.Qpc });
                    break;
                case JournalEntry.VideoEnd when videos.LastOrDefault(v => v.File == entry.File) is { } ended:
                    ended.End = entry.Qpc;
                    ended.Error = entry.Error;
                    break;
                case JournalEntry.TrackStart when entry.Track != null && entry.File != null:
                    var track = new Track(entry.Track, entry.File) { Start = entry.Qpc };
                    track.Sources.Add(entry);
                    tracks.Add(track);
                    break;
                case JournalEntry.TrackSource when TrackOf(entry) is { } changed:
                    changed.Sources.Add(entry);
                    break;
                case JournalEntry.TrackEnd when TrackOf(entry) is { } stopped:
                    stopped.End = entry.Qpc;
                    stopped.Stats = entry;
                    break;
                case JournalEntry.ShareEnd when videos.LastOrDefault(v => v.File == entry.File) is { } share:
                    share.ShareEnd ??= entry.Qpc;
                    break;
            }
        }
        // A recorder that was killed never wrote its end; its last entry is the best guess.
        foreach (var video in videos)
            video.End ??= lastSeen;
        foreach (var track in tracks)
            track.End ??= lastSeen;
        videos.RemoveAll(v => !File.Exists(Path.Combine(folder, v.File)));
        tracks.RemoveAll(t => !File.Exists(Path.Combine(folder, t.File)));
        var shares = videos.Where(v => RecordingSession.IsShareVideo(v.File)).ToList();
        videos.RemoveAll(shares.Contains);

        var notes = new List<string>();
        var dropped = new List<string>();
        if (auto && entries.Count > 0)
        {
            TimeSpan callLength = videos.Select(v => v.Length).Concat(tracks.Select(t => t.Length)).DefaultIfEmpty().Max();
            if (callLength < MinAutoRecording)
            {
                string name = Path.GetFileName(folder);
                Directory.Delete(folder, recursive: true);
                report("dropped", $"Dropped recording {name}: {callLength.TotalSeconds:0} s, shorter than {MinAutoRecording.TotalSeconds:0} s.");
                return folder;
            }
            DropShortVideos(folder, videos, notes, dropped, report);
        }
        if (shares.Count > 0)
            CutInShares(folder, videos, shares, entries, notes, dropped, settings, report);

        foreach (string kind in tracks.Select(t => t.Kind).Distinct().ToList())
            MergeParts(folder, tracks, kind, report);
        var recording = entries.FirstOrDefault(e => e.Kind == JournalEntry.Recording);
        var system = tracks.FirstOrDefault(t => t.Kind == "system");
        var microphone = tracks.FirstOrDefault(t => t.Kind == "mic");
        if (OneSided(system, microphone) is { } oneSided)
        {
            notes.Add(oneSided);
            if (auto)
                report("warning", $"{Path.GetFileName(folder)}: {oneSided}");
        }
        if (system != null || microphone != null)
        {
            foreach (var video in videos)
                AddSound(folder, video, system, microphone, report);
        }

        if (settings.CompressAudio)
        {
            foreach (var track in tracks)
            {
                if (CompressAudio(folder, track.File) is { } compressError)
                    report("warning", compressError);
                else
                    track.File = Path.ChangeExtension(track.File, CompressedAudioExtension);
            }
        }

        if (entries.Count > 0)
        {
            try
            {
                File.WriteAllText(Path.Combine(folder, ReadmeFileName), Readme(folder, recording, videos, tracks, entries, notes, dropped, settings));
                File.WriteAllText(Path.Combine(folder, ManifestFileName), Manifest(recording, videos, tracks, entries, notes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report("warning", $"Could not write {ReadmeFileName} or {ManifestFileName} in {Path.GetFileName(folder)}: {ex.Message}");
            }
        }
        foreach (string journal in RecordingJournal.Paths(folder).ToList())
            TryDelete(journal);

        if (auto && videos.Count > 0 && recording != null)
        {
            var main = videos.MaxBy(v => v.Length)!;
            var startedAt = entries.Min(e => e.Time);
            try
            {
                folder = RecordingSession.RenameFolder(folder, StartedAt(folder, startedAt), main.Target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                report("warning", $"Could not rename {Path.GetFileName(folder)}: {ex.Message}");
            }
        }
        report("log", $"Recording saved in {folder}");
        return folder;
    }

    /// <summary>The start time in the folder's name, which renaming keeps; the journal's time if it can't be read.</summary>
    private static DateTime StartedAt(string folder, DateTime fallback) =>
        DateTime.TryParseExact(Path.GetFileName(folder).Length >= 19 ? Path.GetFileName(folder)[..19] : "", "yyyy-MM-dd_HH-mm-ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : fallback;

    /// <summary>
    /// Deletes the videos of windows open only briefly (the pre-join screen before the meeting window), but never the
    /// call's longest, and numbers the rest in order: video.mp4, video-2.mp4, ...
    /// </summary>
    private static void DropShortVideos(string folder, List<Video> videos, List<string> notes, List<string> dropped,
        Action<string, string> report)
    {
        // The call itself lasted long enough to keep, so its longest video stays even if it is a bit shorter.
        var main = videos.MaxBy(v => v.Length);
        foreach (var video in videos.Where(v => v != main && v.Length < MinAutoRecording).ToList())
        {
            File.Delete(Path.Combine(folder, video.File));
            string text = $"{video.File} ({video.Target}) dropped: {video.Length.TotalSeconds:0.0} s, shorter than {MinAutoRecording.TotalSeconds:0} s.";
            notes.Add(text);
            report("dropped", $"{Path.GetFileName(folder)}: {text}");
            dropped.Add(video.File);
            videos.Remove(video);
        }
        videos.Sort((a, b) => a.Start.CompareTo(b.Start));
        // Each file moves to the same or a lower number, which is free by then.
        for (int i = 0; i < videos.Count; i++)
        {
            string numbered = RecordingSession.NumberedVideoFileName(i + 1);
            if (numbered == videos[i].File)
                continue;
            File.Move(Path.Combine(folder, videos[i].File), Path.Combine(folder, numbered));
            report("log", $"Renamed {videos[i].File} to {numbered}.");
            videos[i].OriginalFile = videos[i].File;
            videos[i].File = numbered;
        }
    }

    /// <summary>
    /// Re-encodes the meeting's main video (the longest) with the screen shares cut in: from a share's first frame
    /// until the tray cut back (the meeting window showed again), the shared window or display is shown instead, both
    /// scaled into the larger frame size. The share files are deleted once cut in; if that fails they are kept as
    /// videos of their own. Without a meeting video the shares are kept as they are.
    /// </summary>
    private static void CutInShares(string folder, List<Video> videos, List<Video> shares, List<JournalEntry> entries,
        List<string> notes, List<string> dropped, AppSettings settings, Action<string, string> report)
    {
        foreach (var share in shares)
            share.ShareEnd = Math.Min(share.ShareEnd ?? share.End!.Value, share.End!.Value);
        foreach (var share in shares.Where(s => TimeSpan.FromTicks(s.ShareEnd!.Value - s.Start) < MinShare).ToList())
        {
            TryDelete(Path.Combine(folder, share.File));
            notes.Add($"{share.File} ({share.Target}) dropped: a screen share shorter than {MinShare.TotalSeconds:0} s.");
            dropped.Add(share.File);
            shares.Remove(share);
        }
        var main = videos.MaxBy(v => v.Length);
        if (shares.Count == 0 || main == null)
        {
            videos.AddRange(shares);
            return;
        }

        shares.Sort((a, b) => a.Start.CompareTo(b.Start));
        string mainPath = Path.Combine(folder, main.File);
        string temp = mainPath + CompositeTempSuffix;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var all = shares.Prepend(main).ToList();
            var sizes = all.Select(v => VideoCompositor.FrameSize(Path.Combine(folder, v.File))).OfType<Size>().ToList();
            if (sizes.Count == 0)
                throw new InvalidDataException("The videos' frame sizes can't be read.");
            var frameSize = sizes.MaxBy(s => s.Width * (long)s.Height);
            var inputs = all.Select(v => new VideoCompositor.Input(Path.Combine(folder, v.File), v.Start - main.Start)).ToList();
            var cuts = new List<VideoCompositor.Cut> { new(0, 0) };
            for (int i = 0; i < shares.Count; i++)
            {
                long start = Math.Max(0, shares[i].Start - main.Start);
                long end = shares[i].ShareEnd!.Value - main.Start;
                if (i + 1 < shares.Count)
                    end = Math.Min(end, shares[i + 1].Start - main.Start);
                cuts.Add(new(start, i + 1));
                cuts.Add(new(end, 0));
            }
            VideoCompositor.Write(inputs, cuts, frameSize, settings.VideoQuality, temp);
            File.Move(temp, mainPath, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            report("warning", $"The screen share in {Path.GetFileName(folder)} could not be cut into {main.File} and is kept "
                + $"as a video of its own: {ex.Message}");
            videos.AddRange(shares);
            return;
        }

        report("log", $"Cut {shares.Count} screen share(s) into {main.File} in {stopwatch.Elapsed.TotalSeconds:0} s.");
        foreach (var share in shares)
        {
            TryDelete(Path.Combine(folder, share.File));
            main.Shares.Add(new SharedSpan(share.Start, share.ShareEnd!.Value, share.Description ?? share.Target));
            foreach (var entry in entries.Where(e => e.File == share.File))
                entry.File = $"{main.File} (screen share)";
        }
        main.End = Math.Max(main.End ?? main.Start, shares.Max(s => s.ShareEnd!.Value));
    }

    /// <summary>
    /// Mixes the part files of one kind of track (the system audio of each output device) into the first one,
    /// e.g. audio-system-2.wav into audio-system.wav. They all start together.
    /// </summary>
    private static void MergeParts(string folder, List<Track> tracks, string kind, Action<string, string> report)
    {
        var parts = tracks.Where(t => t.Kind == kind).ToList();
        if (parts.Count < 2)
            return;
        var main = parts[0];
        var others = parts.Where(t => t != main).ToList();
        string path = Path.Combine(folder, main.File);
        string temp = path + SoundTempSuffix;
        try
        {
            var readers = parts.Select(t => new WaveFileReader(Path.Combine(folder, t.File))).ToList();
            try
            {
                using var writer = new WaveFileWriter(temp, readers[0].WaveFormat);
                var buffer = new byte[AudioTrack.SampleRate * readers[0].WaveFormat.BlockAlign];
                var sum = new int[buffer.Length / 2];
                while (true)
                {
                    Array.Clear(sum);
                    int longest = 0;
                    foreach (var reader in readers)
                    {
                        int read = reader.Read(buffer.AsSpan());
                        longest = Math.Max(longest, read);
                        var samples = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, read & ~1));
                        for (int i = 0; i < samples.Length; i++)
                            sum[i] += samples[i];
                    }
                    if (longest == 0)
                        break;
                    var output = new byte[longest];
                    var mixed = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(output.AsSpan());
                    for (int i = 0; i < mixed.Length; i++)
                        mixed[i] = (short)Math.Clamp(sum[i], short.MinValue, short.MaxValue);
                    writer.Write(output, 0, longest);
                }
            }
            finally
            {
                foreach (var reader in readers)
                    reader.Dispose();
            }
            File.Move(temp, path, overwrite: true);
            foreach (var other in others)
            {
                File.Delete(Path.Combine(folder, other.File));
                main.Parts.Add(other.File);
                main.Sources.AddRange(other.Sources);
                main.Stats = Merge(main.Stats, other.Stats);
                tracks.Remove(other);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or InvalidOperationException)
        {
            TryDelete(temp);
            report("warning", $"The {kind} audio parts in {Path.GetFileName(folder)} could not be mixed and were kept apart: {ex.Message}");
        }
    }

    private static JournalEntry? Merge(JournalEntry? a, JournalEntry? b)
    {
        if (a == null || b == null)
            return a ?? b;
        var minutes = new double[Math.Max(a.MinutePeaks?.Length ?? 0, b.MinutePeaks?.Length ?? 0)];
        // A part of a device that played nothing has no minutes; a missing minute is silence, not 0 dBFS.
        static double At(double[]? peaks, int i) => peaks != null && i < peaks.Length ? peaks[i] : -100;
        for (int i = 0; i < minutes.Length; i++)
            minutes[i] = Math.Max(At(a.MinutePeaks, i), At(b.MinutePeaks, i));
        return a with
        {
            SoundSeconds = Math.Max(a.SoundSeconds ?? 0, b.SoundSeconds ?? 0),
            PeakDb = Math.Max(a.PeakDb ?? -100, b.PeakDb ?? -100),
            MinutePeaks = minutes,
        };
    }

    /// <summary>Mixes the system audio and microphone of the video's time span into it. A video that can't get its sound keeps none.</summary>
    private static void AddSound(string folder, Video video, Track? system, Track? microphone, Action<string, string> report)
    {
        string path = Path.Combine(folder, video.File);
        string temp = path + SoundTempSuffix;
        try
        {
            long audioStart = (system ?? microphone)!.Start;
            WaveStream? Open(Track? track) => track == null ? null : new WaveFileReader(Path.Combine(folder, track.File));
            // Both files start together (one recorder opens them), so one offset fits both.
            long startFrame = (video.Start - audioStart) * AudioTrack.SampleRate / TimeSpan.TicksPerSecond;
            using (var mix = new SoundMix(Open(system), Open(microphone), startFrame))
                VideoSoundMuxer.Write(path, mix, video.Length, temp);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            video.SoundError = ex.Message;
            report("warning", $"{video.File} in {Path.GetFileName(folder)} has no sound (the separate audio files are fine): {ex.Message}");
        }
    }

    /// <summary>
    /// Fixes the sizes in the header of a WAV whose recorder was killed before it could write them, so the sound in it
    /// can be read. Returns what was fixed, or null when nothing needed fixing.
    /// </summary>
    private static string? RepairWav(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var reader = new BinaryReader(file);
        using var writer = new BinaryWriter(file);
        if (file.Length < 12 || reader.ReadUInt32() != 0x46464952 /* RIFF */)
            return null;
        uint riffSize = reader.ReadUInt32();
        if (reader.ReadUInt32() != 0x45564157 /* WAVE */)
            return null;
        while (file.Position + 8 <= file.Length)
        {
            uint id = reader.ReadUInt32();
            long sizeAt = file.Position;
            uint size = reader.ReadUInt32();
            if (id != 0x61746164 /* data */)
            {
                file.Position += size + (size & 1);
                continue;
            }
            long actual = file.Length - file.Position;
            if (size == actual && riffSize == file.Length - 8)
                return null;
            file.Position = sizeAt;
            writer.Write((uint)Math.Min(actual, uint.MaxValue));
            file.Position = 4;
            writer.Write((uint)Math.Min(file.Length - 8, uint.MaxValue));
            return $"header repaired (the recorder stopped without closing it), {actual / (double)(1 << 20):0.0} MB of sound";
        }
        return null;
    }

    /// <summary>Converts one WAV in the folder to AAC, then deletes the WAV; returns the problem, or null. A WAV that can't be converted is kept.</summary>
    private static string? CompressAudio(string folder, string wavName)
    {
        string wav = Path.Combine(folder, wavName);
        string target = Path.Combine(folder, Path.ChangeExtension(wavName, CompressedAudioExtension));
        string temp = RecordingSession.TempFileName(target);
        try
        {
            using (var reader = new WaveFileReader(wav))
            {
                // The stream overload writes MP4 regardless of the file name; the path overload goes by the extension.
                using var output = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                MediaFoundationEncoder.EncodeToAac(reader, output, reader.WaveFormat.Channels == 1 ? MonoAacBitrate : StereoAacBitrate);
            }
            File.Move(temp, target, overwrite: true);
            File.Delete(wav);
            return null;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return $"{wavName} could not be compressed and was kept as WAV: {ex.Message}";
        }
    }

    private static string Readme(string folder, JournalEntry? recording, List<Video> videos, List<Track> tracks,
        List<JournalEntry> entries, List<string> notes, List<string> dropped, AppSettings settings)
    {
        long origin = videos.Select(v => v.Start).Concat(tracks.Select(t => t.Start)).DefaultIfEmpty(entries[0].Qpc).Min();
        string At(long qpc) => Clock(TimeSpan.FromTicks(Math.Max(0, qpc - origin)));
        DateTime started = entries.Min(e => e.Time);
        DateTime ended = entries.Max(e => e.Time);
        var system = tracks.FirstOrDefault(t => t.Kind == "system");
        var microphone = tracks.FirstOrDefault(t => t.Kind == "mic");

        var text = new StringBuilder();
        text.AppendLine($"# {Path.GetFileName(folder)}");
        text.AppendLine();
        text.AppendLine($"Recorded by Meeting Recorder from {started:yyyy-MM-dd HH:mm:ss} to {ended:HH:mm:ss} "
            + $"(local time, UTC{started:zzz}), {Duration(ended - started)}.");
        if (recording != null)
        {
            text.AppendLine();
            text.AppendLine($"What was recorded: {recording.Text}{(recording.Auto == true ? ", recorded automatically when the call started" : "")}.");
        }
        text.AppendLine();
        text.AppendLine("## Files");
        text.AppendLine();
        text.AppendLine("Every file is on the same clock, which starts with the first file: *Starts at* says where on it a file "
            + "begins, so a file that starts at 00:01:10.0 lines up with the 00:01:10.0 point of a file that starts at 00:00:00.0.");
        text.AppendLine();
        text.AppendLine("| File | What it holds | Starts at | Length |");
        text.AppendLine("|---|---|---|---|");
        string mixed = string.Join(" and ", new[] { system, microphone }.Where(t => t != null).Select(t => $"`{t!.File}`"));
        foreach (var video in videos)
        {
            string sound = video.SoundError != null ? $"No sound: adding it failed ({Cell(video.SoundError)})."
                : mixed.Length == 0 ? "No sound: no audio was recorded."
                : $"Its sound is {mixed} mixed, for watching.";
            string end = video.Error != null ? $" The video stopped early: {Cell(video.Error)}." : "";
            string shared = string.Concat(video.Shares.Select(s => $" From {At(s.Start)} to {At(s.End)} it shows your screen share instead: {Cell(s.What)}."));
            text.AppendLine($"| `{video.File}` | Picture of {Cell(video.Description ?? video.Target)}{(video.App != null ? $" ({video.App})" : "")}, "
                + $"H.264.{shared} {sound}{end} | {At(video.Start)} | {Duration(video.Length)} |");
        }
        if (system != null)
            text.AppendLine($"| `{system.File}` | Everything the PC played: every app, on every speaker or headset (each output "
                + $"device is recorded and they are mixed): in a call, the other participants. Stereo. {Sound(system)} "
                + $"| {At(system.Start)} | {Duration(system.Length)} |");
        if (microphone != null)
            text.AppendLine($"| `{microphone.File}` | The microphone in use, mono; see *Microphone* for which one when. {Sound(microphone)} "
                + $"| {At(microphone.Start)} | {Duration(microphone.Length)} |");
        text.AppendLine($"| `{ReadmeFileName}` | This description. | | |");
        text.AppendLine($"| `{ManifestFileName}` | The same facts for programs (JSON). | | |");

        var missing = new List<string>();
        if (system == null)
            missing.Add(settings.SystemAudioDeviceId == AppSettings.DisabledDevice ? "No system audio: it is turned off in the tray menu." : "No system audio: it could not be recorded.");
        if (microphone == null)
            missing.Add(settings.MicrophoneDeviceId == AppSettings.DisabledDevice ? "No microphone: it is turned off in the tray menu." : "No microphone: it could not be recorded.");
        foreach (string line in missing)
            text.AppendLine().Append(line).AppendLine();

        if (microphone != null)
        {
            text.AppendLine();
            text.AppendLine("## Microphone");
            text.AppendLine();
            text.AppendLine("Recorded from the microphone the recorded program uses, else one another app uses, else the Windows default; "
                + "it follows a change during the recording (unless one is chosen in the tray menu).");
            text.AppendLine();
            text.AppendLine("| From | Device and why |");
            text.AppendLine("|---|---|");
            foreach (var source in microphone.Sources)
                text.AppendLine($"| {At(source.Qpc)} | {Cell(source.Text ?? "")} |");
        }

        text.AppendLine();
        text.AppendLine("## Timeline");
        text.AppendLine();
        text.AppendLine("Everything that happened to the recording and to the audio devices, on the clock above.");
        text.AppendLine();
        // Journals name the files as they were written; the table and timeline use their final names.
        var renamed = videos.Where(v => v.OriginalFile != null).ToDictionary(v => v.OriginalFile!, v => v.File);
        foreach (string file in dropped)
            renamed[file] = $"{file} (dropped)";
        foreach (var track in tracks)
        {
            renamed[Path.ChangeExtension(track.File, ".wav")] = track.File;
            foreach (string part in track.Parts)
                renamed[part] = $"{track.File} (part {part})";
        }
        foreach (var entry in entries)
        {
            if (entry.File != null && renamed.TryGetValue(entry.File, out string? final))
                entry.File = final;
            string? line = entry.Kind switch
            {
                JournalEntry.VideoStart => $"{entry.File}: started ({entry.Target})",
                JournalEntry.VideoEnd => $"{entry.File}: stopped{(entry.Error != null ? $", failed: {entry.Error}" : "")}",
                JournalEntry.TrackStart => $"{entry.File}: recording {entry.Text}",
                JournalEntry.TrackSource => $"{entry.File}: now recording {entry.Text}",
                JournalEntry.TrackEnd => $"{entry.File}: stopped",
                JournalEntry.Event => entry.Text,
                JournalEntry.ShareEnd => $"{entry.File}: back to the meeting window",
                _ => null,
            };
            if (line != null)
                text.AppendLine($"- {At(entry.Qpc)} ({entry.Time:HH:mm:ss}) {line}");
        }

        if (notes.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("## Notes");
            text.AppendLine();
            foreach (string note in notes)
                text.AppendLine($"- {note}");
        }
        return text.ToString();
    }

    /// <summary>Why the recording holds only one side of a call, or null when both sides have sound.</summary>
    private static string? OneSided(Track? system, Track? microphone)
    {
        if (system?.Stats?.SoundSeconds is not { } heard || microphone?.Stats?.SoundSeconds is not { } spoken)
            return null;
        return spoken >= OneSidedMicSeconds && heard < OneSidedSystemSeconds
            ? $"Only one side was recorded: the microphone has {Duration(TimeSpan.FromSeconds(spoken))} of sound but the "
              + $"system audio only {heard:0} s, so what the other participants said is missing."
            : null;
    }

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    /// <summary>recording.json: README.md's facts for programs, e.g. an analysis that decides which files to use.</summary>
    private static string Manifest(JournalEntry? recording, List<Video> videos, List<Track> tracks, List<JournalEntry> entries, List<string> notes)
    {
        long origin = videos.Select(v => v.Start).Concat(tracks.Select(t => t.Start)).DefaultIfEmpty(entries[0].Qpc).Min();
        double At(long qpc) => Math.Round(Math.Max(0, qpc - origin) / (double)TimeSpan.TicksPerSecond, 1);
        var system = tracks.FirstOrDefault(t => t.Kind == "system");
        var microphone = tracks.FirstOrDefault(t => t.Kind == "mic");
        string? mixed = system != null || microphone != null
            ? string.Join(",", new[] { system, microphone }.Where(t => t != null).Select(t => t!.File))
            : null;
        var files = new List<object>();
        foreach (var video in videos)
            files.Add(new
            {
                File = video.File,
                Kind = "video",
                Holds = video.Description ?? video.Target,
                App = video.App,
                StartSeconds = At(video.Start),
                LengthSeconds = Math.Round(video.Length.TotalSeconds, 1),
                SoundIsMixOf = video.SoundError == null ? mixed?.Split(',') : null,
                ScreenShares = video.Shares.Count == 0 ? null
                    : video.Shares.Select(s => new { StartSeconds = At(s.Start), EndSeconds = At(s.End), Holds = s.What }).ToList(),
                Error = video.Error ?? video.SoundError,
            });
        foreach (var track in tracks)
            files.Add(new
            {
                File = track.File,
                Kind = track.Kind == "mic" ? "microphone" : track.Kind,
                Holds = track.Kind switch
                {
                    "system" => "everything the PC played, on every output device: in a call, the other participants",
                    "mic" => "the microphone in use: the person who recorded",
                    _ => track.Kind,
                },
                Speaker = track.Kind switch { "system" => "others", "mic" => "me", _ => null },
                StartSeconds = At(track.Start),
                LengthSeconds = Math.Round(track.Length.TotalSeconds, 1),
                SoundSeconds = track.Stats?.SoundSeconds,
                PeakDb = track.Stats?.PeakDb,
                Silent = track.Stats?.SoundSeconds is { } seconds ? seconds < 1 : (bool?)null,
                MinutePeaksDb = track.Stats?.MinutePeaks,
                Sources = track.Sources.Select(e => new { AtSeconds = At(e.Qpc), Text = e.Text }).ToList(),
            });
        var manifest = new
        {
            Schema = 1,
            Recorder = "Meeting Recorder",
            Started = entries.Min(e => e.Time),
            Ended = entries.Max(e => e.Time),
            What = recording?.Text,
            Target = recording?.Target,
            App = recording?.App,
            Auto = recording?.Auto,
            Clock = "start_seconds and at_seconds are on one clock for every file, starting with the first file",
            Files = files,
            Notes = notes,
        };
        return JsonSerializer.Serialize(manifest, ManifestOptions);
    }

    private static string Sound(Track track)
    {
        if (track.Stats?.SoundSeconds is not { } seconds)
            return "";
        if (seconds < 1)
            return $"**Silent or nearly silent** (loudest {track.Stats.PeakDb:0.0} dBFS).";
        string minutes = "";
        if (track.Stats.MinutePeaks is { Length: > 1 } peaks)
        {
            var silent = peaks.Select((peak, minute) => (peak, minute)).Where(p => p.peak <= -60).Select(p => p.minute).ToList();
            if (silent.Count > 0)
                minutes = $" Silent minutes: {Ranges(silent)}.";
        }
        return $"Sound in {Duration(TimeSpan.FromSeconds(seconds))}, loudest {track.Stats.PeakDb:0.0} dBFS.{minutes}";
    }

    /// <summary>"3, 5-9" for minute numbers counted from 0, shown counted from 1.</summary>
    private static string Ranges(List<int> minutes)
    {
        var parts = new List<string>();
        for (int i = 0; i < minutes.Count;)
        {
            int j = i;
            while (j + 1 < minutes.Count && minutes[j + 1] == minutes[j] + 1)
                j++;
            parts.Add(i == j ? $"{minutes[i] + 1}" : $"{minutes[i] + 1}-{minutes[j] + 1}");
            i = j + 1;
        }
        return string.Join(", ", parts);
    }

    private static string Clock(TimeSpan time) => $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 100}";

    private static string Duration(TimeSpan time) => time.TotalHours >= 1 ? $"{(int)time.TotalHours} h {time.Minutes} min {time.Seconds} s"
        : time.TotalMinutes >= 1 ? $"{time.Minutes} min {time.Seconds} s" : $"{time.TotalSeconds:0} s";

    private static string Cell(string text) => text.ReplaceLineEndings(" ").Replace("|", "\\|");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
