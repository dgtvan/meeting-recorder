using NAudio.CoreAudioApi;

namespace MeetingRecorder;

/// <summary>
/// Microsoft Teams (new Teams, <c>ms-teams.exe</c>): detects a call or meeting in progress and finds its windows.
/// </summary>
/// <remarks>
/// A call is in progress while Teams holds the microphone, or plays sound with a meeting window open. A meeting window
/// is a Teams window with the call's Leave button, found by its UI Automation id, which stays the same whatever the
/// title says or the UI language is: the main window, the pre-join screen and the sharing toolbar have none. The
/// compact view shown while the meeting window is minimized has one too, so it is told apart by its title
/// ("Meeting compact view | "). Only when the controls can't be read does the title decide: the meeting window's is
/// "&lt;meeting&gt; | Microsoft Teams", with the user's organization first in a meeting organized by someone from
/// another organization, while the main window's starts with the section shown
/// ("Chat | &lt;chat name&gt; | Microsoft Teams") and the pre-join screen's with "Meeting join | ".
/// </remarks>
sealed class TeamsMeetingApp : IMeetingApp
{
    private const string ProcessName = "ms-teams";
    private const string WindowClass = "TeamsWebView";
    private const string TitleSuffix = " | Microsoft Teams";
    // While you share, Teams draws a border around what is shared in a window of this class, hidden from capture.
    private const string ShareBorderClass = "ScreenBorderWindow";
    private const string LeaveButtonId = "hangup-button";
    private static readonly TimeSpan LeaveButtonTimeout = TimeSpan.FromSeconds(2);
    // A window without the button is looked at again after this, in case its call controls were still loading.
    private static readonly TimeSpan RecheckAfter = TimeSpan.FromSeconds(10);
    private const string PreJoinPrefix = "Meeting join | ";
    // The small floating view Teams shows while the meeting window is minimized; it has its own Leave button.
    private const string CompactViewPrefix = "Meeting compact view | ";

    private static readonly HashSet<string> MainWindowSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Activity", "Chat", "Teams", "Calendar", "Calls", "OneDrive", "Files", "Apps", "Copilot", "Communities",
        "Assignments", "Approvals", "Shifts", "Planner", "Settings", "Help", "Search", "Microsoft Teams",
    };

    public string Id => "teams";

    public string DisplayName => "Microsoft Teams";

    /// <remarks>
    /// Teams opens the microphone when a call starts (already on the pre-join screen) but closes it again while the
    /// user is muted, so the microphone alone would end a call the moment they mute. The sound Teams plays says the
    /// call is still on; on its own that would also catch a notification chime, so it only counts while a meeting
    /// window is open, which Teams has only during a call.
    /// </remarks>
    public bool IsCallActive()
    {
        var processIds = MeetingWindows.ProcessIds(ProcessName);
        if (MeetingWindows.HasActiveAudioSession(processIds, DataFlow.Capture))
            return true;
        return MeetingWindows.HasActiveAudioSession(processIds, DataFlow.Render)
            && FindMeetingWindows(onScreenOnly: false).Count > 0;
    }

    private sealed record LeaveButtonCheck(string Title, bool? HasButton, DateTime CheckedAt);

    // Reading a window's controls takes up to ~0.1 s, so each window's answer is kept: a meeting window stays one
    // for as long as it's open (Teams closes it when the call ends; minimized while you share, it may not show its
    // controls), and any other window is looked at again when its title changes or after a while.
    private readonly Dictionary<IntPtr, LeaveButtonCheck> _checks = [];

    /// <remarks>A second call (one on hold, or one being answered) has its own window.</remarks>
    public List<WindowInfo> FindMeetingWindows(bool onScreenOnly = true) =>
        FindAppWindows()
            .Where(hwnd => MeetingWindows.ClassName(hwnd) == WindowClass && (!onScreenOnly || MeetingWindows.NotShownReason(hwnd) == null))
            .Select(hwnd => MeetingWindow(hwnd, MeetingWindows.Title(hwnd)))
            .OfType<WindowInfo>()
            .ToList();

    private WindowInfo? MeetingWindow(IntPtr hwnd, string title)
    {
        if (title.StartsWith(CompactViewPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        bool? hasLeaveButton = HasLeaveButton(hwnd, title);
        if (hasLeaveButton == false)
            return null;
        if (hasLeaveButton == true)
            return new WindowInfo(hwnd, NameOfCall(title));
        return MeetingName(title) is { } name ? new WindowInfo(hwnd, name) : null;
    }

    private bool? HasLeaveButton(IntPtr hwnd, string title)
    {
        lock (_checks)
        {
            DateTime now = DateTime.UtcNow;
            if (_checks.TryGetValue(hwnd, out var check)
                && (check.HasButton == true || check.Title == title && now - check.CheckedAt < RecheckAfter))
                return check.HasButton;
            foreach (IntPtr gone in _checks.Keys.Where(h => !WindowList.IsAlive(h)).ToList())
                _checks.Remove(gone);
            bool? hasButton = MeetingWindows.HasControl(hwnd, LeaveButtonId, LeaveButtonTimeout);
            _checks[hwnd] = new LeaveButtonCheck(title, hasButton, now);
            return hasButton;
        }
    }

    /// <summary>
    /// The call's name from the title of a window known to be a meeting window: the last part before
    /// "Microsoft Teams", e.g. "Daily standup" from "Contoso | Daily standup | Microsoft Teams".
    /// </summary>
    internal static string NameOfCall(string title)
    {
        string? name = title.Split(" | ", StringSplitOptions.TrimEntries)
            .LastOrDefault(part => part.Length > 0 && part != "Microsoft Teams" && part != "Pinned window");
        return name ?? "Teams call";
    }

    public List<IntPtr> FindAppWindows() => MeetingWindows.VisibleWindows(MeetingWindows.ProcessIds(ProcessName));

    /// <remarks>
    /// The share border has the shared window's frame bounds (following it as it moves) or the shared display's.
    /// Teams shows it as a 1x1 window for a moment while a share starts and ends.
    /// </remarks>
    public SharedContent? FindShare()
    {
        var processIds = MeetingWindows.ProcessIds(ProcessName);
        foreach (IntPtr hwnd in MeetingWindows.VisibleWindows(processIds))
        {
            if (MeetingWindows.ClassName(hwnd) == ShareBorderClass && MeetingWindows.Bounds(hwnd) is { } bounds
                && !MeetingWindows.IsTiny(bounds))
                return new SharedContent(bounds, MeetingWindows.SharedTarget(bounds));
        }
        return null;
    }

    /// <summary>
    /// "Daily standup | Microsoft Teams" gives "Daily standup", and so does "Contoso | Daily standup | Microsoft Teams"
    /// (Teams puts the user's organization first in a meeting organized by someone from another organization);
    /// main-window, pre-join and compact-view titles give null.
    /// </summary>
    internal static string? MeetingName(string title)
    {
        if (!title.EndsWith(TitleSuffix, StringComparison.Ordinal))
            return null;
        string[] parts = title[..^TitleSuffix.Length].Split(" | ", StringSplitOptions.TrimEntries);
        if (parts[0].Length == 0 || MainWindowSections.Contains(parts[0])
            || title.StartsWith(PreJoinPrefix, StringComparison.OrdinalIgnoreCase) || title.StartsWith(CompactViewPrefix, StringComparison.OrdinalIgnoreCase))
            return null;
        string name = parts.Length > 1 ? string.Join(" | ", parts[1..]) : parts[0];
        return name.Length > 0 ? name : null;
    }
}
