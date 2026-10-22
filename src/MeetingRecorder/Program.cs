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
