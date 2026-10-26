using System.Runtime.InteropServices;

namespace MeetingRecorder;

/// <summary>What an <see cref="AutoRecorder"/> needs from the tray: its recordings, and a way to tell the user.</summary>
interface IAutoRecordHost
{
    IReadOnlyCollection<ActiveRecording> Recordings { get; }

    /// <summary>True once the app is exiting; no recording starts then.</summary>
    bool IsExiting { get; }

    /// <summary>Creates the recordings folder if needed and returns it.</summary>
    string EnsureRecordingsRoot();

    /// <summary>Starts recording a window or a call's audio into the call's folder; false when it didn't start.</summary>
    bool StartRecording(RecordingTarget target, MeetingCall call);

    void StopRecording(ActiveRecording recording);

    /// <summary>Something the user must act on: logged, and the tray shows a warning until they look at it.</summary>
    void AddProblem(string text);

    void ShowBalloon(string title, string text, ToolTipIcon icon);

    /// <summary>A call's folder is complete; it is the one to open from the tray now.</summary>
    void OnCallSaved(string folder);
}

/// <summary>
/// Auto-records one meeting app's calls, checked every few seconds by the tray. Once a call has been seen for a few
/// seconds it records every meeting window into the call's folder (a call on hold keeps its own window, so it is
/// recorded too) and the call's audio with the first one. It stops a window's video as soon as the window closes, and
/// the whole call once no meeting window is open or the app is out of the call for a few seconds.
/// </summary>
sealed class AutoRecorder(IMeetingApp app, IAutoRecordHost host)
{
    /// <summary>How often <see cref="Check"/> is called.</summary>
    public const int CheckMilliseconds = 3000;
    // A call must be seen twice to start (skips a quick device test) and be gone four times in a row to stop (rides
    // out a device switch mid-call).
    private const int CallStartedChecks = 2;
    private const int CallEndedChecks = 4;
    // A call whose meeting windows are all closed ends after two checks, long enough for Teams to replace the
    // pre-join screen with the meeting window.
    private const int NoWindowChecks = 2;
    private static readonly TimeSpan MinAutoRecording = TimeSpan.FromSeconds(30);

    private readonly MeetingLog _log = new(app);
    // Meeting windows auto-recorded during this call: one that was stopped by hand or failed waits for the next call.
    private readonly HashSet<IntPtr> _tried = [];
    private int _callChecks;
    private int _noCallChecks;
    private int _noWindowChecks;
    private bool _callSeen;
    private MeetingCall? _call;

    public IMeetingApp App => app;

    /// <summary>Forgets what the last checks saw, e.g. after auto-record was turned back on.</summary>
    public void Reset() => _callChecks = _noCallChecks = 0;

    public void Check()
    {
        // Meeting apps close the meeting window the moment the call ends, so this is the quickest end signal. Teams
        // also replaces the window mid-call (the pre-join screen, which already holds the microphone, becomes the
        // meeting window on Join); the new window is recorded below like any other, and the call's audio carries on.
        foreach (var recording in host.Recordings.Where(r => r.Call?.Recorder == this && !r.IsAudio && r.IsCapturing
                     && !WindowList.IsAlive(r.Target.WindowHandle)).ToList())
            host.StopRecording(recording);
        // With every meeting window gone for a moment the call is over, well before the app lets go of the microphone.
        if (_call != null && app.FindMeetingWindows(onScreenOnly: false).Count == 0)
        {
            if (++_noWindowChecks >= NoWindowChecks)
                EndCall("no meeting window open");
        }
        else
        {
            _noWindowChecks = 0;
        }

        bool inCall;
        try
        {
            inCall = app.IsCallActive();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            // Audio devices can be mid-change; the next check decides.
            return;
        }

        if (!inCall)
        {
            _callChecks = 0;
            if (++_noCallChecks < CallEndedChecks)
                return;
            _tried.Clear();
            EndCall("the app left the call");
            if (_callSeen)
            {
                _callSeen = false;
                _log.Reset();
                _log.Append("Call over.");
            }
            return;
        }

        _noCallChecks = 0;
        if (!_callSeen)
        {
            _callSeen = true;
            _log.Append("Call seen.");
        }
        _log.SnapshotIfChanged();
        if (++_callChecks < CallStartedChecks || host.IsExiting)
            return;
        foreach (var window in app.FindMeetingWindows())
        {
            if (!_tried.Add(window.Handle))
                continue;
            bool first = _call == null;
            if (StartWindow(window))
                host.ShowBalloon(first ? $"Recording {app.DisplayName} meeting" : $"Also recording another {app.DisplayName} window",
                    $"{window.Title}\nStops by itself when the meeting ends.", ToolTipIcon.Info);
        }
    }

    /// <summary>Starts recording a meeting window into the current call's folder, starting the call first if needed.</summary>
    private bool StartWindow(WindowInfo window)
    {
        if (host.Recordings.Any(r => r.IsCapturing && r.Target.WindowHandle == window.Handle))
            return false;
        if (_call == null)
        {
            try
            {
                var startedAt = DateTime.Now;
                _call = new MeetingCall(this, RecordingSession.CreateFolder(host.EnsureRecordingsRoot(), startedAt, window.Title), startedAt);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                host.AddProblem($"Could not start recording: {ex.Message}");
                return false;
            }
            _log.Append($"Call recording started in {_call.Folder}");
            host.StartRecording(RecordingTarget.ForAudio($"Call audio: {window.Title}"), _call);
        }
        _log.Append($"Recording window {window.Handle} \"{window.Title}\" as {_call.NextVideoFile}");
        return host.StartRecording(RecordingTarget.ForWindow(window), _call);
    }

    /// <summary>Stops every recording of the current call; its folder is tidied up once the last one is saved.</summary>
    public void EndCall(string reason)
    {
        if (_call is not { } call)
            return;
        _log.Append($"Call recording ending: {reason}.");
        _call = null;
        _noWindowChecks = 0;
        call.Ended = true;
        foreach (var recording in host.Recordings.Where(r => r.Call == call).ToList())
            host.StopRecording(recording);
        FinishCallIfSaved(call);
    }

    /// <summary>A window was stopped from the menu; stopping the last window of the call ends the call.</summary>
    public void OnStoppedByHand(ActiveRecording recording)
    {
        if (recording.Call is { } call && call == _call
            && !host.Recordings.Any(r => r.Call == call && !r.IsAudio && r.IsCapturing))
            EndCall("its last window was stopped by hand");
    }

    /// <summary>
    /// Called when a recording of one of this app's calls has exited. A window that failed before its first frame while
    /// another window of the call is recorded is only logged: Teams opens windows with the meeting's title that can't
    /// be captured.
    /// </summary>
    public void OnRecordingFinished(ActiveRecording recording, string? error)
    {
        var call = recording.Call!;
        string length = recording.Process.CapturedFor is { } captured ? $"{captured.TotalSeconds:0} s" : "never started";
        _log.Append($"{(recording.IsAudio ? "Call audio" : recording.VideoFile)} ({recording.Target.Name}) ended: {length}"
            + (error != null ? $", failed: {error}" : "."));
        if (error != null)
        {
            if (!recording.IsAudio && recording.Process.CapturedFor == null
                && call.Videos.Any(v => v != recording && v.Process.CapturedFor != null))
                WarningLog.Append($"{app.DisplayName} window not recorded ({recording.Target.Name}), the call's other windows are: {error}");
            else
                host.AddProblem($"Recording failed: {error}");
        }
        FinishCallIfSaved(call);
    }

    private void FinishCallIfSaved(MeetingCall call)
    {
        if (call.Ended && !host.Recordings.Any(r => r.Call == call))
            FinishCall(call);
    }

    /// <summary>
    /// Tidies up a call's folder once every recording of it is saved. A call shorter than
    /// <see cref="MinAutoRecording"/> (a pre-join screen, a call left at once) is deleted. Otherwise the videos of
    /// windows open that briefly (the pre-join screen before the meeting window) are deleted, the rest are numbered in
    /// order, and the folder is named after the window recorded longest. Deletions are logged without a warning.
    /// </summary>
    private void FinishCall(MeetingCall call)
    {
        string folder = call.Folder;
        if (!Directory.Exists(folder))
            return;
        string name = Path.GetFileName(folder);
        // The workers rename their own files; this covers one that was killed or crashed first.
        if (RecordingSession.FinalizeFiles(folder) is { } renameError)
            host.AddProblem(renameError);
        try
        {
            TimeSpan Length(ActiveRecording? r) => r?.Process.CapturedFor ?? TimeSpan.Zero;
            TimeSpan callLength = call.Videos.Select(Length).Append(Length(call.Audio)).Max();
            if (callLength < MinAutoRecording)
            {
                Directory.Delete(folder, recursive: true);
                WarningLog.Append($"Dropped {app.DisplayName} recording {name}: {callLength.TotalSeconds:0} s, shorter than {MinAutoRecording.TotalSeconds:0} s.");
                _log.Append($"Call recording dropped: {callLength.TotalSeconds:0} s.");
                return;
            }

            var kept = new List<ActiveRecording>();
            foreach (var video in call.Videos.Where(v => File.Exists(Path.Combine(folder, v.VideoFile))))
            {
                if (Length(video) >= MinAutoRecording)
                {
                    kept.Add(video);
                    continue;
                }
                File.Delete(Path.Combine(folder, video.VideoFile));
                string dropped = $"Dropped {video.VideoFile} ({video.Target.Name}) from {app.DisplayName} recording {name}: "
                    + $"{Length(video).TotalSeconds:0} s, shorter than {MinAutoRecording.TotalSeconds:0} s.";
                WarningLog.Append(dropped);
                _log.Append(dropped);
            }
            // Each file moves to the same or a lower number, which is free by then.
            for (int i = 0; i < kept.Count; i++)
            {
                string numbered = RecordingSession.NumberedVideoFileName(i + 1);
                if (numbered == kept[i].VideoFile)
                    continue;
                File.Move(Path.Combine(folder, kept[i].VideoFile), Path.Combine(folder, numbered));
                _log.Append($"Renamed {kept[i].VideoFile} to {numbered}.");
            }
            if (kept.MaxBy(Length) is { } main)
                folder = RecordingSession.RenameFolder(folder, call.StartedAt, main.Target.Name);
            _log.Append($"Call recording saved in {folder}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            host.AddProblem($"Could not tidy up the {app.DisplayName} recording {name}: {ex.Message}");
        }
        host.OnCallSaved(folder);
    }
}
