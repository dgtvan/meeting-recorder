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
            // Diagnostic: what Teams auto-record would see right now. Captures nothing and needs no data folder.
            Console.Out.WriteLine(TeamsMeeting.IsCallActive() ? "call active" : "call none");
            Console.Out.WriteLine(TeamsMeeting.FindMeetingWindow() is { } window ? $"window {window.Handle} {window.Title}" : "window none");
            return 0;
        }

        using var mutex = new Mutex(true, @"Local\MeetingRecorder-SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance)
            return 0;

        ApplicationConfiguration.Initialize();
        if (!AppPaths.TryInitialize(args))
        {
            MessageBox.Show("Meeting Recorder was started without --data-dir. Start it from the Start Menu shortcut.",
                "Meeting Recorder", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        // Errors in UI callbacks become a tray warning instead of a crash.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.Run(new TrayApp());
        return 0;
    }
}
