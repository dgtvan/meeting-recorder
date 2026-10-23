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

Every warning is also appended to `warnings.log` in the data folder (*Open warnings log* in the menu;
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

While a window is recorded, a red border surrounds it, with a red **● Recording** tab (the dot pulses) in its
top-left corner, over the window's own corner; on a maximized window the border is drawn just inside its edges. They follow the window, hide while
it's minimized, and stay behind windows that cover it. They are left out of every screen capture, so they don't
appear in the recording, in screenshots or in a screen share. On Windows 11 this replaces Windows' own yellow
capture border, which flashes.

## Settings

`%LOCALAPPDATA%\MeetingRecorder\data\settings.json` (the data folder given by `--data-dir`), created with defaults when
the app first starts. On the first start with an empty data folder, the app copies `settings.json` and `warnings.log`
from where earlier versions kept them (`%APPDATA%\MeetingRecorder`, `%LOCALAPPDATA%\MeetingRecorder`); the old files are
left in place.

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

Deployment follows the [App deployment standard v2.1](https://github.com/dgtvan/local-app-deployment-strategy),
for one Windows account: run the scripts from the account that uses the app.

| Script | What it does |
|---|---|
| `build.cmd` | Builds the working directory into `dist\` (release). Installs nothing. |
| `install.cmd` | Copies `dist\` to `%LOCALAPPDATA%\MeetingRecorder\app`, starts it at sign-in (HKCU Run) and from a Start Menu shortcut, and starts it. Installs missing prerequisites with winget, and removes entries left by earlier install methods (`%LOCALAPPDATA%\Programs\MeetingRecorder`, the *Settings > Apps* entry). Safe to re-run. `-NoStart` skips starting it. |
| `uninstall.cmd` | Removes the app folder, the Run value and the shortcut. Never deletes `%LOCALAPPDATA%\MeetingRecorder\data` or the recordings; it prints where they are. |
| `deploy.cmd` | `build`, `uninstall`, `install`: all an update needs. A failed build stops it before anything is removed. |

All scripts accept `/quiet` as the first argument (no pause). Exit codes: `0` done, `1` error, `3` refused.

Install and uninstall don't close the app by force: they ask it to (named event `Local\MeetingRecorder-Exit`).
When idle it exits. While a recording is starting, running or saving it is **busy**: it refuses
(`Local\MeetingRecorder-ExitRefused`), keeps recording and shows a notification, and the script stops with exit
code 3 without changing anything. `-Force` closes it anyway: the recording is saved and ends, then the install goes
on. An app that doesn't answer within 10 s (e.g. a build from before v2.1) also gets exit code 3 without `-Force`.

The app must be started with `--data-dir "<folder>"` (the Run value and shortcut do this); without it, it shows an
error and exits. A second launch exits silently.

## Build

```powershell
dotnet build src/MeetingRecorder        # development build in src/MeetingRecorder/bin/
build.cmd                               # release build in dist\
deploy.cmd                              # release build, installed and started for this account
```

## Libraries

- [ScreenRecorderLib](https://github.com/sskodje/ScreenRecorderLib) (MIT): screen/window capture and H.264 encoding via Media Foundation
- [NAudio](https://github.com/naudio/NAudio) (MIT): WASAPI loopback and microphone capture
