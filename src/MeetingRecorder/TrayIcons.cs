using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MeetingRecorder;

/// <summary>
/// Tray icons drawn at startup so the app ships without image assets.
/// </summary>
static class TrayIcons
{
    private static readonly Color WarningFill = Color.FromArgb(255, 196, 0);
    private static readonly Color RecordingFill = Color.FromArgb(230, 30, 30);
    private const int PulseFrames = 12;

    public static Icon Idle { get; } = Create(g => DrawDot(g, Color.FromArgb(150, 150, 150), Color.FromArgb(90, 90, 90)));

    /// <summary>Recording: the red dot fading in and out, one icon per animation frame.</summary>
    public static Icon[] Recording { get; } = Pulse(dot => g => DrawDot(g, dot, Color.White));

    public static Icon Starting { get; } = Create(g => DrawDot(g, Color.FromArgb(245, 165, 0), Color.White));

    /// <summary>Saving a recording: a blue spinner, one icon per animation frame.</summary>
    public static Icon[] Processing { get; } = Enumerable.Range(0, 8)
        .Select(frame => Create(g => DrawSpinner(g, frame * 45f)))
        .ToArray();

    /// <summary>Needs attention while idle: a large warning triangle.</summary>
    public static Icon Warning { get; } = Create(g => DrawTriangle(g, new RectangleF(0, 0, Size, Size)));

    /// <summary>Needs attention while still recording: the pulsing red dot with a steady warning badge.</summary>
    public static Icon[] RecordingWarning { get; } = Pulse(dot => g =>
    {
        DrawDot(g, dot, Color.White);
        float badge = Size * 0.62f;
        DrawTriangle(g, new RectangleF(Size - badge, Size - badge, badge, badge));
    });

    private static int Size => SystemInformation.SmallIconSize.Width;

    private static void DrawDot(Graphics g, Color dot, Color ring)
    {
        int size = Size;
        float stroke = Math.Max(1.5f, size / 10f);
        using (var pen = new Pen(ring, stroke))
            g.DrawEllipse(pen, stroke / 2, stroke / 2, size - stroke - 0.5f, size - stroke - 0.5f);

        float inset = size * 0.25f;
        using var brush = new SolidBrush(dot);
        g.FillEllipse(brush, inset, inset, size - inset * 2 - 0.5f, size - inset * 2 - 0.5f);
    }

    /// <summary>One icon per frame of a pulse whose recording dot fades between 30% and 100% opacity.</summary>
    private static Icon[] Pulse(Func<Color, Action<Graphics>> draw) => Enumerable.Range(0, PulseFrames)
        .Select(frame =>
        {
            double alpha = 0.3 + 0.7 * (0.5 + 0.5 * Math.Cos(2 * Math.PI * frame / PulseFrames));
            return Create(draw(Color.FromArgb((int)(255 * alpha), RecordingFill)));
        })
        .ToArray();

    private static void DrawSpinner(Graphics g, float startAngle)
    {
        int size = Size;
        float stroke = Math.Max(2f, size / 6f);
        var bounds = new RectangleF(stroke / 2 + 0.5f, stroke / 2 + 0.5f, size - stroke - 1.5f, size - stroke - 1.5f);
        using (var track = new Pen(Color.FromArgb(200, 200, 200), stroke))
            g.DrawEllipse(track, bounds);
        using var arc = new Pen(Color.FromArgb(0, 120, 215), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawArc(arc, bounds, startAngle, 120);
    }

    private static void DrawTriangle(Graphics g, RectangleF area)
    {
        var points = new[]
        {
            new PointF(area.Left + area.Width / 2, area.Top + 0.5f),
            new PointF(area.Right - 0.5f, area.Bottom - 0.5f),
            new PointF(area.Left + 0.5f, area.Bottom - 0.5f),
        };
        using (var fill = new SolidBrush(WarningFill))
            g.FillPolygon(fill, points);
        using (var outline = new Pen(Color.Black, Math.Max(1f, area.Width / 16f)))
            g.DrawPolygon(outline, points);

        float barWidth = Math.Max(1.5f, area.Width / 8f);
        float centerX = area.Left + area.Width / 2;
        using var mark = new SolidBrush(Color.Black);
        g.FillRectangle(mark, centerX - barWidth / 2, area.Top + area.Height * 0.32f, barWidth, area.Height * 0.36f);
        g.FillRectangle(mark, centerX - barWidth / 2, area.Top + area.Height * 0.75f, barWidth, barWidth);
    }

    private static Icon Create(Action<Graphics> draw)
    {
        using var bitmap = new Bitmap(Size, Size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            draw(g);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
