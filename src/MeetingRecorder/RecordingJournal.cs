using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MeetingRecorder;

/// <summary>
/// One line of a <see cref="RecordingJournal"/>. Which fields are set depends on <see cref="Kind"/>; see the constants.
/// </summary>
sealed record JournalEntry
{
    /// <summary>What a worker records: <see cref="Target"/>, <see cref="Text"/> (window, display, ...), <see cref="Auto"/>, <see cref="App"/>.</summary>
    public const string Recording = "recording";
    /// <summary>The first video frame, <see cref="File"/>.</summary>
    public const string VideoStart = "video-start";
    /// <summary>The video stopped, <see cref="File"/>; <see cref="Error"/> when it failed.</summary>
    public const string VideoEnd = "video-end";
    /// <summary>An audio file started, <see cref="Track"/> and <see cref="File"/>, recording <see cref="Text"/>.</summary>
    public const string TrackStart = "track-start";
    /// <summary>A track's source changed to <see cref="Text"/>, e.g. another microphone.</summary>
    public const string TrackSource = "track-source";
    /// <summary>A track stopped, with its sound statistics; <see cref="Error"/> when its source failed.</summary>
    public const string TrackEnd = "track-end";
    /// <summary>Something that happened during the recording, e.g. a device was plugged in.</summary>
    public const string Event = "event";
    /// <summary>
    /// The screen share recorded in <see cref="File"/> is over from here on: the meeting window shows again, or the
    /// share stopped a while ago. Written by the tray; the finisher cuts back to the meeting window here.
    /// </summary>
    public const string ShareEnd = "share-end";

    public string Kind { get; init; } = "";
    public DateTime Time { get; init; } = DateTime.Now;
    /// <summary>The QueryPerformanceCounter in 100 ns units: the same clock in every process, used to line files up.</summary>
    public long Qpc { get; init; } = RecordingJournal.Now();
    public string? File { get; set; }
    public string? Track { get; init; }
    public string? Text { get; init; }
    public string? Target { get; init; }
    public bool? Auto { get; init; }
    public string? App { get; init; }
    public double? SoundSeconds { get; init; }
    public double? PeakDb { get; init; }
    /// <summary>The loudest moment of each minute in dBFS, -100 for a silent minute.</summary>
    public double[]? MinutePeaks { get; init; }
    public string? Error { get; init; }
}

/// <summary>
/// What one recorder process did, appended as JSON lines to <c>&lt;name&gt;.journal.jsonl</c> in the recording folder
/// as it happens: what it recorded, from which app and devices, when each file started and stopped, and every device
/// change. <see cref="RecordingFinisher"/> reads the journals to line the files up, mix the sound into the videos and
/// write README.md, then deletes them. Appending line by line keeps it useful after a crash.
/// </summary>
sealed class RecordingJournal
{
    public const string Extension = ".journal.jsonl";

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly object _sync = new();

    public RecordingJournal(string folder, string name) => _path = Path.Combine(folder, name + Extension);

    /// <summary>Appends one entry; a journal that can't be written must never stop the recording.</summary>
    public void Write(JournalEntry entry)
    {
        try
        {
            string line = JsonSerializer.Serialize(entry, Options) + "\n";
            lock (_sync)
                System.IO.File.AppendAllText(_path, line);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Every entry of every journal in the folder, in time order; lines that can't be read are skipped.</summary>
    public static List<JournalEntry> ReadAll(string folder)
    {
        var entries = new List<JournalEntry>();
        foreach (string path in Paths(folder))
        {
            foreach (string line in System.IO.File.ReadLines(path))
            {
                try
                {
                    if (JsonSerializer.Deserialize<JournalEntry>(line, Options) is { } entry)
                        entries.Add(entry);
                }
                catch (JsonException)
                {
                    // A line cut short by a crash.
                }
            }
        }
        return entries.OrderBy(e => e.Qpc).ToList();
    }

    public static IEnumerable<string> Paths(string folder) =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*" + Extension) : [];

    /// <summary>The QueryPerformanceCounter in 100 ns units, the same clock WASAPI uses for packet timestamps.</summary>
    public static long Now() => (long)(Stopwatch.GetTimestamp() * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
}
