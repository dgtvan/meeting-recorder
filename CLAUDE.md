# Meeting Recorder

Windows tray app (.NET 10 WinForms) that records a window or the screen with separate system/mic audio.
See README.md for features and design. `<AppId>` is `MeetingRecorder`.

## Deployment

This app follows the App deployment standard v1.2
(https://github.com/dgtvan/local-app-deployment-strategy). The agent runs as a separate Windows account and
cannot touch a user's install directly; each user's running app installs new builds from D:\Apps\MeetingRecorder by itself.

After every change to the app that builds cleanly, publish it (sandbox disabled):

    cmd //c 'D:\Src\Personal\meeting-recorder\publish.cmd /quiet'

It prints one line per account. Report the result in one line by exit code: 0 updated and running ·
5 waiting until the app is idle · 6 an app didn't answer, it installs on next start · 7 an app failed to install
it (show output) · 1 error (show output).

To withdraw a bad build, delete its folder under D:\Apps\MeetingRecorder\builds\; apps go back to the previous one.
Deleting only its `build-id.txt` has the same effect (publishing removes the leftover folder after an hour).

Never run install.cmd or start the exe from the agent account (except for the tests in the standard): it creates
a separate copy under the agent account instead of updating the user's app. The user runs install.cmd once
themselves; after that, publishing is all an update needs. Never pass -Force.

## App specifics

- "Busy" (updates wait, install/uninstall refuse with exit 3) = a recording is starting, running or saving.
  A recording runs in a child process (`MeetingRecorder.exe --record ...`) that saves and exits when its stdin closes.
- Startup initialization (standard 7.1) is `AppInitializer`; settings migrations go there, keyed on `SchemaVersion`.
- Self-update (standard 7.3–7.5) is `SelfUpdater` plus `TrayApp.CheckForUpdate`.

## Testing

- Recording logic can be exercised without the tray: `MeetingRecorder.exe --record --display "\\.\DISPLAY1" --name fullscreen`
  (or `--window <hwnd>`) from `src/MeetingRecorder/bin/`, then write `stop` to stdin. It prints `folder`, `started`,
  `warning`, `finished`/`failed` lines. Recordings go to the agent account's own Videos folder.
- Deployment tests: section 11 of the standard, with a throwaway install under the agent account, removed afterwards
  with `uninstall.cmd /quiet -RemoveData`.
- Delete files with `~/.claude/.addons/utilities/file-helper/dist/file-helper.exe delete <file>`; `rm` is blocked.
