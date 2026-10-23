namespace MeetingRecorder;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == RecorderWorker.Flag)
        {
            ApplicationConfiguration.Initialize();
            return RecorderWorker.Run(args);
        }
        if (args.Length > 0 && args[0] == "--teams-status")
        {
            // Diagnostic: what Teams auto-record would see right now. Captures nothing.
            Console.Out.WriteLine(TeamsMeeting.IsCallActive() ? "call active" : "call none");
            Console.Out.WriteLine(TeamsMeeting.FindMeetingWindow() is { } window ? $"window {window.Handle} {window.Title}" : "window none");
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\MeetingRecorder-SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
        {
            MessageBox.Show("Meeting Recorder is already running in the system tray.", "Meeting Recorder",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();
        // Errors in UI callbacks become a tray warning instead of a crash.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.Run(new TrayApp());
        return 0;
    }
}
