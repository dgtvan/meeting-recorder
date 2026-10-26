namespace MeetingRecorder;

/// <summary>
/// Microsoft Teams (new Teams, <c>ms-teams.exe</c>): detects a call or meeting in progress and finds its windows.
/// </summary>
/// <remarks>
/// A call is in progress while Teams holds an active microphone capture session. The meeting window is the separate
/// Teams window titled "&lt;meeting&gt; | Microsoft Teams"; the main window's title starts with the section shown,
/// e.g. "Chat | &lt;chat name&gt; | Microsoft Teams", and the pre-join screen's with "Meeting join | ".
/// </remarks>
sealed class TeamsMeetingApp : IMeetingApp
{
    private const string ProcessName = "ms-teams";
    private const string WindowClass = "TeamsWebView";
    private const string TitleSuffix = " | Microsoft Teams";

    private static readonly HashSet<string> MainWindowSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Activity", "Chat", "Teams", "Calendar", "Calls", "OneDrive", "Files", "Apps", "Copilot", "Communities",
        "Assignments", "Approvals", "Shifts", "Planner", "Settings", "Help", "Search", "Microsoft Teams",
    };

    public string Id => "teams";

    public string DisplayName => "Microsoft Teams";

    public bool IsCallActive() => MeetingWindows.IsCapturingMicrophone(MeetingWindows.ProcessIds(ProcessName));

    /// <remarks>A second call (one on hold, or one being answered) has its own window.</remarks>
    public List<WindowInfo> FindMeetingWindows(bool onScreenOnly = true) =>
        FindAppWindows()
            .Where(hwnd => MeetingWindows.ClassName(hwnd) == WindowClass && (!onScreenOnly || MeetingWindows.NotShownReason(hwnd) == null))
            .Select(hwnd => MeetingName(MeetingWindows.Title(hwnd)) is { } name ? new WindowInfo(hwnd, name) : null)
            .OfType<WindowInfo>()
            .ToList();

    public List<IntPtr> FindAppWindows() => MeetingWindows.VisibleWindows(MeetingWindows.ProcessIds(ProcessName));

    /// <summary>"Daily standup | Microsoft Teams" gives "Daily standup"; main-window titles give null.</summary>
    internal static string? MeetingName(string title)
    {
        if (!title.EndsWith(TitleSuffix, StringComparison.Ordinal))
            return null;
        string name = title[..^TitleSuffix.Length].Trim();
        if (name.Length == 0 || name.Contains(" | ") || MainWindowSections.Contains(name))
            return null;
        return name;
    }
}
