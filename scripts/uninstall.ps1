<#
.SYNOPSIS
    Removes Meeting Recorder for the current account.

.DESCRIPTION
    Follows the App deployment standard v2.2, section 3.3 (https://github.com/dgtvan/local-app-deployment-strategy).
    Stops the app, removes the start-at-sign-in entry, the Start Menu shortcut and
    %LOCALAPPDATA%\VanDang\MeetingRecorder\app. Settings, logs (%LOCALAPPDATA%\VanDang\MeetingRecorder\data) and
    recordings are never deleted. Succeeds and does nothing
    when the app isn't installed.

    Exit codes: 0 removed, 1 error, 3 refused because the app is busy (a recording is starting, running or saving)
    or didn't answer the close request.

.PARAMETER NoDataWarning
    Don't print where the data was kept (used by deploy.ps1, which installs again right away).

.PARAMETER Force
    Close the app even while recording. The recording is saved and ends.
#>
param([switch]$NoDataWarning, [switch]$Force)

$ErrorActionPreference = 'Stop'

$AppId    = 'MeetingRecorder'
$AppName  = 'Meeting Recorder'
$ExeName  = "$AppId.exe"
$Publisher = 'VanDang'
$Root     = Join-Path $env:LOCALAPPDATA "$Publisher\$AppId"
$AppDir   = Join-Path $Root 'app'
$DataDir  = Join-Path $Root 'data'
$RunKey   = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# Standard section 3.5: ask the app to close, and let it refuse while it is busy.
function Stop-App {
    # This account's processes only; --meeting-status and --audio-status are short diagnostic runs.
    $own = @(Get-CimInstance Win32_Process -Filter "Name = '$ExeName'" | Where-Object {
        $_.CommandLine -notmatch '--meeting-status|--audio-status' -and
            (Invoke-CimMethod -InputObject $_ -MethodName GetOwner).User -eq $env:USERNAME
    })
    if ($own.Count -eq 0) { return }

    Write-Step 'Asking Meeting Recorder to close'
    $refused = $null
    try {
        $refused = [Threading.EventWaitHandle]::OpenExisting("Local\$AppId-ExitRefused")
        $null = $refused.Reset()
        $exit = [Threading.EventWaitHandle]::OpenExisting("Local\$AppId-Exit")
        $null = $exit.Set()
        $exit.Dispose()
    } catch {
        # An app from before standard v2.1 has no close events; it counts as not answering.
    }
    $answer = 'none'
    $deadline = (Get-Date).AddSeconds(10)
    while ((Get-Date) -lt $deadline) {
        if (-not (Get-Process -Id $own.ProcessId -ErrorAction SilentlyContinue)) { $answer = 'closed'; break }
        if ($refused -and $refused.WaitOne(250)) { $answer = 'refused'; break }
        if (-not $refused) { Start-Sleep -Milliseconds 250 }
    }
    if ($refused) { $refused.Dispose() }
    if ($answer -eq 'closed') { return }

    if (-not $Force) {
        if ($answer -eq 'refused') {
            Write-Host "$AppName is busy (a recording is starting, running or saving). Let it finish or stop it, then run this again, or use -Force (the recording is saved and ends)." -ForegroundColor Yellow
        } else {
            Write-Host "$AppName did not answer the close request within 10 s. Exit it from the tray menu, then run this again, or use -Force." -ForegroundColor Yellow
        }
        exit 3
    }

    Write-Step "Closing $AppName (-Force)"
    # Killing the tray process ends a recording safely: the worker sees its stdin close, saves the files and exits.
    # A recording finisher (--finish) is left to complete too; one killed is redone when the app next starts.
    $workers = @($own | Where-Object { $_.CommandLine -match '--record|--finish' })
    $own | Where-Object { $_.CommandLine -notmatch '--record|--finish' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($workers.Count -gt 0) {
        Write-Step 'Waiting for the recording to be saved'
        Wait-Process -Id $workers.ProcessId -Timeout 600 -ErrorAction SilentlyContinue
        if (Get-Process -Id $workers.ProcessId -ErrorAction SilentlyContinue) {
            throw 'The recording is still being saved after 10 minutes. Nothing was changed; run this again later.'
        }
    }
}

# Read before anything is removed: where the recordings go.
$recordingsRoot = Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Meeting Recordings'
$settingsFile = Join-Path $DataDir 'settings.json'
if (Test-Path $settingsFile) {
    try {
        $configured = (Get-Content $settingsFile -Raw | ConvertFrom-Json).RecordingsRoot
        if ($configured) { $recordingsRoot = $configured }
    } catch { }
}

# 1. Stop
Stop-App

# 2. Registration, including entries from earlier install methods
Remove-ItemProperty $RunKey -Name $AppId -ErrorAction SilentlyContinue
Remove-Item $Shortcut -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $AppId -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) "$AppName.lnk") -Force -ErrorAction SilentlyContinue

# 3. App folder, then the app's and the publisher's folders when nothing else is left in them
if (Test-Path $AppDir) {
    Write-Step "Removing $AppDir"
    Remove-Item $AppDir -Recurse -Force
}
foreach ($dir in $Root, (Split-Path -Parent $Root)) {
    if ((Test-Path $dir) -and -not (Get-ChildItem $dir -Force)) { Remove-Item $dir -Force }
}

# 4. Data is kept
if (-not $NoDataWarning) {
    Write-Host "$AppName has been uninstalled." -ForegroundColor Green
    if (Test-Path $DataDir) {
        Write-Host "WARNING: $AppName data was kept in $DataDir" -ForegroundColor Yellow
        Write-Host "         Delete that folder yourself if you no longer need it." -ForegroundColor Yellow
    }
    if (Test-Path $recordingsRoot) {
        Write-Host "WARNING: Recordings were kept in $recordingsRoot" -ForegroundColor Yellow
    }
}
