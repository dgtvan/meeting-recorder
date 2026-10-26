namespace MeetingRecorder;

static class Program
{
    private const string AudioStatusFlag = "--audio-status";

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
        if (args.Length > 0 && args[0] == RecordingFinisher.Flag)
            return RecordingFinisher.Run(args);
        if (args.Length > 0 && args[0] == AudioStatusFlag)
        {
            // Diagnostic: every audio device, which apps use it, and which microphone a recording would use now.
            // Captures nothing and needs no data folder.
            Console.Out.WriteLine(AudioDevices.Describe());
            var choice = AudioDevices.ChooseMicrophone(null, null);
            Console.Out.WriteLine(choice == null ? "microphone none" : $"microphone {choice.DeviceName} ({choice.Reason})");
            return 0;
        }
        if (args.Length > 0 && args[0] == MeetingLog.StatusFlag)
        {
            // Diagnostic: what auto-record sees right now, for one meeting app (by id) or all. Captures nothing and
            // needs no data folder.
            var apps = MeetingApps.All.ToList();
            if (args.Length > 1)
            {
                if (MeetingApps.Find(args[1]) is not { } only)
                {
                    Console.Out.WriteLine($"unknown app {args[1]}; known: {string.Join(", ", apps.Select(a => a.Id))}");
                    return 2;
                }
                apps = [only];
            }
            foreach (var app in apps)
                MeetingWindowReport.Print(app, Console.Out);
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
