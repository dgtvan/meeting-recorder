using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MeetingRecorder;

/// <summary>
/// Diagnostic for <c>--meeting-status</c>: describes every visible window of a meeting app, to learn which one is the
/// meeting, the sharing toolbar, and so on. Teams' meeting windows all share one title, so it reports what tells them
/// apart: size and position, window styles, whether the app hides it from capture, and the buttons it shows (read
/// through UI Automation, the accessibility interface screen readers use). Captures nothing.
/// </summary>
static class MeetingWindowReport
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;
    private const int MaxButtons = 40;
    private static readonly TimeSpan ButtonTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A cheap summary of the app's windows (no UI Automation) that changes when a window opens or closes, changes
    /// title, size, or whether it's shown or on top, but not when it only moves.
    /// </summary>
    public static string Signature(IMeetingApp app) => string.Join("\n", app.FindAppWindows().Select(hwnd =>
    {
        var size = MeetingWindows.Bounds(hwnd)?.Size;
        bool topmost = (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
        return $"{hwnd} {MeetingWindows.Title(hwnd)} {size} {MeetingWindows.NotShownReason(hwnd)} {topmost}";
    }));

    /// <summary>Prints whether the app is in a call, the windows auto-record would use, and every window of the app.</summary>
    public static void Print(IMeetingApp app, TextWriter output)
    {
        output.WriteLine($"app {app.Id} {app.DisplayName}");
        output.WriteLine(app.IsCallActive() ? "call active" : "call none");
        var meetings = app.FindMeetingWindows(onScreenOnly: false).ToDictionary(w => w.Handle, w => w.Title);
        var recordable = app.FindMeetingWindows();
        if (recordable.Count == 0)
            output.WriteLine("window none");
        foreach (var window in recordable)
            output.WriteLine($"window {window.Handle} {window.Title}");

        foreach (IntPtr hwnd in app.FindAppWindows())
        {
            string title = MeetingWindows.Title(hwnd);
            var bounds = MeetingWindows.Bounds(hwnd);
            long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
            IntPtr owner = GetWindow(hwnd, GW_OWNER);
            GetWindowDisplayAffinity(hwnd, out uint affinity);
            var flags = new List<string>();
            if ((exStyle & WS_EX_TOPMOST) != 0)
                flags.Add("topmost");
            if ((exStyle & WS_EX_TOOLWINDOW) != 0)
                flags.Add("toolwindow");
            if (owner != IntPtr.Zero)
                flags.Add($"owner={owner}");
            if (affinity != 0)
                flags.Add($"affinity=0x{affinity:X}");

            output.WriteLine($"app-window {hwnd} class={MeetingWindows.ClassName(hwnd)} title=\"{title}\"");
            output.WriteLine($"  meeting={(meetings.TryGetValue(hwnd, out string? name) ? $"\"{name}\"" : "no")}"
                + $" shown={MeetingWindows.NotShownReason(hwnd) ?? "yes"}"
                + $" rect={(bounds is { } b ? $"{b.X},{b.Y} {b.Width}x{b.Height}" : "none")}"
                + (flags.Count > 0 ? " " + string.Join(' ', flags) : ""));
            output.WriteLine($"  buttons: {Buttons(hwnd)}");
            output.Flush();
        }
    }

    /// <summary>The names of the first buttons in the window, or why they couldn't be read.</summary>
    private static string Buttons(IntPtr hwnd)
    {
        string result = "(timed out)";
        // UI Automation can hang on a busy window; the thread is abandoned when it does.
        var reader = new Thread(() =>
        {
            try
            {
                var buttons = AutomationElement.FromHandle(hwnd).FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                var names = buttons.Cast<AutomationElement>().Select(button => button.Current.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToList();
                result = names.Count == 0 ? "(none)"
                    : string.Join("; ", names.Take(MaxButtons)) + (names.Count > MaxButtons ? $"; ... ({names.Count} in all)" : "");
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
            {
                result = $"(unreadable: {ex.Message})";
            }
        }) { IsBackground = true };
        reader.SetApartmentState(ApartmentState.MTA);
        reader.Start();
        reader.Join(ButtonTimeout);
        return result;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);

    [DllImport("user32.dll")]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);
}
