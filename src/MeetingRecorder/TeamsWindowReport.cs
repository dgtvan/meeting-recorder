using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace MeetingRecorder;

/// <summary>
/// Diagnostic for <c>--teams-status</c>: describes every visible Teams window, to learn which one is the meeting,
/// the sharing toolbar, and so on. Its meeting windows all share one title, so it reports what tells them apart:
/// size and position, window styles, whether Teams hides it from capture, and the buttons it shows (read through
/// UI Automation, the accessibility interface screen readers use). Captures nothing.
/// </summary>
static class TeamsWindowReport
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const uint GW_OWNER = 4;
    private const int MaxButtons = 40;
    private static readonly TimeSpan ButtonTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A cheap summary of the Teams windows (no UI Automation) that changes when a window opens or closes, changes
    /// title, size, or whether it's shown or on top, but not when it only moves.
    /// </summary>
    public static string Signature() => string.Join("\n", TeamsMeeting.FindTeamsWindows().Select(hwnd =>
    {
        var size = TeamsMeeting.Bounds(hwnd)?.Size;
        bool topmost = (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
        return $"{hwnd} {TeamsMeeting.Title(hwnd)} {size} {TeamsMeeting.NotShownReason(hwnd)} {topmost}";
    }));

    public static void Print(TextWriter output)
    {
        foreach (IntPtr hwnd in TeamsMeeting.FindTeamsWindows())
        {
            string title = TeamsMeeting.Title(hwnd);
            var bounds = TeamsMeeting.Bounds(hwnd);
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

            output.WriteLine($"teams-window {hwnd} class={TeamsMeeting.ClassName(hwnd)} title=\"{title}\"");
            output.WriteLine($"  meeting={(TeamsMeeting.MeetingName(title) is { } name ? $"\"{name}\"" : "no")}"
                + $" shown={TeamsMeeting.NotShownReason(hwnd) ?? "yes"}"
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
