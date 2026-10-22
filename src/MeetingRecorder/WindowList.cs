using System.Runtime.InteropServices;
using System.Text;

namespace MeetingRecorder;

/// <summary>A top-level window the user can pick as a recording target.</summary>
sealed record WindowInfo(IntPtr Handle, string Title);

/// <summary>
/// Lists the windows that would show on the taskbar or Alt+Tab, which is what the user expects to choose from.
/// </summary>
static class WindowList
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const int DWMWA_CLOAKED = 14;
    private const uint GW_OWNER = 4;
    private const int SW_RESTORE = 9;

    public static List<WindowInfo> GetRecordableWindows()
    {
        var windows = new List<WindowInfo>();
        uint ownProcessId = (uint)Environment.ProcessId;

        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero)
                return true;
            if ((GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOOLWINDOW) != 0)
                return true;
            // UWP frames and windows on other virtual desktops are "cloaked": visible to Win32 but not on screen.
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;
            GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId == ownProcessId)
                return true;

            string title = GetTitle(hwnd);
            if (title.Length > 0 && title != "Program Manager")
                windows.Add(new WindowInfo(hwnd, title));
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    public static bool IsAlive(IntPtr hwnd) => IsWindow(hwnd);

    /// <summary>
    /// Windows Graphics Capture only delivers frames for windows that are not minimized.
    /// </summary>
    public static void RestoreIfMinimized(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SW_RESTORE);
    }

    private static string GetTitle(IntPtr hwnd)
    {
        int length = GetWindowTextLength(hwnd);
        if (length == 0)
            return string.Empty;
        var buffer = new StringBuilder(length + 1);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString().Trim();
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
