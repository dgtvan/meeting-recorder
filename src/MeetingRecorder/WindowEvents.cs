using System.Runtime.InteropServices;

namespace MeetingRecorder;

/// <summary>
/// Reports, the moment it happens, that a top-level window of any app was shown, hidden, minimized or restored: what
/// starts and ends a screen share. Must be created on the UI thread: out-of-context hooks are delivered through its
/// message loop, and so is <see cref="Changed"/>.
/// </summary>
sealed class WindowEvents : IDisposable
{
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;
    private const uint GA_ROOT = 2;

    private readonly WinEventProc _callback;
    private readonly List<IntPtr> _hooks = [];
    private bool _disposed;

    public event Action? Changed;

    public WindowEvents()
    {
        _callback = OnWinEvent;
        Hook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (IntPtr hook in _hooks)
            UnhookWinEvent(hook);
    }

    private void Hook(uint eventMin, uint eventMax)
    {
        IntPtr hook = SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _callback, 0, 0, WINEVENT_OUTOFCONTEXT);
        if (hook != IntPtr.Zero)
            _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_disposed && idObject == OBJID_WINDOW && idChild == 0 && hwnd != IntPtr.Zero && GetAncestor(hwnd, GA_ROOT) == hwnd)
            Changed?.Invoke();
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
}
