using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetingRecorder;

/// <summary>Window and audio helpers for detecting a meeting app's calls (<see cref="IMeetingApp"/>).</summary>
static class MeetingWindows
{
    private const int MinWindowSize = 100;
    private const int DWMWA_CLOAKED = 14;
    private const uint WDA_NONE = 0;

    /// <summary>The ids of every running process with this name (no ".exe").</summary>
    public static HashSet<uint> ProcessIds(string processName) => Process.GetProcessesByName(processName).Select(p =>
    {
        using (p)
            return (uint)p.Id;
    }).ToHashSet();

    /// <summary>
    /// True while one of the processes holds an active microphone capture session. Meeting apps open one only while
    /// in a call (muting keeps it open) and close it when the call ends.
    /// </summary>
    public static bool IsCapturingMicrophone(HashSet<uint> processIds)
    {
        if (processIds.Count == 0)
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
                    if (session.State == AudioSessionState.AudioSessionStateActive && processIds.Contains(session.GetProcessID))
                        return true;
                }
            }
        }
        return false;
    }

    /// <summary>Every visible top-level window of the processes, in z-order.</summary>
    public static List<IntPtr> VisibleWindows(HashSet<uint> processIds)
    {
        var found = new List<IntPtr>();
        if (processIds.Count == 0)
            return found;
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processIds.Contains(processId) && IsWindowVisible(hwnd))
                found.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>
    /// Why a window shows nothing to record, or null when it does: minimized, cloaked, excluded from screen capture
    /// (Teams opens such windows with the meeting's title while you share), tiny, or off every display. Capturing one
    /// fails, and restoring a minimized one would pop it up over what you share. It's found again once it shows.
    /// </summary>
    public static string? NotShownReason(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            return "minimized";
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
            return "cloaked";
        if (GetWindowDisplayAffinity(hwnd, out uint affinity) && affinity != WDA_NONE)
            return "excluded from capture";
        if (Bounds(hwnd) is not { } bounds)
            return "gone";
        if (bounds.Width < MinWindowSize || bounds.Height < MinWindowSize)
            return "tiny";
        return Screen.AllScreens.Any(screen => screen.Bounds.IntersectsWith(bounds)) ? null : "off screen";
    }

    public static Rectangle? Bounds(IntPtr hwnd) =>
        GetWindowRect(hwnd, out RECT r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : null;

    public static string Title(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    public static string ClassName(IntPtr hwnd)
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

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int maxCount);
}
