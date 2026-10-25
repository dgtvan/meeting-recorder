using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetingRecorder;

/// <summary>
/// Detects a Microsoft Teams (new Teams, <c>ms-teams.exe</c>) call or meeting in progress and finds its window.
/// </summary>
/// <remarks>
/// A call is in progress while Teams holds an active microphone capture session: Teams opens one only while in a
/// call (muting keeps it open) and closes it when the call ends. The meeting window is the separate Teams window
/// titled "&lt;meeting&gt; | Microsoft Teams"; the main window's title starts with the section shown, e.g.
/// "Chat | &lt;chat name&gt; | Microsoft Teams".
/// </remarks>
static class TeamsMeeting
{
    private const string ProcessName = "ms-teams";
    private const string WindowClass = "TeamsWebView";
    private const string TitleSuffix = " | Microsoft Teams";

    private static readonly HashSet<string> MainWindowSections = new(StringComparer.OrdinalIgnoreCase)
    {
        "Activity", "Chat", "Teams", "Calendar", "Calls", "OneDrive", "Files", "Apps", "Copilot", "Communities",
        "Assignments", "Approvals", "Shifts", "Planner", "Settings", "Help", "Search", "Microsoft Teams",
    };

    /// <summary>True while Teams is capturing from any microphone, i.e. a call or meeting is in progress.</summary>
    public static bool IsCallActive()
    {
        var teamsProcessIds = Process.GetProcessesByName(ProcessName).Select(p =>
        {
            using (p)
                return (uint)p.Id;
        }).ToHashSet();
        if (teamsProcessIds.Count == 0)
            return false;

        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    using var session = sessions[i];
                    if (session.State == AudioSessionState.AudioSessionStateActive && teamsProcessIds.Contains(session.GetProcessID))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Every open meeting or call window, named after the meeting; empty when Teams shows none. A second call (one
    /// on hold, or one being answered) has its own window.
    /// </summary>
    public static List<WindowInfo> FindMeetingWindows()
    {
        var teamsProcessIds = Process.GetProcessesByName(ProcessName).Select(p =>
        {
            using (p)
                return (uint)p.Id;
        }).ToHashSet();
        var found = new List<WindowInfo>();
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (teamsProcessIds.Contains(processId) && IsWindowVisible(hwnd) && ClassName(hwnd) == WindowClass
                && MeetingName(Title(hwnd)) is { } name)
                found.Add(new WindowInfo(hwnd, name));
            return true;
        }, IntPtr.Zero);
        return found;
    }

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

    private static string Title(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private static string ClassName(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);
}
