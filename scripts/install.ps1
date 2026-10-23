<#
.SYNOPSIS
    Installs the build in dist\ for the current account.

.DESCRIPTION
    Follows the App deployment standard v2.1, section 3.2 (https://github.com/dgtvan/local-app-deployment-strategy).
    Copies dist\ to %LOCALAPPDATA%\MeetingRecorder\app, starts it at sign-in (HKCU Run) and from the Start Menu, and
    starts it. It does no app initialization (the app does that itself at startup) and never touches the data
    folder. Re-running it is safe.

    Exit codes: 0 installed (and started unless -NoStart), 1 error, 3 refused because the app is busy (a recording
    is starting, running or saving) or didn't answer the close request.

.PARAMETER NoStart
    Don't start the app after installing.

.PARAMETER Force
    Close the app even while recording. The recording is saved and ends.
#>
param([switch]$NoStart, [switch]$Force)

$ErrorActionPreference = 'Stop'

$AppId      = 'MeetingRecorder'
$AppName    = 'Meeting Recorder'
$ExeName    = "$AppId.exe"
$Dist       = Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'
$Root       = Join-Path $env:LOCALAPPDATA $AppId
$AppDir     = Join-Path $Root 'app'
$DataDir    = Join-Path $Root 'data'
$ExePath    = Join-Path $AppDir $ExeName
$Launch     = "`"$ExePath`" --data-dir `"$DataDir`""
$RunKey     = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$Shortcut   = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Install-WithWinget($id, $label) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "$label is missing and winget is not available. Install it manually, then run this again."
    }
    Write-Step "Installing $label (Windows may ask for permission)"
    winget install --id $id --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "Installing $label failed (winget exit code $LASTEXITCODE)." }
}

# Standard section 3.5: ask the app to close, and let it refuse while it is busy.
function Stop-App {
    # This account's processes only; --teams-status is a short diagnostic run.
    $own = @(Get-CimInstance Win32_Process -Filter "Name = '$ExeName'" | Where-Object {
        $_.CommandLine -notmatch '--teams-status' -and
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
    $workers = @($own | Where-Object { $_.CommandLine -match '--record' })
    $own | Where-Object { $_.CommandLine -notmatch '--record' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    if ($workers.Count -gt 0) {
        Write-Step 'Waiting for the recording to be saved'
        Wait-Process -Id $workers.ProcessId -Timeout 600 -ErrorAction SilentlyContinue
        if (Get-Process -Id $workers.ProcessId -ErrorAction SilentlyContinue) {
            throw 'The recording is still being saved after 10 minutes. Nothing was changed; run this again later.'
        }
    }
}

# 1. Build present
if (-not (Test-Path (Join-Path $Dist $ExeName))) {
    Write-Host "No build in $Dist. Run build.cmd first." -ForegroundColor Red
    exit 1
}

# 2. Prerequisites
if (-not (Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App" -Directory -Filter '10.*' -ErrorAction SilentlyContinue)) {
    Install-WithWinget 'Microsoft.DotNet.DesktopRuntime.10' '.NET 10 Desktop Runtime'
}
$vc = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue
if (-not ($vc -and $vc.Installed -eq 1)) {
    Install-WithWinget 'Microsoft.VCRedist.2015+.x64' 'Visual C++ x64 runtime'
}

# 3. Stop
Stop-App

# 4. Copy. The guard makes sure a wrong path can never mirror over the data folder.
if (-not $AppDir.TrimEnd('\').EndsWith("\$AppId\app", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to install into $AppDir (it must end in \$AppId\app)."
}
Write-Step "Installing to $AppDir"
New-Item $AppDir -ItemType Directory -Force | Out-Null
& robocopy $Dist $AppDir /MIR /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the build failed (robocopy exit code $LASTEXITCODE)." }

# 5. Register
Write-Step 'Registering start at sign-in and the Start Menu shortcut'
Set-ItemProperty $RunKey -Name $AppId -Value $Launch

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($Shortcut)
$link.TargetPath = $ExePath
$link.Arguments = "--data-dir `"$DataDir`""
$link.WorkingDirectory = $AppDir
$link.Description = 'Record a window or the screen with separate system and microphone audio'
$link.Save()

# 6. Entries from earlier install methods (standard v1.x and before). Their data is left for the app to copy.
Unregister-ScheduledTask -TaskName $AppId -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) "$AppName.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId" -Recurse -Force -ErrorAction SilentlyContinue
$legacyInstall = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
if (Test-Path $legacyInstall) {
    Write-Step "Removing the old install in $legacyInstall"
    Remove-Item $legacyInstall -Recurse -Force
}

# 7. Start
if ($NoStart) {
    Write-Host "Installed to $AppDir (not started). It starts at sign-in or from the Start Menu." -ForegroundColor Green
    exit 0
}
Write-Step "Starting $AppName"
Start-Process $ExePath -ArgumentList '--data-dir', "`"$DataDir`"" -WorkingDirectory $AppDir
Write-Host "Installed to $AppDir. $AppName is in the system tray and starts at sign-in." -ForegroundColor Green
