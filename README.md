# Meeting Recorder

A lightweight Windows system-tray app that records a window or a full screen as video, with system audio
and microphone saved to **separate** audio files.

## Features

- Tray icon shows the state: grey = idle, amber = starting, red = recording, spinning blue = saving (the
  recording has stopped but its files aren't finished yet).
- **Start recording**: pick *Full screen* (one entry per display) or any open window.
- **Stop recording**: from the menu, or double-click the tray icon.
- **Recordings**: the 10 most recent recordings, each with *Open folder* and its files (the one being recorded
  is listed but can't be opened until it is saved); plus *Open recordings folder*.
- **System audio** / **Microphone**: choose the device, follow the Windows default, or turn the source off.
- Every recording gets its own folder: `yyyy-MM-dd_HH-mm-ss-<window title|fullscreen>`, containing

  | File | Content |
  |---|---|
  | `video.mp4` | H.264 video, no audio |
  | `audio-system.wav` | What the PC plays (loopback), 48 kHz 16-bit stereo |
  | `audio-mic.wav` | Microphone, 48 kHz 16-bit mono |

  All three start at the first video frame, so they line up when played or edited together.
  While a recording is running or being saved, the files are named `video.tmp`, `audio-system.tmp` and
  `audio-mic.tmp`; they get their final names once saving is done.

Recordings go to `%USERPROFILE%\Videos\Meeting Recordings` by default. Change it with *Change recordings folder...* in the
tray menu (only while idle); new recordings go to the new folder, existing ones stay where they are.

## When something needs attention

The tray icon turns into a **yellow warning triangle** (or a red dot with a warning badge while still
recording) only when you need to do something, and stays that way until you look at it, even if
Windows notifications are muted. Open it from the bold *Needs attention* item at the top of the menu,
by double-clicking the icon, or by clicking the notification. Closing the details clears the warning.

It's raised when:
- a recording fails to start, stops by itself, or the recorder crashes or hangs (30 s to start, 2 min to save);
- system audio or the microphone can't be recorded, stops mid-recording (e.g. headset unplugged), or the
  chosen device is missing and the Windows default is used instead;
- the recordings drive has less than 2 GB free;
- the app hits an unexpected error.

Things that need no action don't raise it: a normal save, or a window recording that ends because the
window was closed.

## Resource use

- Idle in the tray: about 55 MB RAM and no CPU.
- While recording: about 1% CPU on a 2560x1440 screen, using the GPU's H.264 encoder at a variable
  frame rate (a static screen costs almost nothing). WAV audio needs no encoding.
- Each recording runs in a short-lived child process (`MeetingRecorder.exe --record ...`). The native
  capture stack holds ~250 MB that it never gives back, so isolating it means memory returns to the system
  as soon as a recording ends. It also stops cleanly if the tray process dies.
- Files are about 0.5–3 MB per minute of video (depends on screen activity), 11 MB/min system audio,
  5.5 MB/min microphone.
- `video.mp4` is a fragmented MP4, so it stays playable even if the PC crashes mid-recording.

## Window recording

Windows are captured with Windows Graphics Capture, which records the window even when it is covered.
If that API is unavailable (some managed/enterprise PCs disable it), the app falls back automatically
to recording the window's display cropped to the window and following it when it moves. In that mode,
anything on top of the window is recorded too, and the crop stays on the display the window started on.

Minimized windows are restored when recording starts.

## Settings

`%APPDATA%\MeetingRecorder\settings.json`, created with defaults when the app first starts:

| Setting | Default | Notes |
|---|---|---|
| `SchemaVersion` | `1` | Managed by the app; older files are migrated at startup |
| `RecordingsRoot` | `Videos\Meeting Recordings` | Where recording folders are created |
| `SystemAudioDeviceId` / `MicrophoneDeviceId` | `""` | `""` = Windows default, `"off"` = don't record, else a device id (set via the menu) |
| `VideoFramerate` | `15` | Maximum frames per second |
| `VideoQuality` | `60` | 1–100; lower = smaller files |
| `CaptureMouseCursor` | `true` | |
| `UseHardwareEncoder` | `true` | GPU encoding; uses far less CPU and memory than software |

## Requirements

- Windows 10 1903+ / Windows 11, x64
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Visual C++ Redistributable x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) (needed by ScreenRecorderLib)

## Install, update, uninstall

Deployment follows the [App deployment standard v1.2](https://github.com/dgtvan/local-app-deployment-strategy).

| Script | Who runs it | What it does |
|---|---|---|
| `publish.cmd` | The developer or agent, after every change | Builds into `D:\Apps\MeetingRecorder\builds\<build id>\`, keeps the newest 3 builds plus any build an account has installed, and reports what each account's app did with it |
| `install.cmd` | Each Windows account that uses the app, **once**, from that account | Installs the latest published build to `%LOCALAPPDATA%\Programs\MeetingRecorder`, starts it at sign-in (HKCU Run), adds a Start Menu shortcut and a *Settings > Apps* entry, and starts it. Installs missing prerequisites with winget. Re-run it to repair the install. |
| `uninstall.cmd` | That account | Removes the app and its registration; keeps settings and recordings unless `-RemoveData` / `-RemoveRecordings` |

After the first install, the app updates itself: it watches `D:\Apps\MeetingRecorder\builds\`, and when the
latest build differs from its own it installs it and restarts, but only when no recording is in progress and
no warning is waiting to be read. A build that fails to install is not retried; it shows a tray warning instead.
To withdraw a bad build, delete its folder (or just its `build-id.txt`); apps go back to the previous build.

The app keeps its update state in `%LOCALAPPDATA%\MeetingRecorder\update-state.txt` and mirrors it to
`D:\Apps\MeetingRecorder\pickup\<user name>\status.txt`, which is how `publish.cmd` reports per account.

All scripts accept `/quiet` as the first argument (no pause). `install.cmd` and `uninstall.cmd` refuse while a
recording is in progress (exit code 3) unless given `-Force`, which saves and ends the recording.

## Build

```powershell
dotnet build src/MeetingRecorder        # development build in src/MeetingRecorder/bin/
publish.cmd                             # release build, published for installed apps
```

## Libraries

- [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) (MIT): screen/window capture and H.264 encoding via Media Foundation
- [NAudio](https://github.com/naudio/NAudio) (MIT): WASAPI loopback and microphone capture
