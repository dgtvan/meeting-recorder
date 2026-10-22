<#
.SYNOPSIS
    Builds Meeting Recorder and publishes the build for every account's running app.

.DESCRIPTION
    Follows the App deployment standard v1.2, section 5.1 (https://github.com/dgtvan/local-app-deployment-strategy).
    Touches only the publish folder D:\Apps\MeetingRecorder, never an account's install, registry or profile.

    1. Builds into a new folder D:\Apps\MeetingRecorder\builds\<build id>\ and writes build-id.txt last.
    2. Keeps the newest 3 ready builds plus any build an account has installed or is installing (per
       pickup\<user>\status.txt); removes incomplete build folders older than an hour.
    3. Waits up to 30 seconds for each account's app to report on the new build in pickup\<user>\status.txt.

    Exit codes: 0 every account runs the new build, 1 error (nothing published), 5 an account is waiting until
    its app is idle, 6 an account didn't answer or nobody has installed the app yet, 7 an account failed to
    install it. When accounts differ, the most serious wins: 7, 5, 6, 0.
#>
$ErrorActionPreference = 'Stop'

$AppId       = 'MeetingRecorder'
$PublishRoot = "D:\Apps\$AppId"
$BuildsDir   = Join-Path $PublishRoot 'builds'
$PickupDir   = Join-Path $PublishRoot 'pickup'
$Project     = Join-Path (Split-Path -Parent $PSScriptRoot) 'src\MeetingRecorder'
$KeepBuilds  = 3
$IdPattern   = '^\d{8}T\d{6}Z-[0-9a-f]{8}$'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Get-ReadyBuilds {
    Get-ChildItem $BuildsDir -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match $IdPattern } |
        Where-Object {
            $marker = Join-Path $_.FullName 'build-id.txt'
            (Test-Path $marker) -and ((Get-Content $marker -Raw).Trim() -eq $_.Name)
        } |
        Sort-Object Name -Descending
}

# 1. Build into a new build folder
$buildId = '{0:yyyyMMddTHHmmssZ}-{1}' -f (Get-Date).ToUniversalTime(), ([guid]::NewGuid().ToString('N').Substring(0, 8))
$buildDir = Join-Path $BuildsDir $buildId
New-Item $buildDir -ItemType Directory -Force | Out-Null

Write-Step "Building $buildId"
& dotnet publish $Project -c Release -o $buildDir --nologo -v quiet | Out-Host
if ($LASTEXITCODE -ne 0) {
    Remove-Item $buildDir -Recurse -Force -ErrorAction SilentlyContinue
    throw 'Build failed. Nothing was published.'
}
Copy-Item (Join-Path $PSScriptRoot 'apply-update.cmd') $buildDir
Copy-Item (Join-Path $PSScriptRoot 'uninstall.ps1') $buildDir
Set-Content (Join-Path $buildDir 'update-source.txt') $PublishRoot -NoNewline -Encoding ASCII
# Written last: apps ignore a build folder until this marker matches its name.
Set-Content (Join-Path $buildDir 'build-id.txt') $buildId -NoNewline -Encoding ASCII

# 2. Retention, and the pickup folder every account reports into
$inUse = @(Get-ChildItem $PickupDir -Directory -ErrorAction SilentlyContinue | ForEach-Object {
    $lines = @(Get-Content (Join-Path $_.FullName 'status.txt') -ErrorAction SilentlyContinue)
    if ($lines.Count -ge 2) { $lines[1] }
    if ($lines.Count -ge 3 -and $lines[0] -eq 'applying') { $lines[2] }
} | Where-Object { $_ -match $IdPattern })
Get-ReadyBuilds | Select-Object -Skip $KeepBuilds | Where-Object { $inUse -notcontains $_.Name } | ForEach-Object {
    Write-Step "Removing old build $($_.Name)"
    Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
}
$ready = @(Get-ReadyBuilds | ForEach-Object Name)
Get-ChildItem $BuildsDir -Directory | Where-Object {
    $ready -notcontains $_.Name -and $_.Name -ne $buildId -and $_.LastWriteTime -lt (Get-Date).AddHours(-1)
} | ForEach-Object {
    Write-Step "Removing incomplete build folder $($_.Name)"
    Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Path $PickupDir)) {
    New-Item $PickupDir -ItemType Directory | Out-Null
    # Users may create a subfolder here; only its creator can change it (Users: create folders, CREATOR OWNER: modify).
    & icacls $PickupDir /grant '*S-1-5-32-545:(AD)' '*S-1-3-0:(OI)(CI)(IO)M' | Out-Null
}

Write-Host "Published $buildId to $BuildsDir" -ForegroundColor Green

# 3. Report what each account's app did with it
$accounts = @(Get-ChildItem $PickupDir -Directory -ErrorAction SilentlyContinue)
if ($accounts.Count -eq 0) {
    Write-Host 'No account has installed the app yet. Each user runs install.cmd once from their own account.' -ForegroundColor Yellow
    exit 6
}

Write-Step 'Waiting for each account''s app to report (up to 30 s)'
$results = @{}
$deadline = (Get-Date).AddSeconds(30)
do {
    foreach ($account in $accounts) {
        $statusFile = Join-Path $account.FullName 'status.txt'
        $lines = @(Get-Content $statusFile -ErrorAction SilentlyContinue)
        if ($lines.Count -lt 3) { continue }
        $state = $lines[0]
        if ($state -eq 'running' -and $lines[1] -eq $buildId) { $results[$account.Name] = 'running' }
        elseif ($state -like 'failed*' -and $lines[2] -eq $buildId) { $results[$account.Name] = $state }
        elseif ($state -like 'waiting*' -and $state -like "*$buildId*") { $results[$account.Name] = $state }
    }
    $settled = $results.Count -eq $accounts.Count
    if (-not $settled) { Start-Sleep -Milliseconds 500 }
} while (-not $settled -and (Get-Date) -lt $deadline)

$exitCode = 0
foreach ($account in $accounts) {
    $result = $results[$account.Name]
    if (-not $result) { $result = 'no answer (app not running?); it installs the build when it next starts' }
    Write-Host "$($account.Name): $result"
    $code = if ($result -eq 'running') { 0 } elseif ($result -like 'failed*') { 7 } elseif ($result -like 'waiting*') { 5 } else { 6 }
    $rank = @{ 0 = 0; 6 = 1; 5 = 2; 7 = 3 }
    if ($rank[$code] -gt $rank[$exitCode]) { $exitCode = $code }
}
exit $exitCode
