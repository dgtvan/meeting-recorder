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
    // A second digit wouldn't be readable at 16 px.
    private const int MaxShownCount = 9;
    private static readonly Dictionary<(int Count, bool Warning), Icon[]> s_recording = [];

    public static Icon Idle { get; } = Create(g => DrawDot(g, Color.FromArgb(150, 150, 150), Color.FromArgb(90, 90, 90)));

    /// <summary>
    /// Recording: the red dot fading in and out, one icon per animation frame. With more than one recording running
    /// the dot fills the ring and shows how many in white. <paramref name="warning"/> adds a steady warning badge for
    /// something that needs attention.
    /// </summary>
    public static Icon[] Recording(int count, bool warning)
    {
        count = Math.Clamp(count, 1, MaxShownCount);
        if (!s_recording.TryGetValue((count, warning), out Icon[]? frames))
        {
            frames = Pulse(dot => g =>
            {
                if (count == 1)
                {
                    DrawDot(g, dot, Color.White);
                }
                else
                {
                    DrawDot(g, dot, Color.White, insetRatio: 0.17f);
                    DrawDigit(g, count);
                }
                if (warning)
                {
                    float badge = Size * 0.52f;
                    DrawTriangle(g, new RectangleF(Size - badge, Size - badge, badge, badge));
                }
            });
            s_recording[(count, warning)] = frames;
        }
        return frames;
    }

    /// <summary>Frames per pulse of <see cref="Recording"/>.</summary>
    public static int RecordingFrames => PulseFrames;

    public static Icon Starting { get; } = Create(g => DrawDot(g, Color.FromArgb(245, 165, 0), Color.White));

    /// <summary>Saving a recording: a blue spinner, one icon per animation frame.</summary>
    public static Icon[] Processing { get; } = Enumerable.Range(0, 8)
        .Select(frame => Create(g => DrawSpinner(g, frame * 45f)))
        .ToArray();

    /// <summary>Needs attention while idle: a large warning triangle.</summary>
    public static Icon Warning { get; } = Create(g => DrawTriangle(g, new RectangleF(0, 0, Size, Size)));

    private static int Size => SystemInformation.SmallIconSize.Width;

    private static void DrawDot(Graphics g, Color dot, Color ring, float insetRatio = 0.25f)
    {
        int size = Size;
        float stroke = Math.Max(1.5f, size / 10f);
        using (var pen = new Pen(ring, stroke))
            g.DrawEllipse(pen, stroke / 2, stroke / 2, size - stroke - 0.5f, size - stroke - 0.5f);

        float inset = size * insetRatio;
        using var brush = new SolidBrush(dot);
        g.FillEllipse(brush, inset, inset, size - inset * 2 - 0.5f, size - inset * 2 - 0.5f);
    }

    /// <summary>A bold white digit in the middle of the icon.</summary>
    private static void DrawDigit(Graphics g, int digit)
    {
        using var path = new GraphicsPath();
        using (var family = new FontFamily("Segoe UI"))
            path.AddString(digit.ToString(), family, (int)FontStyle.Bold, 100, PointF.Empty, StringFormat.GenericTypographic);
        RectangleF bounds = path.GetBounds();
        float scale = Size * 0.42f / bounds.Height;
        float center = (Size - 0.5f) / 2;
        using var matrix = new Matrix();
        matrix.Translate(center - (bounds.X + bounds.Width / 2) * scale, center - (bounds.Y + bounds.Height / 2) * scale);
        matrix.Scale(scale, scale);
        path.Transform(matrix);
        using var brush = new SolidBrush(Color.White);
        g.FillPath(brush, path);
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
