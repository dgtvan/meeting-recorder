using System.Diagnostics;

namespace MeetingRecorder;

/// <summary>
/// Tray-side handle to a recording running in a <see cref="RecorderWorker"/> child process.
/// </summary>
/// <remarks>All events are raised on the UI thread.</remarks>
sealed class RecordingProcess
{
    /// <summary>Remembered across recordings so later window recordings skip the failing capture API.</summary>
    private static bool s_skipGraphicsCapture;

    private readonly SynchronizationContext _ui;
    private readonly Process _process;
    private string? _error;
    private bool _finishedReported;

    public string TargetName { get; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public string? FolderPath { get; private set; }

    /// <summary>Why the recording ended on its own when nothing went wrong, e.g. the window was closed.</summary>
    public string? EndNote { get; private set; }

    /// <summary>Raised once video and audio are capturing.</summary>
    public event Action<RecordingProcess>? Started;

    /// <summary>Raised when the worker starts saving the files, whether stopped or ended on its own.</summary>
    public event Action<RecordingProcess>? Stopping;

    /// <summary>Raised as soon as the worker reports something the user should act on.</summary>
    public event Action<RecordingProcess, string>? Warning;

    /// <summary>Raised once the worker has exited. The argument is an error message, or null on success.</summary>
    public event Action<RecordingProcess, string?>? Finished;

    public RecordingProcess(SynchronizationContext ui, RecordingTarget target)
    {
        _ui = ui;
        TargetName = target.Name;
        _process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!, RecorderWorker.BuildArguments(target, s_skipGraphicsCapture))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
            },
            EnableRaisingEvents = true,
        };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) _ui.Post(_ => OnLine(e.Data), null); };
        // Exited can fire before the last output lines are processed; WaitForExit() drains them first.
        _process.Exited += (_, _) =>
        {
            _process.WaitForExit();
            _ui.Post(_ => OnExited(), null);
        };
    }

    public void Start()
    {
        _process.Start();
        _process.BeginOutputReadLine();
    }

    public void Stop()
    {
        try
        {
            _process.StandardInput.WriteLine("stop");
            _process.StandardInput.Flush();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
        }
    }

    /// <summary>Last resort when the worker stops responding; fragmented MP4 keeps what was written playable.</summary>
    public void Kill(string reason)
    {
        _error ??= reason;
        try
        {
            _process.Kill();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void OnLine(string line)
    {
        int space = line.IndexOf(' ');
        string kind = space < 0 ? line : line[..space];
        string text = space < 0 ? "" : line[(space + 1)..];
        switch (kind)
        {
            case "folder":
                FolderPath = text;
                break;
            case "started":
                Started?.Invoke(this);
                break;
            case "stopping":
                Stopping?.Invoke(this);
                break;
            case "fallback":
                s_skipGraphicsCapture = true;
                break;
            case "warning":
                Warning?.Invoke(this, text);
                break;
            case "note":
                EndNote = text;
                break;
            case "failed":
                _error = text;
                break;
        }
    }

    private void OnExited()
    {
        if (_finishedReported)
            return;
        _finishedReported = true;
        int code = _process.ExitCode;
        if (code != 0 && _error == null)
            _error = $"The recorder process stopped unexpectedly (exit code {code}).";
        _process.Dispose();
        Finished?.Invoke(this, _error);
    }
}
