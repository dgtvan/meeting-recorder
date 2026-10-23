namespace MeetingRecorder;

/// <summary>
/// Entry point of the child process that performs one recording (<c>MeetingRecorder.exe --record ...</c>).
/// </summary>
/// <remarks>
/// The native capture and encoder stack keeps a few hundred MB after a recording ends, so each recording
/// runs in its own process and the tray process stays small. The protocol is one line per message:
/// the tray writes "stop" to stdin (EOF also stops), the worker answers on stdout with
/// "folder &lt;path&gt;", "started", any number of "warning &lt;text&gt;" (sent as they happen),
/// "stopping" when the files start being saved,
/// then "fallback" and "note &lt;text&gt;" if they apply, and finally "finished" or "failed &lt;text&gt;".
/// </remarks>
static class RecorderWorker
{
    public const string Flag = "--record";

    public static int Run(string[] args)
    {
        RecordingTarget target;
        try
        {
            target = ParseTarget(args);
        }
        catch (Exception ex)
        {
            Send("failed", $"Bad arguments: {ex.Message}");
            return 2;
        }

        if (args.Contains("--crop"))
            RecordingSession.GraphicsCaptureUnavailable = true;

        // A hidden control installs the WinForms SynchronizationContext that the session posts events to.
        using var anchor = new Control();
        _ = anchor.Handle;
        var ui = SynchronizationContext.Current!;
        var context = new ApplicationContext();
        int exitCode = 0;

        RecordingSession session;
        try
        {
            session = RecordingSession.Create(ui, AppSettings.Load(), target);
        }
        catch (Exception ex)
        {
            Send("failed", ex.Message);
            return 1;
        }

        Send("folder", session.FolderPath);
        session.Started += _ => Send("started");
        session.Warning += warning => Send("warning", warning);
        session.Stopping += () => Send("stopping");
        session.Finished += (_, error) =>
        {
            if (RecordingSession.GraphicsCaptureUnavailable)
                Send("fallback");
            if (session.EndNote != null)
                Send("note", session.EndNote);
            if (error == null)
            {
                Send("finished");
            }
            else
            {
                Send("failed", error);
                exitCode = 1;
            }
            context.ExitThread();
        };

        var stdinReader = new Thread(() =>
        {
            string? line;
            while ((line = Console.In.ReadLine()) != null && line != "stop")
            {
            }
            ui.Post(_ => session.Stop(), null);
        }) { IsBackground = true };

        try
        {
            session.Start();
        }
        catch (Exception ex)
        {
            session.DeleteFolderIfEmpty();
            Send("failed", ex.Message);
            return 1;
        }

        stdinReader.Start();
        Application.Run(context);
        return exitCode;
    }

    /// <summary>Builds the worker command line for a target.</summary>
    public static string BuildArguments(RecordingTarget target, bool skipGraphicsCapture)
    {
        string args = target.WindowHandle != IntPtr.Zero
            ? $"{Flag} --window {target.WindowHandle.ToInt64()}"
            : $"{Flag} --display \"{target.DisplayDeviceName}\"";
        args += $" --name \"{target.Name.Replace("\"", "'")}\"";
        return skipGraphicsCapture ? args + " --crop" : args;
    }

    private static RecordingTarget ParseTarget(string[] args)
    {
        string? Value(string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        string name = Value("--name") ?? throw new ArgumentException("--name is required");
        if (Value("--file") is { } file)
            return new RecordingTarget(name, IntPtr.Zero, null, Path.GetFullPath(file));
        if (Value("--window") is { } hwnd)
            return new RecordingTarget(name, new IntPtr(long.Parse(hwnd)), null);
        if (Value("--display") is { } display)
            return new RecordingTarget(name, IntPtr.Zero, display);
        throw new ArgumentException("--window, --display or --file is required");
    }

    private static void Send(string kind, string? text = null)
    {
        Console.Out.WriteLine(text == null ? kind : $"{kind} {text.ReplaceLineEndings(" | ")}");
        Console.Out.Flush();
    }
}
