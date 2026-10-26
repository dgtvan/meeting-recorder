using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MeetingRecorder;

/// <summary>The microphone to record and why it was picked, as logged and written to README.md.</summary>
sealed record MicrophoneChoice(string DeviceId, string DeviceName, string Reason);

/// <summary>
/// Audio device helpers: which microphone to record (<see cref="ChooseMicrophone"/>), and descriptions of the devices
/// and of the apps using them for the logs.
/// </summary>
static class AudioDevices
{
    /// <summary>Recorder processes of this app, whose own microphone use never decides which one to record.</summary>
    private static readonly string OwnProcessName = System.Diagnostics.Process.GetCurrentProcess().ProcessName;

    /// <summary>
    /// The microphone to record now: the one <paramref name="app"/> (or one of its child processes) has open; else one
    /// another app has open; else the current one while it's still connected; else the Windows default. Null when
    /// there is no microphone at all.
    /// </summary>
    /// <remarks>
    /// Keeping the current one comes before the default so a meeting app that closes its microphone for a moment
    /// (switching devices, muting in some apps) doesn't make the recording jump to another one and back.
    /// </remarks>
    public static MicrophoneChoice? ChooseMicrophone(AppProcess? app, string? currentId)
    {
        using var enumerator = new MMDeviceEnumerator();
        var users = MicrophoneUsers(enumerator);
        if (app != null)
        {
            var parents = ProcessTree.Parents();
            if (users.FirstOrDefault(u => ProcessTree.IsInTree(u.Process.Id, app.Id, parents)) is { DeviceId: not null } own)
                return new MicrophoneChoice(own.DeviceId, own.DeviceName, $"{app} has it open");
        }
        var others = users.Where(u => u.DeviceId == currentId).Concat(users).ToList();
        if (others.FirstOrDefault() is { } other)
            return new MicrophoneChoice(other.DeviceId, other.DeviceName, $"{other.Process} has it open");
        if (currentId != null && Active(enumerator, currentId) is { } current)
        {
            using (current)
                return new MicrophoneChoice(current.ID, current.FriendlyName, "still connected; no app has a microphone open");
        }
        if (!enumerator.TryGetDefaultAudioEndpoint(DataFlow.Capture, Role.Console, out var fallback))
            return null;
        using (fallback)
            return new MicrophoneChoice(fallback!.ID, fallback.FriendlyName, "Windows default microphone; no app has a microphone open");
    }

    /// <summary>The device with this id while it is connected and enabled; null otherwise.</summary>
    public static MMDevice? Active(MMDeviceEnumerator enumerator, string id)
    {
        try
        {
            var device = enumerator.GetDevice(id);
            if (device.State == DeviceState.Active)
                return device;
            device.Dispose();
        }
        catch (COMException)
        {
        }
        return null;
    }

    /// <summary>One line per microphone in use: which apps have it open, e.g. for the log when the choice changes.</summary>
    public static string DescribeMicrophoneUse()
    {
        using var enumerator = new MMDeviceEnumerator();
        var users = MicrophoneUsers(enumerator);
        return users.Count == 0 ? "no app has a microphone open"
            : string.Join("; ", users.GroupBy(u => u.DeviceName).Select(g => $"{g.Key}: {string.Join(", ", g.Select(u => u.Process))}"));
    }

    /// <summary>"muted, volume 80%" for a capture device, for the log; empty when it can't be read.</summary>
    public static string DescribeLevel(string deviceId)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(deviceId);
            var volume = device.AudioEndpointVolume;
            return $"{(volume.Mute ? "muted in Windows, " : "")}volume {volume.MasterVolumeLevelScalar * 100:0}%";
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            return "";
        }
    }

    private sealed record MicrophoneUser(string DeviceId, string DeviceName, AppProcess Process);

    /// <summary>Every app holding an active microphone capture session, other than this app's recorders.</summary>
    private static List<MicrophoneUser> MicrophoneUsers(MMDeviceEnumerator enumerator)
    {
        var users = new List<MicrophoneUser>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                foreach (var process in ActiveSessionProcesses(device))
                {
                    if (process.Id != 0 && !process.Name.Equals(OwnProcessName, StringComparison.OrdinalIgnoreCase))
                        users.Add(new MicrophoneUser(device.ID, device.FriendlyName, process));
                }
            }
        }
        return users;
    }

    private static List<AppProcess> ActiveSessionProcesses(MMDevice device)
    {
        var found = new List<AppProcess>();
        var sessions = device.AudioSessionManager.Sessions;
        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            if (session.State != AudioSessionState.AudioSessionStateActive)
                continue;
            uint pid = session.GetProcessID;
            if (pid == 0)
                found.Add(new AppProcess(0, "system sounds"));
            else if (ProcessTree.Describe(pid) is { } process)
                found.Add(process);
        }
        return found;
    }

    /// <summary>
    /// Every connected or unplugged audio device with its state and default roles, and which apps are playing or
    /// recording on each: the starting point for working out where a meeting's sound went.
    /// </summary>
    public static string Describe()
    {
        var text = new StringBuilder();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var flow in new[] { DataFlow.Render, DataFlow.Capture })
        {
            var defaults = new Dictionary<string, List<string>>();
            foreach (var role in new[] { Role.Console, Role.Communications })
            {
                if (enumerator.TryGetDefaultAudioEndpoint(flow, role, out var device))
                {
                    using (device)
                        (defaults.TryGetValue(device!.ID, out var roles) ? roles : defaults[device.ID] = []).Add(RoleName(role));
                }
            }
            foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active | DeviceState.Unplugged | DeviceState.Disabled))
            {
                using (device)
                {
                    string roles = defaults.TryGetValue(device.ID, out var r) ? $", default for {string.Join(" and ", r)}" : "";
                    text.Append($"  {FlowName(flow)} {device.FriendlyName}: {StateName(device.State)}{roles}");
                    if (device.State == DeviceState.Active)
                    {
                        if (flow == DataFlow.Capture && DescribeLevel(device.ID) is { Length: > 0 } level)
                            text.Append($", {level}");
                        var processes = ActiveSessionProcesses(device);
                        if (processes.Count > 0)
                            text.Append($"; in use by {string.Join(", ", processes)}");
                    }
                    text.AppendLine();
                }
            }
        }
        return text.ToString().TrimEnd();
    }

    public static string FlowName(DataFlow flow) => flow switch
    {
        DataFlow.Render => "output",
        DataFlow.Capture => "input",
        _ => flow.ToString(),
    };

    public static string RoleName(Role role) => role switch
    {
        Role.Console or Role.Multimedia => "apps",
        Role.Communications => "calls",
        _ => role.ToString(),
    };

    public static string StateName(DeviceState state) => state switch
    {
        DeviceState.Active => "connected",
        DeviceState.Unplugged => "unplugged",
        DeviceState.Disabled => "disabled",
        DeviceState.NotPresent => "removed",
        _ => state.ToString(),
    };
}

/// <summary>
/// Reports every audio device change as a line for the log: a device plugged in or out, enabled or disabled, added or
/// removed, and a new Windows default device. Events arrive on the thread that created it (the UI thread).
/// </summary>
sealed class AudioDeviceWatcher : IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDeviceNotificationClient _client;
    private readonly Dictionary<string, (string Name, DataFlow Flow)> _devices = [];

    /// <summary>A device change, described for the log.</summary>
    public event Action<string>? Changed;

    public AudioDeviceWatcher()
    {
        foreach (var device in _enumerator.EnumerateAudioEndPoints(DataFlow.All, DeviceState.All))
        {
            using (device)
                Remember(device);
        }
        _client = _enumerator.CreateNotificationClient();
        _client.DeviceStateChanged += (_, e) => Report($"{Name(e.DeviceId)}: now {AudioDevices.StateName(e.NewState)}");
        _client.DeviceAdded += (_, e) => Report($"{Name(e.DeviceId)}: added");
        _client.DeviceRemoved += (_, e) => Report($"{Name(e.DeviceId)}: removed");
        _client.DefaultDeviceChanged += (_, e) =>
        {
            // Windows reports Console and Multimedia separately; they always change together, so one line is enough.
            if (e.Role == Role.Multimedia)
                return;
            string flow = AudioDevices.FlowName(e.Flow);
            Report(e.DeviceId == null
                ? $"Windows default {flow} for {AudioDevices.RoleName(e.Role)}: none"
                : $"Windows default {flow} for {AudioDevices.RoleName(e.Role)}: {Name(e.DeviceId)}");
        };
    }

    private void Report(string text) => Changed?.Invoke($"Device change: {text}");

    private void Remember(MMDevice device)
    {
        try
        {
            _devices[device.ID] = (device.FriendlyName, device.DataFlow);
        }
        catch (COMException)
        {
            // Devices that were removed long ago can have no properties left.
        }
    }

    /// <summary>"input Jabra SPEAK 410 (Echo Cancelling Speakerphone)"; the id when the device can't be read.</summary>
    private string Name(string? id)
    {
        if (id == null)
            return "(none)";
        if (!_devices.ContainsKey(id))
        {
            try
            {
                using var device = _enumerator.GetDevice(id);
                Remember(device);
            }
            catch (COMException)
            {
            }
        }
        return _devices.TryGetValue(id, out var known) ? $"{AudioDevices.FlowName(known.Flow)} {known.Name}" : id;
    }

    public void Dispose()
    {
        _client.Dispose();
        _enumerator.Dispose();
    }
}
