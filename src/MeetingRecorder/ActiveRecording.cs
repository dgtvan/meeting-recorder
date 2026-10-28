namespace MeetingRecorder;

enum RecordingPhase { Starting, Recording, Saving }

/// <summary>A recording started from the tray, from the moment it is launched until its worker exits.</summary>
sealed class ActiveRecording(RecordingProcess process, RecordingTarget target, MeetingCall? call, string videoFile)
{
    public RecordingProcess Process { get; } = process;
    public RecordingTarget Target { get; } = target;
    /// <summary>The call this was auto-recorded for; null for a recording started by hand.</summary>
    public MeetingCall? Call { get; } = call;
    public bool IsAuto => Call != null;
    /// <summary>The audio of a call, recorded next to its window videos; not listed as a recording of its own.</summary>
    public bool IsAudio => Target.IsAudioOnly;
    /// <summary>A screen share of a call, recorded next to its window videos.</summary>
    public bool IsShare => Target.IsShare;
    public string VideoFile { get; } = videoFile;
    /// <summary>
    /// A <see cref="RecordingFinisher"/> run on a folder whose recordings are saved: mixing the sound into the videos,
    /// compressing the audio and writing README.md. It starts in <see cref="RecordingPhase.Saving"/>.
    /// </summary>
    public bool IsFinishing { get; init; }
    /// <summary>How long the recording being finished is, which sets how long finishing may take.</summary>
    public TimeSpan RecordedFor { get; init; }
    public RecordingPhase Phase { get; private set; } = RecordingPhase.Starting;
    public DateTime PhaseSince { get; private set; } = DateTime.UtcNow;
    public bool IsCapturing => Phase is RecordingPhase.Starting or RecordingPhase.Recording;

    public void Enter(RecordingPhase phase)
    {
        Phase = phase;
        PhaseSince = DateTime.UtcNow;
    }
}

/// <summary>
/// An auto-recorded call: one folder with a video per meeting window (video.mp4, video-2.mp4, ... in the order they
/// opened) and one set of audio files, recorded by a worker of its own for the whole call.
/// </summary>
sealed class MeetingCall(AutoRecorder recorder, string folder, DateTime startedAt)
{
    /// <summary>The auto-recorder of the meeting app the call is in.</summary>
    public AutoRecorder Recorder { get; } = recorder;
    public string Folder { get; } = folder;
    public DateTime StartedAt { get; } = startedAt;
    /// <summary>Set once the call ended or was stopped; its folder is tidied up when the last recording is saved.</summary>
    public bool Ended { get; set; }
    public ActiveRecording? Audio { get; set; }
    /// <summary>Every window video started, including those already saved.</summary>
    public List<ActiveRecording> Videos { get; } = [];
    /// <summary>Set once the call's folder is being finished; it then waits for nothing else.</summary>
    public bool Finishing { get; set; }
    public string NextVideoFile => RecordingSession.NumberedVideoFileName(Videos.Count + 1);
    /// <summary>Every screen share recorded (share.mp4, share-2.mp4, ...), including those already saved.</summary>
    public List<ActiveRecording> Shares { get; } = [];
    public string NextShareFile => RecordingSession.ShareVideoFileName(Shares.Count + 1);
    /// <summary>
    /// Problems of a call too young to tell from a window Teams shows briefly as a call ends: raised once the call lasts
    /// <see cref="RecordingFinisher.MinAutoRecording"/> or is kept, only logged when it is dropped as too short.
    /// </summary>
    public List<string> HeldProblems { get; } = [];
    public bool IsYoung => DateTime.Now - StartedAt < RecordingFinisher.MinAutoRecording;
}
