using System.Diagnostics;

namespace MeetingRecorder;

/// <summary>
/// One meeting app's entries in meetings.log in the data folder: what auto-record did during each call, plus a
/// <c>--meeting-status</c> snapshot of every window of the app whenever they change, so which window is which
/// (meeting, sharing toolbar, ...) can be worked out after the call. Each entry starts with the app's name.
/// </summary>
/// <remarks>
/// Snapshots run in a child process: reading button names loads UI Automation, which the tray shouldn't keep in
/// memory, and can take seconds on a busy window.
/// </remarks>
sealed class MeetingLog(IMeetingApp app)
{
    public const string StatusFlag = "--meeting-status";

    private string? _lastSignature;
    private Process? _snapshot;
    private bool _snapshotPending;

    public void Append(string text) => LogFile.Append(AppPaths.MeetingLogPath, $"[{app.DisplayName}] {text}");

    /// <summary>Takes a snapshot when the app's windows changed since the last one. Call on the UI thread.</summary>
    public void SnapshotIfChanged()
    {
        string signature;
        try
        {
            signature = MeetingWindowReport.Signature(app);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return;
        }
        if (signature == _lastSignature)
            return;
        _lastSignature = signature;
        if (_snapshot != null)
        {
            _snapshotPending = true;
            return;
        }
        StartSnapshot();
    }

    /// <summary>Forgets the last snapshot, so the next call starts with a fresh one.</summary>
    public void Reset() => _lastSignature = null;

    private void StartSnapshot()
    {
        var ui = SynchronizationContext.Current!;
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(Environment.ProcessPath!, $"{StatusFlag} {app.Id}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            },
            EnableRaisingEvents = true,
        };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Append($"Window snapshot failed: {ex.Message}");
            process.Dispose();
            return;
        }
        _snapshot = process;
        Task.Run(async () =>
        {
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            ui.Post(_ => OnSnapshotDone(process, output), null);
        });
    }

    private void OnSnapshotDone(Process process, string output)
    {
        process.Dispose();
        _snapshot = null;
        LogFile.Append(AppPaths.MeetingLogPath, $"[{app.DisplayName}] Windows:\n{output}", multiline: true);
        if (_snapshotPending)
        {
            _snapshotPending = false;
            StartSnapshot();
        }
    }
}
