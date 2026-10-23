using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace MeetingRecorder;

/// <summary>
/// Shows that a window is being recorded: a red border just outside the window and, when <see cref="ShowLabel"/> is
/// set, a "Recording" label with a pulsing dot in the border's top-left corner, inside it. Replaces the Windows Graphics Capture border, which Windows hides and
/// redraws on its own and so flashes.
/// </summary>
/// <remarks>
/// Two click-through layered popups that never take focus, kept directly in front of the target in the Z-order so
/// windows covering the target cover them too. They follow the target through WinEvent hooks, plus a slow timer
/// for events Windows drops. Both are excluded from every screen capture, so they never show in a recording or a
/// screen share. Must be created on the UI thread: out-of-context hooks are delivered through its message loop.
/// </remarks>
sealed class RecordingOverlay : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
    private const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint EVENT_OBJECT_HIDE = 0x8003;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    private const uint EVENT_OBJECT_CLOAKED = 0x8017;
    private const uint EVENT_OBJECT_UNCLOAKED = 0x8018;
    private const uint WINEVENT_OUTOFCONTEXT = 0;
    private const int OBJID_WINDOW = 0;

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const int DWMWA_VISIBLE_FRAME_BORDER_THICKNESS = 37;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x8;
    private const uint GW_HWNDPREV = 3;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    private static readonly Color BorderColor = Color.FromArgb(232, 17, 35);
    private static readonly Color LabelTextColor = Color.White;
    private const string LabelText = "Recording";
    private const int BorderThickness = 3;
    private const int PulseFrames = 16;
    private const int PulseFrameMs = 90;
    // static readonly rather than const so the disabled branches don't raise unreachable-code warnings.
    private static readonly bool ShowLabel = false;

    private readonly IntPtr _target;
    private readonly BorderWindow _border = new(BorderColor);
    private readonly LayeredWindow? _label = ShowLabel ? new() : null;
    private readonly WinEventProc _callback;
    private readonly List<IntPtr> _hooks = [];
    private readonly System.Windows.Forms.Timer _rescan = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer _pulse = new() { Interval = PulseFrameMs };

    private IntPtr[] _frames = [];
    private Size _labelSize;
    private int _framesDpi;
    private int _frame;
    private Point _labelPosition;
    private bool _visible;
    private bool _disposed;

    public RecordingOverlay(IntPtr target)
    {
        _target = target;
        _callback = OnWinEvent;
        GetWindowThreadProcessId(target, out uint processId);
        Hook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, processId);
        Hook(EVENT_OBJECT_SHOW, EVENT_OBJECT_HIDE, processId);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED, processId);
        Hook(EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND, processId);
        // Activating any other window is a pure Z-order change that LOCATIONCHANGE doesn't reliably report.
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, 0);

        _rescan.Tick += (_, _) => Sync();
        _pulse.Tick += (_, _) =>
        {
            _frame = (_frame + 1) % PulseFrames;
            ShowLabelFrame();
        };
        _rescan.Start();
        Sync();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (IntPtr hook in _hooks)
            UnhookWinEvent(hook);
        _rescan.Dispose();
        _pulse.Dispose();
        _border.DestroyHandle();
        _label?.Dispose();
        DeleteFrames();
    }

    private void Hook(uint eventMin, uint eventMax, uint processId)
    {
        IntPtr hook = SetWinEventHook(eventMin, eventMax, IntPtr.Zero, _callback, processId, 0, WINEVENT_OUTOFCONTEXT);
        if (hook != IntPtr.Zero)
            _hooks.Add(hook);
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed)
            return;
        if (eventType == EVENT_SYSTEM_FOREGROUND)
        {
            if (_visible)
                Restack();
        }
        else if (hwnd == _target && idObject == OBJID_WINDOW)
        {
            Sync();
        }
    }

    /// <summary>Matches the overlay to the target's current position, size, visibility and Z-order.</summary>
    private void Sync()
    {
        if (!IsOnScreen() || DwmGetWindowAttribute(_target, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT frame, Marshal.SizeOf<RECT>()) != 0)
        {
            Hide();
            return;
        }

        int dpi = (int)GetDpiForWindow(_target);
        if (dpi == 0)
            dpi = 96;
        int thickness = Math.Max(2, BorderThickness * dpi / 96);
        Rectangle monitor = MonitorBounds();

        // Kept on the target's monitor: a maximized window's border is drawn just inside its edges instead of
        // spilling onto the next display.
        var outer = Rectangle.FromLTRB(frame.Left, frame.Top, frame.Right, frame.Bottom);
        // Windows 11 draws its own thin border inside the frame bounds, which looks like a gap next to ours (dark in
        // apps like Teams), so ours covers it.
        int windowsBorder = DwmGetWindowAttribute(_target, DWMWA_VISIBLE_FRAME_BORDER_THICKNESS, out int visibleBorder, sizeof(int)) == 0
            ? visibleBorder : 0;
        outer.Inflate(thickness - windowsBorder, thickness - windowsBorder);
        outer.Intersect(monitor);
        if (outer.Width <= 2 * thickness || outer.Height <= 2 * thickness)
        {
            Hide();
            return;
        }

        if (_label != null && dpi != _framesDpi)
            RenderLabelFrames(dpi);

        _border.SetBounds(outer, thickness);
        // A tab hanging from the border into the window, like a title; it covers the window's own corner.
        _labelPosition = new Point(outer.Left, outer.Top);
        ShowLabelFrame();

        if (!_visible)
        {
            SetWindowPos(_border.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _visible = true;
            if (_label != null)
            {
                SetWindowPos(_label.Handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                _pulse.Start();
            }
        }
        Restack();
    }

    private bool IsOnScreen()
    {
        if (!IsWindow(_target) || IsIconic(_target) || !IsWindowVisible(_target))
            return false;
        // Cloaked windows (another virtual desktop, some Teams states) are "visible" to Win32 but not on screen.
        return DwmGetWindowAttribute(_target, DWMWA_CLOAKED, out int cloaked, sizeof(int)) != 0 || cloaked == 0;
    }

    private void Hide()
    {
        if (!_visible)
            return;
        _visible = false;
        _pulse.Stop();
        ShowWindow(_border.Handle, 0);
        if (_label != null)
            ShowWindow(_label.Handle, 0);
    }

    /// <summary>
    /// Puts the label (if shown), then the border, directly in front of the target. Skipped when already there, since every
    /// move would otherwise reorder windows.
    /// </summary>
    private void Restack()
    {
        IntPtr above = GetWindow(_target, GW_HWNDPREV);
        if (above == _border.Handle && (_label == null || GetWindow(_border.Handle, GW_HWNDPREV) == _label.Handle))
            return;
        while (above == _border.Handle || (_label != null && above == _label.Handle))
            above = GetWindow(above, GW_HWNDPREV);

        // SetWindowPos inserts *behind* insertAfter. HWND_TOP is the top of the target's own band (topmost or not),
        // which is where the target is when nothing in its band is in front of it.
        bool targetTopmost = IsTopmost(_target);
        IntPtr insertAfter = above == IntPtr.Zero || IsTopmost(above) != targetTopmost
            ? (targetTopmost ? HWND_TOPMOST : HWND_TOP)
            : above;
        const uint flags = SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE;
        SetWindowPos(_border.Handle, insertAfter, 0, 0, 0, 0, flags);
        if (_label != null)
            SetWindowPos(_label.Handle, insertAfter, 0, 0, 0, 0, flags);
    }

    private static bool IsTopmost(IntPtr hwnd) => (GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;

    private Rectangle MonitorBounds()
    {
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(_target, MONITOR_DEFAULTTONEAREST), ref info);
        return Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);
    }

    private void ShowLabelFrame()
    {
        if (_label != null && _frames.Length > 0)
            _label.Show(_frames[_frame], _labelPosition, _labelSize);
    }

    /// <summary>Draws every step of the dot's pulse once per DPI, so the animation only swaps bitmaps.</summary>
    private void RenderLabelFrames(int dpi)
    {
        DeleteFrames();
        float scale = dpi / 96f;
        using var font = new Font("Segoe UI Semibold", 12 * scale, GraphicsUnit.Pixel);
        int height = (int)Math.Round(22 * scale);
        int padding = (int)Math.Round(8 * scale);
        int dot = (int)Math.Round(8 * scale);
        int gap = (int)Math.Round(6 * scale);
        Size text = TextRenderer.MeasureText(LabelText, font, Size.Empty, TextFormatFlags.NoPadding);
        _labelSize = new Size(padding + dot + gap + text.Width + padding, height);

        _frames = new IntPtr[PulseFrames];
        using var textBrush = new SolidBrush(LabelTextColor);
        for (int i = 0; i < PulseFrames; i++)
        {
            double alpha = 0.25 + 0.75 * (0.5 + 0.5 * Math.Cos(2 * Math.PI * i / PulseFrames));
            using var bitmap = new Bitmap(_labelSize.Width, _labelSize.Height);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(BorderColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using var dotBrush = new SolidBrush(Color.FromArgb((int)(255 * alpha), LabelTextColor));
                g.FillEllipse(dotBrush, padding, (height - dot) / 2f, dot, dot);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.DrawString(LabelText, font, textBrush, new RectangleF(padding + dot + gap, 0, text.Width + padding, height),
                    new StringFormat { LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap });
            }
            _frames[i] = bitmap.GetHbitmap(Color.FromArgb(0));
        }
        _framesDpi = dpi;
    }

    private void DeleteFrames()
    {
        foreach (IntPtr frame in _frames)
            DeleteObject(frame);
        _frames = [];
    }

    private static CreateParams OverlayParams() => new()
    {
        Style = unchecked((int)0x80000000), // WS_POPUP
        // WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE: click-through, no taskbar button.
        ExStyle = 0x00080000 | 0x00000020 | 0x00000080 | 0x08000000,
    };

    /// <summary>A solid-colour frame: a layered popup clipped to a hollow rectangle.</summary>
    private sealed class BorderWindow : NativeWindow
    {
        private const int WM_PAINT = 0x000F;
        private const int WM_ERASEBKGND = 0x0014;
        private readonly IntPtr _brush;
        private Size _size;
        private int _thickness;

        public BorderWindow(Color color)
        {
            _brush = CreateSolidBrush(ColorTranslator.ToWin32(color));
            CreateHandle(OverlayParams());
            SetLayeredWindowAttributes(Handle, 0, 255, 0x2); // LWA_ALPHA, opaque
            ExcludeFromCapture(Handle);
        }

        public void SetBounds(Rectangle bounds, int thickness)
        {
            SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE);
            if (bounds.Size == _size && thickness == _thickness)
                return;
            _size = bounds.Size;
            _thickness = thickness;
            IntPtr region = CreateRectRgn(0, 0, bounds.Width, bounds.Height);
            IntPtr hole = CreateRectRgn(thickness, thickness, bounds.Width - thickness, bounds.Height - thickness);
            CombineRgn(region, region, hole, 4); // RGN_DIFF
            DeleteObject(hole);
            SetWindowRgn(Handle, region, true); // the window owns the region from here on
        }

        public override void DestroyHandle()
        {
            base.DestroyHandle();
            DeleteObject(_brush);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = 1;
                return;
            }
            if (m.Msg == WM_PAINT)
            {
                IntPtr dc = BeginPaint(Handle, out PAINTSTRUCT ps);
                FillRect(dc, ref ps.rcPaint, _brush);
                EndPaint(Handle, ref ps);
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>A popup whose pixels are set with UpdateLayeredWindow.</summary>
    private sealed class LayeredWindow : NativeWindow, IDisposable
    {
        private readonly IntPtr _memoryDc = CreateCompatibleDC(IntPtr.Zero);

        public LayeredWindow()
        {
            CreateHandle(OverlayParams());
            ExcludeFromCapture(Handle);
        }

        public void Show(IntPtr bitmap, Point position, Size size)
        {
            IntPtr old = SelectObject(_memoryDc, bitmap);
            var dst = new POINT { X = position.X, Y = position.Y };
            var sz = new SIZE { cx = size.Width, cy = size.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_ALPHA
            UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref sz, _memoryDc, ref src, 0, ref blend, 0x2); // ULW_ALPHA
            SelectObject(_memoryDc, old);
        }

        public void Dispose()
        {
            DestroyHandle();
            DeleteDC(_memoryDc);
        }
    }

    private static void ExcludeFromCapture(IntPtr hwnd) => SetWindowDisplayAffinity(hwnd, 0x11); // WDA_EXCLUDEFROMCAPTURE

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public bool fErase;
        public RECT rcPaint;
        public bool fRestore, fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
    }

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback,
        uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc,
        ref POINT src, uint colorKey, ref BLENDFUNCTION blend, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr dc, ref RECT rect, IntPtr brush);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(int color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
}
