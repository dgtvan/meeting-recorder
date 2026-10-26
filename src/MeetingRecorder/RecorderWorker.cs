namespace MeetingRecorder;

/// <summary>
/// Entry point of the child process that performs one recording (<c>MeetingRecorder.exe --record ...</c>).
/// </summary>
/// <remarks>
/// The native capture and encoder stack keeps a few hundred MB after a recording ends, so each recording
/// runs in its own process and the tray process stays small. The protocol is one line per message:
/// the tray writes "stop" to stdin (EOF also stops), the worker answers on stdout with
/// "folder &lt;path&gt;", "started", any number of "warning &lt;text&gt;" (sent as they happen) and "log &lt;text&gt;"
/// (for the audio log: sources, switches, device changes),
/// "stopping" when the files start being saved,
/// then "fallback" and "note &lt;text&gt;" if they apply, and finally "finished" or "failed &lt;text&gt;".
/// </remarks>
static class RecorderWorker
{
    public const string Flag = "--record";

    public static int Run(string[] args)
    {
        if (!AppPaths.TryInitialize(args))
        {
            Send("failed", $"Bad arguments: {AppPaths.DataDirFlag} is required");
            return 2;
        }

        RecordingTarget target;
        var output = new RecordingOutput(Value(args, "--folder"), Value(args, "--video-file") ?? RecordingSession.VideoFileName,
            AudioFiles: !args.Contains("--no-audio-files"));
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
            session = RecordingSession.Create(ui, AppSettings.Load(), target, output);
        }
        catch (Exception ex)
        {
            Send("failed", ex.Message);
            return 1;
        }

        Send("folder", session.FolderPath);
        RecordingOverlay? overlay = null;
        session.Started += _ =>
        {
            if (target.WindowHandle != IntPtr.Zero)
                overlay = new RecordingOverlay(target.WindowHandle);
            Send("started");
        };
        session.Warning += warning => Send("warning", warning);
        session.Log += text => Send("log", text);
        session.Stopping += () =>
        {
            overlay?.Dispose();
            Send("stopping");
        };
        session.Finished += (_, error) =>
        {
            overlay?.Dispose();
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
    public static string BuildArguments(RecordingTarget target, RecordingOutput output, bool skipGraphicsCapture)
    {
        string args = target.IsAudioOnly ? $"{Flag} --audio-only"
            : target.WindowHandle != IntPtr.Zero ? $"{Flag} --window {target.WindowHandle.ToInt64()}"
            : $"{Flag} --display \"{target.DisplayDeviceName}\"";
        args += $" --name \"{target.Name.Replace("\"", "'")}\" {AppPaths.Argument}";
        if (output.Folder != null)
            args += $" --folder \"{output.Folder}\"";
        if (output.VideoFileName != RecordingSession.VideoFileName)
            args += $" --video-file \"{output.VideoFileName}\"";
        if (!output.AudioFiles)
            args += " --no-audio-files";
        if (target.AppProcessId is { } pid)
            args += $" --app-pid {pid}";
        return skipGraphicsCapture ? args + " --crop" : args;
    }

    private static RecordingTarget ParseTarget(string[] args)
    {
        var target = ParseSource(args);
        return Value(args, "--app-pid") is { } pid ? target with { AppProcessId = uint.Parse(pid) } : target;
    }

    private static RecordingTarget ParseSource(string[] args)
    {
        string name = Value(args, "--name") ?? throw new ArgumentException("--name is required");
        if (args.Contains("--audio-only"))
            return RecordingTarget.ForAudio(name, null);
        if (Value(args, "--file") is { } file)
            return new RecordingTarget(name, IntPtr.Zero, null, Path.GetFullPath(file));
        if (Value(args, "--window") is { } hwnd)
            return new RecordingTarget(name, new IntPtr(long.Parse(hwnd)), null);
        if (Value(args, "--display") is { } display)
            return new RecordingTarget(name, IntPtr.Zero, display);
        throw new ArgumentException("--window, --display, --file or --audio-only is required");
    }

    private static string? Value(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static void Send(string kind, string? text = null)
    {
        Console.Out.WriteLine(text == null ? kind : $"{kind} {text.ReplaceLineEndings(" | ")}");
        Console.Out.Flush();
    }
}
