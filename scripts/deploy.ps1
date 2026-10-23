<#
.SYNOPSIS
    Builds Meeting Recorder, uninstalls it and installs the new build for the current account.

.DESCRIPTION
    Follows the App deployment standard v2.2, section 3.4 (https://github.com/dgtvan/local-app-deployment-strategy).
    Stops at the first failure; a failed build stops it before anything is uninstalled.

    Exit codes: 0 installed and running, 1 error, 3 refused because the app is busy (a recording is starting,
    running or saving) or didn't answer.

.PARAMETER Force
    Passed to uninstall and install: close the app even while recording. The recording is saved and ends.
#>
param([switch]$Force)

$ErrorActionPreference = 'Stop'

function Invoke-Step($script, [string[]]$arguments) {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot $script) @arguments
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$forceArg = @(if ($Force) { '-Force' })
Invoke-Step 'build.ps1' @()
Invoke-Step 'uninstall.ps1' (@('-NoDataWarning') + $forceArg)
Invoke-Step 'install.ps1' $forceArg
