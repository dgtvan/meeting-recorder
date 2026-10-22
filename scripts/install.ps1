<#
.SYNOPSIS
    Installs Meeting Recorder for the current account from the latest published build.

.DESCRIPTION
    Follows the App deployment standard v1.2, section 5.2 (https://github.com/dgtvan/local-app-deployment-strategy).
    Run once by each account that uses the app, from that account; after that the app updates itself.
    It never builds and never writes to the publish folder, and it does no app initialization (the app does that
    itself at startup). Re-running it repairs the install with the latest build.

    Exit codes: 0 installed (and started unless -NoStart), 1 error, 3 refused because a recording is in progress.

.PARAMETER NoStart
    Don't start the app after installing.

.PARAMETER Force
    Install even while a recording is in progress. The recording is saved and ends.
#>
param([switch]$NoStart, [switch]$Force)

$ErrorActionPreference = 'Stop'

$AppId        = 'MeetingRecorder'
$AppName      = 'Meeting Recorder'
$ExeName      = "$AppId.exe"
$PublishRoot  = "D:\Apps\$AppId"
$BuildsDir    = Join-Path $PublishRoot 'builds'
$InstallDir   = Join-Path $env:LOCALAPPDATA "Programs\$AppId"
$ExePath      = Join-Path $InstallDir $ExeName
$RunKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$UninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$AppId"
$Shortcut     = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
$IdPattern    = '^\d{8}T\d{6}Z-[0-9a-f]{8}$'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Install-WithWinget($id, $label) {
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
        throw "$label is missing and winget is not available. Install it manually, then run this again."
    }
    Write-Step "Installing $label (Windows may ask for permission)"
    winget install --id $id --exact --silent --accept-package-agreements --accept-source-agreements
    if ($LASTEXITCODE -ne 0) { throw "Installing $label failed (winget exit code $LASTEXITCODE)." }
}

# 1. Prerequisites
if (-not (Get-ChildItem "$env:ProgramFiles\dotnet\shared\Microsoft.WindowsDesktop.App" -Directory -Filter '10.*' -ErrorAction SilentlyContinue)) {
    Install-WithWinget 'Microsoft.DotNet.DesktopRuntime.10' '.NET 10 Desktop Runtime'
}
$vc = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64' -ErrorAction SilentlyContinue
if (-not ($vc -and $vc.Installed -eq 1)) {
    Install-WithWinget 'Microsoft.VCRedist.2015+.x64' 'Visual C++ x64 runtime'
}

# 2. Latest ready build
$latest = Get-ChildItem $BuildsDir -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match $IdPattern } |
    Where-Object {
        $marker = Join-Path $_.FullName 'build-id.txt'
        (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $_.Name)
    } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $latest) {
    Write-Host "No published build in $BuildsDir. Run publish.cmd first." -ForegroundColor Red
    exit 1
}

# 3. Stop this account's copy. Only this account's processes expose their command line.
$own = @(Get-CimInstance Win32_Process -Filter "Name = '$ExeName'" | Where-Object { $_.CommandLine })
if ($own.Count -gt 0) {
    $recording = @($own | Where-Object { $_.CommandLine -match '--record' })
    if ($recording.Count -gt 0 -and -not $Force) {
        Write-Host 'A recording is in progress. Stop it first, or run again with -Force (the recording is saved and ends).' -ForegroundColor Yellow
        exit 3
    }
    Write-Step 'Stopping the running app'
    # Closing the tray process ends a recording safely: the worker sees its stdin close, saves the files and exits.
    $own | Where-Object { $_.CommandLine -notmatch '--record' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    $recording | ForEach-Object { Wait-Process -Id $_.ProcessId -Timeout 60 -ErrorAction SilentlyContinue }
}

# 4. Copy
Write-Step "Installing build $($latest.Name) to $InstallDir"
New-Item $InstallDir -ItemType Directory -Force | Out-Null
& robocopy $latest.FullName $InstallDir /MIR /R:5 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "Copying the build failed (robocopy exit code $LASTEXITCODE)." }

# 5. Register
Write-Step 'Registering start at sign-in, Start Menu shortcut and Settings > Apps entry'
Set-ItemProperty $RunKey -Name $AppId -Value "`"$ExePath`""

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($Shortcut)
$link.TargetPath = $ExePath
$link.WorkingDirectory = $InstallDir
$link.Description = 'Record a window or the screen with separate system and microphone audio'
$link.Save()

New-Item $UninstallKey -Force | Out-Null
Set-ItemProperty $UninstallKey -Name 'DisplayName' -Value $AppName
Set-ItemProperty $UninstallKey -Name 'DisplayIcon' -Value $ExePath
Set-ItemProperty $UninstallKey -Name 'DisplayVersion' -Value $latest.Name
Set-ItemProperty $UninstallKey -Name 'InstallLocation' -Value $InstallDir
Set-ItemProperty $UninstallKey -Name 'UninstallString' -Value "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
Set-ItemProperty $UninstallKey -Name 'NoModify' -Value 1 -Type DWord
Set-ItemProperty $UninstallKey -Name 'NoRepair' -Value 1 -Type DWord
$sizeKb = [int]((Get-ChildItem $InstallDir -Recurse | Measure-Object Length -Sum).Sum / 1KB)
Set-ItemProperty $UninstallKey -Name 'EstimatedSize' -Value $sizeKb -Type DWord

# 6. Legacy startup entries from earlier install methods
Unregister-ScheduledTask -TaskName $AppId -Confirm:$false -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Startup')) "$AppName.lnk") -Force -ErrorAction SilentlyContinue

# 7. Start
if ($NoStart) {
    Write-Host "Installed build $($latest.Name) (not started). It starts at sign-in or from the Start Menu." -ForegroundColor Green
    exit 0
}
Write-Step 'Starting Meeting Recorder'
Start-Process $ExePath -WorkingDirectory $InstallDir
Write-Host "Installed build $($latest.Name). Meeting Recorder is in the system tray, starts at sign-in and updates itself." -ForegroundColor Green
