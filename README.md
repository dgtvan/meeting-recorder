# Meeting Recorder

A lightweight Windows system-tray app that records a window or a full screen as video, with the system audio and the
microphone saved to **separate** audio files, and a `README.md` (plus `recording.json` for programs) in every
recording folder that says what each file holds.

## Features

- Tray icon shows the state: grey = idle, amber = starting, pulsing red = recording, spinning blue = saving (the
  recording has stopped but its files aren't finished yet). With more than one recording running, the red dot
  fills the ring and shows how many in white.
- **Start recording**: pick *Full screen* (one entry per display) or any open window. Several windows or displays
  can be recorded at once, each into its own folder; what is already being recorded is ticked and can't be picked
  again. All recordings capture the same system audio and microphone.
- **Stop recording**: from the menu (with several running, a submenu stops one of them or *Stop all*), or
  double-click the tray icon to stop them all.
- **Auto-record meetings** (on by default for each supported app, so far Microsoft Teams): see [below](#auto-recording-meetings).
- **Recordings**: the 10 most recent recordings, each with *Open folder* and its files (the one being recorded
  is listed but can't be opened until it is saved); plus *Open recordings folder*.
- **System audio**: on (every app, on any speaker or headset) or off. **Microphone**: automatic (the one in use, see
  [Audio](#audio)), a device of your choice, or off.
- Every recording gets its own folder (an auto-recorded call has [one per call](#auto-recording-meetings)):
  `yyyy-MM-dd_HH-mm-ss-<window title|fullscreen>`, containing the same files whatever is recorded (a window of any
  app, a meeting, a full screen):

  | File | Content |
  |---|---|
  | `video.mp4` | H.264 video, scaled down to 1080 lines when the source is taller, with `audio-system` and `audio-mic` mixed into one AAC track for watching |
  | `audio-system.m4a` | Everything the PC plays: every app, on every speaker or headset; in a call, the other participants. AAC 48 kHz stereo 128 kbps |
  | `audio-mic.m4a` | The microphone in use, following a change mid-recording. AAC 48 kHz mono 96 kbps |
  | `README.md` | What each file holds, where it starts on the recording's clock, how long it is and whether it has sound; which microphone was used when and why; every device change during the recording |
  | `recording.json` | The same facts as README.md for programs (e.g. an analysis that decides which files to use): per file its kind, speaker (`me`/`others`), start and length in seconds, sound seconds, peak, per-minute peaks and sources; `schema` 1 |

  The separate audio files are for analysis (each source on its own); they start together, and README.md gives each
  file's start, so a video that starts later (a second meeting window) can be lined up with them.
  Audio is recorded as WAV (no encoding work while recording, and safe if the PC crashes); once the recording is
  saved, a finisher (`MeetingRecorder.exe --finish`) mixes the sound into the videos, converts the audio to AAC and
  writes README.md. With `CompressAudio` off, or if a conversion fails, the WAV is kept instead (`audio-system.wav`,
  16-bit stereo; `audio-mic.wav`, 16-bit mono).
  While a recording is running or being saved, the files have `.tmp` appended (`video.mp4.tmp`,
  `audio-system.wav.tmp`, ...); they get their final names once saving is done. Each recorder also keeps a journal
  (`*.journal.jsonl`) in the folder, which the finisher turns into README.md and deletes; a folder whose finisher
  never ran (the app was killed) is finished the next time the app starts.

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
  recorded minute to save); a meeting window that fails in an auto-recorded call's first 30 s is raised only once the
  call lasts longer, since Teams briefly shows a call's window again just after it ends;
- an auto-recorded call has only one side: the microphone has sound but the system audio is (nearly) silent, so the
  other participants are missing (README.md says so too);
- system audio or the microphone can't be recorded (system audio is retried every 2 s), or the
  microphone chosen in the menu is missing and the one in use is recorded instead (a microphone that is unplugged
  while recorded automatically is just replaced; that is logged, not a warning);
- a video can't get its sound added, or the audio can't be compressed;
- the recordings drive has less than 2 GB free;
- the app hits an unexpected error.

Things that need no action don't raise it: a normal save, or a window recording that ends because the
window was closed.

## Resource use

Measurements and the reasoning behind the defaults: [docs/performance.md](docs/performance.md).

- Idle in the tray: about 55 MB RAM and no CPU.
- While recording: about 1% CPU on a 2560x1440 screen, using the GPU's H.264 encoder at a variable
  frame rate (a static screen costs almost nothing). Audio is written as WAV, so it needs no encoding while recording.
- Saving: mixing the sound into the video and converting the three audio files to AAC takes a few seconds per
  recorded minute, in a separate finisher process.
- Each recording runs in a short-lived child process (`MeetingRecorder.exe --record ...`). The native
  capture stack holds ~250 MB that it never gives back, so isolating it means memory returns to the system
  as soon as a recording ends. It also stops cleanly if the tray process dies.
- Files are about 0.5–3 MB per minute of video (depends on screen activity) plus 0.9 MB/min for its sound track, 0.9 MB/min system audio,
  0.7 MB/min microphone (WAV: 11 and 5.5 MB/min).
- `video.mp4` is a fragmented MP4, so it stays playable even if the PC crashes mid-recording.

## Auto-recording meetings

*Auto-record meetings* in the tray menu lists the supported meeting apps; with an app ticked (all are by default),
the app records a call or meeting in it by itself when it starts, and stops when it ends. A notification says when
it starts. One call is one folder, named after the meeting, with a video of every meeting window the app shows
during the call and one set of audio files:

| File | Content |
|---|---|
| `video.mp4`, `video-2.mp4`, ... | One per meeting window, in the order they opened (e.g. the meeting and a call on hold); each has the call's sound for its own time span mixed in. While you share your screen, the main one shows what you share instead (see below) |
| `audio-system.m4a`, `audio-mic.m4a` | The whole call, recorded once, from the first window's start until the call ends; `audio-system` holds the other participants, `audio-mic` the microphone the meeting app uses |
| `README.md` | As for any recording; *Starts at* lines each video up with the audio files |

Supported apps: **Microsoft Teams** (new Teams).

- **Start:** the app has been using a microphone for about 5 s (meeting apps do so only during a call, even while
  muted) and a meeting window is open (for Teams, the separate window titled "*meeting name* | Microsoft Teams"). A
  meeting window that opens later during the call is recorded into the same folder.
- **Stop:** a window's video stops within about 3 s of the window closing. The call stops once no meeting window
  has been open for about 3–6 s (the app closes them as the call ends), or about 12 s after the app stops using the
  microphone.
- If Teams replaces the meeting window during the call (the pre-join screen becomes the meeting window when you
  join), the new window gets the next video and the audio carries on.
- After the call, the finisher deletes a call shorter than 30 s (a pre-join screen, a call left at once); otherwise
  it deletes videos shorter than 30 s, renumbers the rest, and renames the folder after the window recorded longest.
  Each deletion is noted in `warnings.log` and README.md without raising a warning.
- Teams also opens windows with the meeting's title that show nothing (minimized, off screen, tiny or hidden from
  screen capture); they're skipped until they show. If such a window still can't be captured while another window
  of the call is recorded, that's only noted in `warnings.log`.
- Stopping an automatic recording by hand keeps that window unrecorded for the rest of the call; stopping the last
  one stops the call's audio too. A recording you started yourself is never stopped automatically, gets a folder of
  its own, and if it is of a meeting window, no automatic one starts for it.
- **Screen share:** while you share a window or a screen, what you share is recorded too (`share.mp4`, found from the
  border Teams draws around it, the moment it appears). When the call is saved it is cut into the main `video.mp4`:
  the meeting window, then what you share from its first frame, then the meeting window again from the moment it
  shows again (Teams minimizes it while you share), or 5 s after the share stopped. Both are scaled into one frame
  size with black bars, with hard cuts; a share shorter than 2 s is left out. This re-encodes `video.mp4`, which adds
  about 5 s of saving per minute of the call; calls without a share are saved as before. README.md and
  recording.json say when the video shows the share. If what is shared can't be told (`--meeting-status` prints
  `share unknown`), it isn't recorded and `meetings.log` says so.
- `meetings.log` in the data folder records what auto-record did in each call (windows recorded, how long, files
  dropped or renamed) and, whenever the app's windows change during a call, a `--meeting-status` description of each
  one (size, position, styles, button names), for working out which window is which. Each entry starts with the
  app's name. It holds window titles and button labels, which can include participants' names; it rolls over to
  `meetings.old.log` at 1 MB. `MeetingRecorder.exe --meeting-status [app]` prints the same on demand (app: `teams`).
- Make sure everyone in the meeting knows it's being recorded: unlike a meeting app's own recording, this app
  doesn't tell the other participants.

### Supporting another meeting app

Implement `IMeetingApp` (`src/MeetingRecorder/IMeetingApp.cs`): a stable `Id`, a `DisplayName`, how to tell the
app is in a call (`IsCallActive`, usually `MeetingWindows.IsCapturingMicrophone` on its processes) and which windows
show the meeting (`FindMeetingWindows`), plus `FindAppWindows` for the log. Add it to `MeetingApps.All`. The tray
menu, the `AutoRecord` setting, recording, the per-call folder, `meetings.log` and `--meeting-status` all follow
from that list. `TeamsMeetingApp` is the example.

## Audio

The audio files don't depend on which speaker, headset or microphone is in use, so plugging in a headset or a
speakerphone before or during a meeting changes nothing:

- **System audio** records everything every output device plays (WASAPI loopback of each active speaker, headset and
  display), each into a part file that the finisher mixes into `audio-system`. Devices are checked every 2 s, so a
  Bluetooth headset or USB speakerphone plugged in during a meeting is recorded from then on, and one that is
  unplugged and comes back is recorded again. Windows process loopback (recording one app, or every app but the
  recorder) is not used: measured during a Teams call, it records none of the call's sound, only Teams' notification
  sounds, while device loopback records the call.
- **Microphone**, when automatic: the one the recorded program has open; else one another app has open; else the one
  being recorded while it's still connected; else the Windows default. It is checked every 2 s and on every device
  change, and switched within the same file when a different one is seen twice in a row (so a meeting app that
  reopens its microphone doesn't cause a switch back and forth); a microphone that stops (unplugged) is replaced at
  once. Muting in the meeting app keeps its microphone open, so it doesn't count as a change.

Everything about the audio is logged for investigating a recording afterwards:

- `audio.log` in the data folder (rolled over to `audio.old.log` at 1 MB): every device plugged in or out, added,
  removed, enabled or disabled, and every new Windows default device, while the app runs, also when not recording;
  the devices and which apps use them when the app starts and when each recording starts; per recording (prefixed
  with its name), what each audio file records, every microphone switch with the reason and which apps had which
  microphone open, a source that stopped or failed, and each file's sound statistics at the end.
- README.md in the recording folder: the same for that recording, on the recording's clock.
- `MeetingRecorder.exe --audio-status` prints every audio device with its state, default roles, volume and mute, which
  apps are playing or recording on it, and which microphone a recording would use now. It captures nothing.

## Window recording

Windows are captured with Windows Graphics Capture, which records the window even when it is covered.
If that API is unavailable (some managed/enterprise PCs disable it), the app falls back automatically
to recording the window's display cropped to the window and following it when it moves. In that mode,
anything on top of the window is recorded too, and the crop stays on the display the window started on.

Minimized windows are restored when recording starts.

While a window is recorded, a red border surrounds it; on a maximized window it is drawn just inside its edges.
The border follows the window, hides while it's minimized, and stays behind windows that cover it. A red
**● Recording** tab in its top-left corner can be turned on with `RecordingOverlay.ShowLabel` (off for now). Both
are left out of every screen capture, so they don't
appear in the recording, in screenshots or in a screen share. On Windows 11 this replaces Windows' own yellow
capture border, which flashes.

## Settings

`%LOCALAPPDATA%\VanDang\MeetingRecorder\data\settings.json` (the data folder given by `--data-dir`), created with defaults when
the app first starts. On the first start with an empty data folder, the app copies `settings.json` and `warnings.log`
from where earlier versions kept them (`%APPDATA%\MeetingRecorder`, `%LOCALAPPDATA%\MeetingRecorder`); the old files are
left in place.

| Setting | Default | Notes |
|---|---|---|
| `SchemaVersion` | `3` | Managed by the app; older files are migrated at startup |
| `RecordingsRoot` | `Videos\Meeting Recordings` | Where recording folders are created |
| `SystemAudioDeviceId` | `""` | `"off"` = no system or app audio; anything else records every app on any device (schema 3 turned a chosen output device into `""`) |
| `MicrophoneDeviceId` | `""` | `""` = automatic, the one in use; `"off"` = don't record; else a device id (set via the menu), recorded while it's connected |
| `VideoFramerate` | `15` | Maximum frames per second |
| `VideoQuality` | `60` | 1–100; lower = smaller files |
| `MaxVideoHeight` | `1080` | Taller sources (e.g. a 1440p or 4K screen) are scaled down to this height; `0` = never scale |
| `CompressAudio` | `true` | Convert the audio to AAC (.m4a) after stopping; `false` keeps WAV |
| `CaptureMouseCursor` | `true` | |
| `UseHardwareEncoder` | `true` | GPU encoding; uses far less CPU and memory than software |
| `AutoRecord` | `{}` | Auto-record per meeting app id, e.g. `{"teams": false}`; an app not listed is on (also in the tray menu, *Auto-record meetings*). Replaces schema 1's `AutoRecordTeams`, migrated at startup |

## Requirements

- Windows 10 2004+ / Windows 11, x64 (2004 added per-process audio capture)
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Visual C++ Redistributable x64](https://aka.ms/vs/17/release/vc_redist.x64.exe) (needed by ScreenRecorderLib)

## Install, update, uninstall

Deployment follows the [App deployment standard v2.2](https://github.com/dgtvan/local-app-deployment-strategy),
for one Windows account: run the scripts from the account that uses the app.

| Script | What it does |
|---|---|
| `build.cmd` | Builds the working directory into `dist\` (release). Installs nothing. |
| `install.cmd` | Copies `dist\` to `%LOCALAPPDATA%\VanDang\MeetingRecorder\app`, starts it at sign-in (HKCU Run) and from a Start Menu shortcut, and starts it. Installs missing prerequisites with winget, and removes entries left by earlier install methods (`%LOCALAPPDATA%\Programs\MeetingRecorder`, `%LOCALAPPDATA%\MeetingRecorder\app`, the *Settings > Apps* entry). Safe to re-run. `-NoStart` skips starting it. |
| `uninstall.cmd` | Removes the app folder, the Run value and the shortcut. Never deletes `%LOCALAPPDATA%\VanDang\MeetingRecorder\data` or the recordings; it prints where they are. |
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
- [NAudio](https://github.com/naudio/NAudio) (MIT): WASAPI loopback and microphone capture, AAC encoding
