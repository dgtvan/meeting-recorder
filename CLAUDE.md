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
  A recording runs in a child process (`MeetingRecorder.exe --record ...`) that saves and exits when its stdin closes.
- Every mode except `--teams-status` needs `--data-dir <folder>` (`AppPaths`); the tray passes it to the worker.
- Startup initialization (standard 4.2) is `AppInitializer`; settings migrations go there, keyed on `SchemaVersion`.
- Close requests (standard 3.5, 4.4) are answered in `TrayApp.OnExitRequested`.

## Testing

- Recording logic can be exercised without the tray: `MeetingRecorder.exe --record --display "\\.\DISPLAY1" --name fullscreen --data-dir <scratch folder>`
  (or `--window <hwnd>`) from `src/MeetingRecorder/bin/`, then write `stop` to stdin. It prints `folder`, `started`,
  `warning`, `finished`/`failed` lines. Recordings go to the agent account's own Videos folder.
- Window capture (Windows Graphics Capture) isn't available from the agent account, so `--window` falls back to a
  cropped display capture, which records whatever covers the window, including the user's own work. For performance
  tests use `--record --file <video> --name <n> --data-dir <scratch folder>` instead: it replays a video file through the same encoder settings
  and captures nothing from the screen. `bench/make-test-video.ps1` makes the test video and `bench/bench.ps1`
  measures a run; log results in docs/performance.md. Delete test recordings afterwards.
- `MeetingRecorder.exe --teams-status` prints whether a Teams call is active and which windows auto-record would use,
  then a `teams-window` block per visible Teams window (rect, topmost/owner/capture affinity, why it's skipped, and
  its button names via UI Automation) to tell the meeting window from sharing toolbars.
  It captures nothing, and works from the agent account because it shares the user's desktop and audio devices.
- Whether a deploy may run depends only on the app's state: if it isn't running, or is running but not recording,
  deploy. A Teams call doesn't block it (the new app may then start recording that call, which is fine).
- Deployment tests: section 8 of the standard. From an account other than the user's, don't start the tray: its
  `Local\` mutex and events are shared with the user's session, and auto-record could capture their screen. Use
  `install.cmd /quiet -NoStart` and `uninstall.cmd /quiet`, which only touch the agent account.
- Delete files with `~/.claude/.addons/utilities/file-helper/dist/file-helper.exe delete <file>`; `rm` is blocked.
