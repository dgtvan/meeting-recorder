<#
.SYNOPSIS
    Builds Meeting Recorder into dist\ (release configuration).

.DESCRIPTION
    Follows the App deployment standard v2.1, section 3.1 (https://github.com/dgtvan/local-app-deployment-strategy).
    Builds whatever is in the working directory. Touches nothing outside the repo.

    Exit codes: 0 built, 1 error.
#>
$ErrorActionPreference = 'Stop'

$Repo    = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $Repo 'src\MeetingRecorder'
$Dist    = Join-Path $Repo 'dist'
$ExeName = 'MeetingRecorder.exe'

function Write-Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (Test-Path $Dist) {
    Write-Step 'Removing the previous build'
    Remove-Item $Dist -Recurse -Force
}

Write-Step "Building into $Dist"
& dotnet publish $Project -c Release -o $Dist --nologo -v quiet | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed (dotnet exit code $LASTEXITCODE)." }
if (-not (Test-Path (Join-Path $Dist $ExeName))) { throw "Build finished but $ExeName is missing from $Dist." }

Write-Host "Built $Dist\$ExeName" -ForegroundColor Green
