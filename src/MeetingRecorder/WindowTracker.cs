using System.Runtime.InteropServices;

namespace MeetingRecorder;

/// <summary>
/// Follows a window's on-screen rectangle so a display capture can be cropped to it. Used when
/// Windows Graphics Capture is unavailable (e.g. disabled by policy) and the window can't be captured directly.
/// </summary>
/// <remarks>
/// Moves are reported through a WinEvent hook rather than polling, so an idle window costs nothing.
/// Must be created on the UI thread: out-of-context hooks are delivered through its message loop.
/// </remarks>
sealed class WindowTracker : IDisposable
{
    private const uint EVENT_OBJECT_DESTROY = 0x8001;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    private readonly IntPtr _hwnd;
    private readonly WinEventProc _callback;
    private readonly IntPtr _moveHook;
    private readonly IntPtr _destroyHook;
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 250 };

    /// <summary>The display the window was on when tracking started; the crop stays on this display.</summary>
    public Screen Screen { get; }

    /// <summary>Raised on the UI thread with the new crop rectangle, at most every 250 ms while the window moves.</summary>
    public event Action<Rectangle>? CropChanged;

    /// <summary>Raised on the UI thread when the window is destroyed.</summary>
    public event Action? WindowClosed;

    public WindowTracker(IntPtr hwnd)
    {
        _hwnd = hwnd;
        Screen = Screen.FromHandle(hwnd);
        _callback = OnWinEvent;
        GetWindowThreadProcessId(hwnd, out uint processId);
        _moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _callback,
            processId, 0, WINEVENT_OUTOFCONTEXT);
        _destroyHook = SetWinEventHook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_DESTROY, IntPtr.Zero, _callback,
            processId, 0, WINEVENT_OUTOFCONTEXT);
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            if (GetCrop() is { } crop)
                CropChanged?.Invoke(crop);
        };
    }

    /// <summary>
    /// The window's visible area in the display's pixel coordinates, clipped to the display and rounded to even
    /// sizes for the H.264 encoder. Null when the window is gone or entirely off this display.
    /// </summary>
    public Rectangle? GetCrop()
    {
        if (DwmGetWindowAttribute(_hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            return null;

        var bounds = Screen.Bounds;
        var visible = Rectangle.Intersect(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), bounds);
        if (visible.Width < 2 || visible.Height < 2)
            return null;
        return new Rectangle(visible.X - bounds.X, visible.Y - bounds.Y, visible.Width & ~1, visible.Height & ~1);
    }

    public void Dispose()
    {
        _debounce.Dispose();
        if (_moveHook != IntPtr.Zero)
            UnhookWinEvent(_moveHook);
        if (_destroyHook != IntPtr.Zero)
            UnhookWinEvent(_destroyHook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd != _hwnd || idObject != OBJID_WINDOW)
            return;
        if (eventType == EVENT_OBJECT_DESTROY)
            WindowClosed?.Invoke();
        else if (!_debounce.Enabled)
            _debounce.Start();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);
}
