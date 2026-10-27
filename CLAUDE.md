# Meeting Recorder

Windows tray app (.NET 10 WinForms) that records a window or the screen with separate system/mic audio.
See README.md for features and design. `<AppId>` is `MeetingRecorder`.

## Deployment

This app follows the App deployment standard v2.2
(https://github.com/dgtvan/local-app-deployment-strategy).

After every change to the app that builds cleanly, deploy it (sandbox disabled):

    cmd //c 'D:\Src\Personal\meeting-recorder\deploy.cmd /quiet'

Report the result in one line by exit code: 0 installed and running · 3 the app is busy, ask the user to close
it and run again · 1 error (show output). Never pass -Force unless the user asks.

Deploy installs for the account that runs it. If `whoami` isn't the account that uses the app (the user's own
account), don't deploy: run `build.cmd /quiet` instead and ask the user to run `deploy.cmd` from their account.

## App specifics

- "Busy" (install/uninstall/deploy refuse with exit 3) = a recording is starting, running or saving.
  A recording runs in a child process (`MeetingRecorder.exe --record ...`) that saves and exits when its stdin closes;
  then a finisher (`--finish <folder> [--auto]`) mixes the sound into the videos, compresses the audio and writes the
  folder's README.md from the recorders' `*.journal.jsonl` files. Finishing counts as saving.
- Every mode except `--meeting-status` and `--audio-status` needs `--data-dir <folder>` (`AppPaths`); the tray passes it on.
- Audio files mean the same in every kind of recording (`RecordingAudio`): system = device loopback of every active
  output device (one part file each, mixed by the finisher), mic = the one in use (`AudioDevices.ChooseMicrophone`),
  followed. Don't go back to process loopback: it misses Teams call audio entirely.
  Anything about devices or sources goes to the journal (README.md) and `audio.log`, so a recording can be
  investigated afterwards.
- Auto-record is per meeting app: each `IMeetingApp` in `MeetingApps.All` gets an `AutoRecorder` (call detection,
  per-call folder, tidy-up, `meetings.log`). App-specific detection belongs in the `IMeetingApp`, not in the tray.
- Startup initialization (standard 4.2) is `AppInitializer`; settings migrations go there, keyed on `SchemaVersion`.
- Close requests (standard 3.5, 4.4) are answered in `TrayApp.OnExitRequested`.

## Testing

- Recording logic can be exercised without the tray: `MeetingRecorder.exe --record --display "\\.\DISPLAY1" --name fullscreen --data-dir <scratch folder>`
  (or `--window <hwnd>`) from `src/MeetingRecorder/bin/`, then write `stop` to stdin. It prints `folder`, `started`,
  `warning`, `log`, `finished`/`failed` lines. Then run `MeetingRecorder.exe --finish <folder> --data-dir <scratch folder>`
  for the videos' sound and README.md. Recordings go to the `RecordingsRoot` of the scratch folder's settings.json
  (the account's Videos folder without one). System audio records every app the account plays, and the microphone
  the room: keep test recordings short and delete them afterwards.
- Test the audio tracks with sound of your own: `--record --audio-only --name <n> --data-dir <scratch folder>` records
  no screen; a helper process that plays a sound (e.g. `C:\Windows\Media\Alarm01.wav`), on a non-default speaker
  too, or opens another microphone, shows that the files follow it. Never use the user's apps for this.
- Window capture (Windows Graphics Capture) isn't available from the agent account, so `--window` falls back to a
  cropped display capture, which records whatever covers the window, including the user's own work. For performance
  tests use `--record --file <video> --name <n> --data-dir <scratch folder>` instead: it replays a video file through the same encoder settings
  and captures nothing from the screen. `bench/make-test-video.ps1` makes the test video and `bench/bench.ps1`
  measures a run; log results in docs/performance.md. Delete test recordings afterwards.
- `MeetingRecorder.exe --meeting-status [app id]` prints, per meeting app, whether a call is active and which windows
  auto-record would use, then an `app-window` block per visible window of the app (rect, topmost/owner/capture
  affinity, why it's skipped, and its button names via UI Automation) to tell the meeting window from sharing
  toolbars. During calls the tray logs the same to `meetings.log` in the data folder whenever the windows change.
  It captures nothing, and works from the agent account because it shares the user's desktop and audio devices.
- Whether a deploy may run depends only on the app's state: if it isn't running, or is running but not recording,
  deploy. A meeting call doesn't block it (the new app may then start recording that call, which is fine).
- Deployment tests: section 8 of the standard. From an account other than the user's, don't start the tray: its
  `Local\` mutex and events are shared with the user's session, and auto-record could capture their screen. Use
  `install.cmd /quiet -NoStart` and `uninstall.cmd /quiet`, which only touch the agent account.
- Delete files with `~/.claude/.addons/utilities/file-helper/dist/file-helper.exe delete <file>`; `rm` is blocked.
