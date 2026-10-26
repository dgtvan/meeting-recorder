using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MeetingRecorder;

/// <summary>A running program, as recorded in logs and in a recording's README.md.</summary>
sealed record AppProcess(uint Id, string Name)
{
    public override string ToString() => $"{Name}.exe (pid {Id})";
}

/// <summary>
/// Which process owns a window, and which processes are its children: an app's sound and microphone use often come from
/// a child process (a browser's audio service, Teams' web views), so they count as the app's.
/// </summary>
static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x2;
    private const string FrameHostName = "ApplicationFrameHost";

    /// <summary>
    /// The program behind a window. Store apps draw inside a window of ApplicationFrameHost, a host shared by all of
    /// them; their own process owns a child window, so that one is used. Null when the program can't be told apart.
    /// </summary>
    public static AppProcess? ForWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == 0 || Describe(pid) is not { } owner)
            return null;
        if (!owner.Name.Equals(FrameHostName, StringComparison.OrdinalIgnoreCase))
            return owner;
        AppProcess? inner = null;
        EnumChildWindows(hwnd, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint childPid);
            if (childPid != pid && Describe(childPid) is { } found)
            {
                inner = found;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return inner;
    }

    /// <summary>The process with this id, or null when it has exited.</summary>
    public static AppProcess? Describe(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return new AppProcess(pid, process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Every running process's parent, by process id. A process whose parent has exited keeps its old id.</summary>
    public static Dictionary<uint, uint> Parents()
    {
        var parents = new Dictionary<uint, uint>();
        IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == new IntPtr(-1))
            return parents;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                parents[entry.th32ProcessID] = entry.th32ParentProcessID;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return parents;
    }

    /// <summary>True when <paramref name="pid"/> is <paramref name="root"/> or one of its descendants.</summary>
    public static bool IsInTree(uint pid, uint root, Dictionary<uint, uint> parents)
    {
        // Bounded: process ids are reused, so a stale parent link could otherwise loop.
        for (int depth = 0; depth < 32; depth++)
        {
            if (pid == root)
                return true;
            if (pid == 0 || !parents.TryGetValue(pid, out uint parent) || parent == pid)
                return false;
            pid = parent;
        }
        return false;
    }

    /// <summary>
    /// The processes to record so that every process of <paramref name="app"/>'s tree in <paramref name="playing"/> is
    /// heard: <paramref name="app"/> itself, plus the parent of each one that isn't covered yet. Windows' process
    /// loopback takes a process "and its child processes", but only direct children are included (measured: a
    /// grandchild's sound is missing), while apps play from deeper down, e.g. Teams' web views.
    /// </summary>
    public static List<uint> LoopbackRoots(uint app, IEnumerable<uint> playing, Dictionary<uint, uint> parents)
    {
        var roots = new List<uint> { app };
        foreach (uint pid in playing.Where(p => IsInTree(p, app, parents)).Order())
        {
            uint parent = parents.GetValueOrDefault(pid);
            if (roots.Contains(pid) || roots.Contains(parent))
                continue;
            roots.Add(IsInTree(parent, app, parents) ? parent : pid);
        }
        return roots;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
}
