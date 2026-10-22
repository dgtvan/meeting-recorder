<#
.SYNOPSIS
    Removes Meeting Recorder for the current account.

.DESCRIPTION
    Follows the App deployment standard v1.2, section 6 (https://github.com/dgtvan/local-app-deployment-strategy).
    Stops the app, removes the start-at-sign-in entry, Start Menu shortcut, Settings > Apps entry, install folder
    and this account's pickup folder. Settings, logs and recordings are kept unless asked otherwise.
    A copy of this script lives in the install folder, so Settings > Apps can run it without the repo.

    Exit codes: 0 removed, 1 error, 3 refused because a recording is in progress.

.PARAMETER RemoveData
    Also remove settings (%APPDATA%\MeetingRecorder) and local data (%LOCALAPPDATA%\MeetingRecorder).

.PARAMETER RemoveRecordings
    Also remove the recordings folder.

.PARAMETER Force
    Uninstall even while a recording is in progress. The recording is saved and ends.
#>
param([switch]$RemoveData, [switch]$RemoveRecordings, [switch]$Force)

$ErrorActionPreference = 'Stop'

$AppId        = 'MeetingRecorder'
$AppName      = 'Meeting Recorder'
$ExeName      = "$AppId.exe"
$InstallDir   = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
$SettingsDir  = Join-Path $env:APPDATA $AppId
$LocalDataDir = Join-Path $env:LOCALAPPDATA $AppId
$RunKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
$Shortcut     = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# Read these before the install folder and settings are removed.
$publishRoot = "D:\Apps\$AppId"
$sourceFile = Join-Path $InstallDir 'update-source.txt'
if (Test-Path $sourceFile) { $publishRoot = (Get-Content $sourceFile -Raw).Trim() }
$recordingsRoot = Join-Path ([Environment]::GetFolderPath('MyVideos')) 'Meeting Recordings'
$settingsFile = Join-Path $SettingsDir 'settings.json'
if (Test-Path $settingsFile) {
    try {
        $configured = (Get-Content $settingsFile -Raw | ConvertFrom-Json).RecordingsRoot
        if ($configured) { $recordingsRoot = $configured }
    } catch { }
}

# 1. Stop this account's copy. Only this account's processes expose their command line.
$own = @(Get-CimInstance Win32_Process -Filter "Name = '$ExeName'" | Where-Object { $_.CommandLine })
if ($own.Count -gt 0) {
    $recording = @($own | Where-Object { $_.CommandLine -match '--record' })
    if ($recording.Count -gt 0 -and -not $Force) {
        Write-Host 'A recording is in progress. Stop it first, or run again with -Force (the recording is saved and ends).' -ForegroundColor Yellow
        exit 3
    }
    Write-Step 'Stopping the app'
    # Closing the tray process ends a recording safely: the worker sees its stdin close, saves the files and exits.
    $own | Where-Object { $_.CommandLine -notmatch '--record' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    $recording | ForEach-Object { Wait-Process -Id $_.ProcessId -Timeout 60 -ErrorAction SilentlyContinue }
}

# 2. Registration, including legacy startup entries
Write-Step 'Removing start at sign-in, Start Menu shortcut and Settings > Apps entry'
Remove-ItemProperty $RunKey -Name $AppId -ErrorAction SilentlyContinue
Remove-Item $Shortcut -Force -ErrorAction SilentlyContinue
Remove-Item $UninstallKey -Recurse -Force -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $AppId -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) "$AppName.lnk") -Force -ErrorAction SilentlyContinue

# 3. Install folder. This script may be running from it; PowerShell has already read it, so deleting is safe.
Write-Step "Removing $InstallDir"
Set-Location $env:TEMP
Remove-Item $InstallDir -Recurse -Force -ErrorAction SilentlyContinue

# 4. Data, only when asked
if ($RemoveData) {
    Write-Step 'Removing settings and local data'
    Remove-Item $SettingsDir -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $LocalDataDir -Recurse -Force -ErrorAction SilentlyContinue
}
if ($RemoveRecordings) {
    Write-Step "Removing recordings in $recordingsRoot"
    Remove-Item $recordingsRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# 5. This account's pickup folder, so publishing stops waiting for it
Remove-Item (Join-Path $publishRoot "pickup\$env:USERNAME") -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
Write-Host 'Meeting Recorder has been uninstalled.' -ForegroundColor Green
if (-not $RemoveData) { Write-Host "Settings were kept in $SettingsDir (use -RemoveData to remove them)." }
if (-not $RemoveRecordings -and (Test-Path $recordingsRoot)) { Write-Host "Recordings were kept in $recordingsRoot." }
