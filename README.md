# Meeting Recorder

A lightweight Windows system-tray app that records a window or a full screen as video, with system audio
and microphone saved to **separate** audio files.

## Features

- Tray icon shows the state: grey = idle, amber = starting, red = recording, spinning blue = saving (the
  recording has stopped but its files aren't finished yet).
- **Start recording**: pick *Full screen* (one entry per display) or any open window.
- **Stop recording**: from the menu, or double-click the tray icon.
- **Auto-record Teams meetings** (on by default): see [below](#teams-meetings).
- **Recordings**: the 10 most recent recordings, each with *Open folder* and its files (the one being recorded
  is listed but can't be opened until it is saved); plus *Open recordings folder*.
- **System audio** / **Microphone**: choose the device, follow the Windows default, or turn the source off.
- Every recording gets its own folder: `yyyy-MM-dd_HH-mm-ss-<window title|fullscreen>`, containing

  | File | Content |
  |---|---|
  | `video.mp4` | H.264 video, scaled down to 1080 lines when the source is taller, with system audio and microphone mixed into one AAC track for easy review |
  | `audio-system.m4a` | What the PC plays (loopback), AAC 48 kHz stereo 128 kbps |
  | `audio-mic.m4a` | Microphone, AAC 48 kHz mono 96 kbps |

  The separate audio files are for analysis (each source on its own); all three start at the first video frame, so
  they line up with the video. If the recorder can't open the audio devices for the video, the video is recorded
  without sound and a warning says so; the separate files are still recorded.
  Audio is recorded as WAV (no encoding work while recording, and safe if the PC crashes) and converted to AAC
  when the recording stops. With `CompressAudio` off, or if a conversion fails, the WAV is kept instead
  (`audio-system.wav`, 16-bit stereo; `audio-mic.wav`, 16-bit mono).
  While a recording is running or being saved, the files have `.tmp` appended (`video.mp4.tmp`,
  `audio-system.wav.tmp`, ...); they get their final names once saving is done.

Recordings go to `%USERPROFILE%\Videos\Meeting Recordings` by default. Change it with *Change recordings folder...* in the
tray menu (only while idle); new recordings go to the new folder, existing ones stay where they are.

## When something needs attention

The tray icon turns into a **yellow warning triangle** (or a red dot with a warning badge while still
recording) only when you need to do something, and stays that way until you look at it, even if
Windows notifications are muted. Open it from the bold *Needs attention* item at the top of the menu,
by double-clicking the icon, or by clicking the notification. Closing the details clears the warning.

Every warning is also appended to `%LOCALAPPDATA%\MeetingRecorder\warnings.log` (*Open warnings log* in the menu;
rolled over to `warnings.old.log` at 1 MB). Warnings belong to one run of the app: a restart, including an update,
starts with none, and the log keeps the earlier ones.

It's raised when:
- a recording fails to start, stops by itself, or the recorder crashes or hangs (30 s to start; 2 min plus 5 s per
  recorded minute to save);
- system audio or the microphone can't be recorded, stops mid-recording (e.g. headset unplugged), or the
  chosen device is missing and the Windows default is used instead;
- the recordings drive has less than 2 GB free;
- the app hits an unexpected error.

Things that need no action don't raise it: a normal save, or a window recording that ends because the
window was closed.

## Resource use

Measurements and the reasoning behind the defaults: [docs/performance.md](docs/performance.md).

- Idle in the tray: about 55 MB RAM and no CPU.
- While recording: about 1% CPU on a 2560x1440 screen, using the GPU's H.264 encoder at a variable
  frame rate (a static screen costs almost nothing). Audio is written as WAV, so it needs no encoding while recording.
- Saving: converting the audio to AAC takes about 1–1.5 seconds per recorded minute.
- Each recording runs in a short-lived child process (`MeetingRecorder.exe --record ...`). The native
  capture stack holds ~250 MB that it never gives back, so isolating it means memory returns to the system
  as soon as a recording ends. It also stops cleanly if the tray process dies.
- Files are about 0.5–3 MB per minute of video (depends on screen activity) plus 0.9 MB/min for its sound track, 0.9 MB/min system audio,
  0.7 MB/min microphone (WAV: 11 and 5.5 MB/min).
- `video.mp4` is a fragmented MP4, so it stays playable even if the PC crashes mid-recording.

## Teams meetings

With *Auto-record Teams meetings* ticked, the app records the meeting window by itself when a Microsoft Teams
(new Teams) call or meeting starts, and stops when it ends. A notification says when it starts.

- **Start:** Teams has been using a microphone for about 5 s (it does so only during a call, even while muted) and
  its meeting window is open (the separate window titled "*meeting name* | Microsoft Teams"). The recording is
  named after the meeting.
- **Stop:** within about 3 s of the meeting window closing (Teams closes it as the call ends), or about 12 s after
  Teams stops using the microphone if the window stays open.
- Stopping an automatic recording by hand keeps it stopped for the rest of that call. A recording you started
  yourself is never stopped automatically, and nothing starts while another recording is running.
- While you share your screen, Teams minimizes the meeting window, so the video shows the last frame
  until it's restored. The audio is recorded throughout.
- Make sure everyone in the meeting knows it's being recorded: unlike Teams' own recording, this app doesn't tell
  the other participants.

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
| `MaxVideoHeight` | `1080` | Taller sources (e.g. a 1440p or 4K screen) are scaled down to this height; `0` = never scale |
| `CompressAudio` | `true` | Convert the audio to AAC (.m4a) after stopping; `false` keeps WAV |
| `CaptureMouseCursor` | `true` | |
| `UseHardwareEncoder` | `true` | GPU encoding; uses far less CPU and memory than software |
| `AutoRecordTeams` | `true` | Record Teams calls and meetings automatically (also in the tray menu) |

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
latest build differs from its own it installs it and restarts, but only when no recording is in progress.
Unread warnings don't hold an update back (unlike section 7.3 of the standard): they are in the warnings log, and
belong to the session before the restart. A build that fails to install is not retried; it shows a tray warning instead.
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
