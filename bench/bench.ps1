<#
.SYNOPSIS
Records a video file through MeetingRecorder's encoder settings and reports CPU, GPU, RAM, save time and file sizes.

.DESCRIPTION
Uses the worker's --file source, so nothing is captured from the screen. Writes a temporary settings.json for the
run and restores the previous one afterwards. The recording folder is left in place; delete it when done.
CPU and GPU include decoding the source file, so compare them between runs rather than reading them as absolute.

.EXAMPLE
.\bench.ps1 -Exe ..\src\MeetingRecorder\bin\x64\Release\net10.0-windows\win-x64\MeetingRecorder.exe -Source meeting-1440p.mp4 -Label test
#>
param(
    [Parameter(Mandatory)][string]$Exe,
    [Parameter(Mandatory)][string]$Source,
    [string]$Label = 'bench',
    [int]$Seconds = 60,
    [int]$MaxHeight = 1080,
    [string]$Compress = 'true',
    [int]$Quality = 60
)

$settingsPath = Join-Path $env:APPDATA 'MeetingRecorder\settings.json'
$backup = if (Test-Path $settingsPath) { Get-Content -Raw $settingsPath } else { $null }
New-Item -ItemType Directory -Force (Split-Path $settingsPath) | Out-Null
Set-Content -Path $settingsPath -Value ('{{"SchemaVersion":1,"MaxVideoHeight":{0},"CompressAudio":{1},"VideoQuality":{2}}}' -f $MaxHeight, $Compress, $Quality)

try {
    $psi = New-Object System.Diagnostics.ProcessStartInfo (Resolve-Path $Exe)
    $psi.Arguments = "--record --file `"$(Resolve-Path $Source)`" --name bench-$Label"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $folder = $null
    while (($line = $p.StandardOutput.ReadLine()) -ne $null) {
        if ($line -like 'folder *') { $folder = $line.Substring(7) }
        if ($line -eq 'started') { break }
        if ($line -like 'failed*') { "[$Label] $line"; return }
    }

    $cpuStart = $p.TotalProcessorTime
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $peak = 0
    $gpuSamples = @()
    while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
        try {
            $c = Get-Counter "\GPU Engine(pid_$($p.Id)_*)\Utilization Percentage" -ErrorAction Stop
            $gpuSamples += ($c.CounterSamples | Measure-Object CookedValue -Sum).Sum
        } catch { Start-Sleep -Milliseconds 500 }
        $p.Refresh(); if ($p.WorkingSet64 -gt $peak) { $peak = $p.WorkingSet64 }
    }
    $p.Refresh()
    $elapsed = $sw.Elapsed.TotalSeconds
    $cpuRec = ($p.TotalProcessorTime - $cpuStart).TotalSeconds

    $stopAt = [Diagnostics.Stopwatch]::StartNew()
    $p.StandardInput.WriteLine('stop'); $p.StandardInput.Close()
    $out = @()
    while (($line = $p.StandardOutput.ReadLine()) -ne $null) { $out += $line }
    $p.WaitForExit()
    $saveSec = $stopAt.Elapsed.TotalSeconds
    $gpu = if ($gpuSamples) { ($gpuSamples | Measure-Object -Average).Average } else { -1 }

    $files = Get-ChildItem $folder
    $video = ($files | Where-Object Name -eq 'video.mp4').Length
    $audio = ($files | Where-Object Name -like 'audio-*' | Measure-Object Length -Sum).Sum
    "[{0}] CPU {1:N2}% of {2} cores, GPU {3:N1}% (sum of engines), RAM {4:N0} MB, save {5:N1}s, video {6:N2} MB/min, audio {7:N2} MB/min ({8}) | {9}" -f `
        $Label, (100 * $cpuRec / $elapsed / [Environment]::ProcessorCount), [Environment]::ProcessorCount, $gpu,
        ($peak / 1MB), $saveSec, ($video / 1MB * 60 / $elapsed), ($audio / 1MB * 60 / $elapsed),
        (($files | Where-Object Name -like 'audio-*').Name -join ','), ($out -join ' / ')
    "  folder: $folder"
}
finally {
    if ($null -ne $backup) { Set-Content -Path $settingsPath -Value $backup -NoNewline } else { Remove-Item $settingsPath }
}
