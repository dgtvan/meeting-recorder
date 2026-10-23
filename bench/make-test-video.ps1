<#
.SYNOPSIS
Generates the synthetic meeting video used by bench.ps1: a 2560x1440 text slide that changes colour and number every
20 s, a clock, and a moving 480x270 "camera" tile, at 30 fps. Needs ffmpeg on PATH.
#>
param([string]$Output = 'meeting-1440p.mp4', [int]$Seconds = 90)

$words = 'quarterly roadmap budget review hiring release customer feedback latency migration testing rollout design metrics security onboarding' -split ' '
$random = [Random]::new(1)
$slide = Join-Path ([IO.Path]::GetTempPath()) 'meeting-recorder-slide.txt'
$lines = 1..22 | ForEach-Object { (1..12 | ForEach-Object { $words[$random.Next($words.Length)] }) -join ' ' }
Set-Content -Path $slide -Value $lines
$slideArg = $slide.Replace('\', '/').Replace(':', '\:')

$filter = "[0]drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':textfile='$slideArg':fontsize=40:fontcolor=white:line_spacing=14:x=120:y=200," +
    "drawtext=fontfile='C\:/Windows/Fonts/arialbd.ttf':text='Slide %{eif\:floor(t/20)+1\:d}':fontsize=72:fontcolor=0x7fc4ff:x=120:y=80," +
    "drawtext=fontfile='C\:/Windows/Fonts/arial.ttf':text='%{pts\:hms}':fontsize=32:fontcolor=gray:x=2200:y=80," +
    "hue=h='floor(t/20)*40'[bg];[bg][1]overlay=x=W-w-60:y=H-h-60,format=yuv420p"
ffmpeg -v error -y -f lavfi -i "color=c=0x202428:s=2560x1440:r=30:d=$Seconds" -f lavfi -i "testsrc2=s=480x270:r=30:d=$Seconds" `
    -filter_complex $filter -c:v libx264 -crf 10 -preset veryfast $Output
Remove-Item $slide
