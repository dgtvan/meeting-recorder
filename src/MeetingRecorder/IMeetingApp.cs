namespace MeetingRecorder;

/// <summary>
/// A meeting app whose calls can be auto-recorded. To support another app, implement this and add it to
/// <see cref="MeetingApps.All"/>: the tray menu, the setting, call detection, recording, logging and
/// <c>--meeting-status</c> all work from that list. <see cref="MeetingWindows"/> has the window and audio helpers.
/// </summary>
interface IMeetingApp
{
    /// <summary>Stable key for settings, logs and the command line, e.g. "teams". Never change it once released.</summary>
    string Id { get; }

    /// <summary>The name shown in the menu, notifications and logs, e.g. "Microsoft Teams".</summary>
    string DisplayName { get; }

    /// <summary>
    /// True while the app is in a call or meeting. Called every few seconds, so it must be cheap. May throw
    /// <see cref="System.Runtime.InteropServices.COMException"/> or <see cref="InvalidOperationException"/> while
    /// audio devices change; the next check decides then.
    /// </summary>
    bool IsCallActive();

    /// <summary>
    /// Every window showing a call or meeting, named after it; each is recorded as a video of the call. With
    /// <paramref name="onScreenOnly"/>, only those that show something to record (<see cref="MeetingWindows.NotShownReason"/>);
    /// without it, also hidden ones, which keep the call going.
    /// </summary>
    List<WindowInfo> FindMeetingWindows(bool onScreenOnly = true);

    /// <summary>Every visible top-level window of the app, described in the log to tell its windows apart.</summary>
    List<IntPtr> FindAppWindows();
}

/// <summary>The meeting apps the app can auto-record.</summary>
static class MeetingApps
{
    public static readonly IReadOnlyList<IMeetingApp> All = [new TeamsMeetingApp()];

    public static IMeetingApp? Find(string id) => All.FirstOrDefault(app => string.Equals(app.Id, id, StringComparison.OrdinalIgnoreCase));
}
