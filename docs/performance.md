# Performance log

Measurements behind the recording defaults, newest first. Re-run with [bench/](../bench/) to compare a change
against these numbers:

```powershell
cd bench
.\make-test-video.ps1                       # meeting-1440p.mp4, 90 s
.\bench.ps1 -Exe <path>\MeetingRecorder.exe -Source meeting-1440p.mp4 -Label <name> [-MaxHeight 0] [-Compress false] [-Quality 60]
```

`bench.ps1` replays the test video through the recorder (`--record --file`), so nothing is captured from the
screen. Its CPU and GPU figures include decoding the source video, so only compare them between runs.

## 2026-10-23: downscale to 1080p, AAC audio

**Machine:** 16 logical cores, Intel Arc 140T integrated GPU (hardware H.264), 2560x1440 display.
**Source:** synthetic meeting video, 2560x1440 at 30 fps: a text slide that changes every 20 s, a clock, and a
moving 480x270 camera tile. Recorded at 15 fps (variable), 60 s per run.

### Before

H.264 Main profile, quality 60, encoded at the source size; audio as 16-bit WAV (system stereo, mic mono).
On a calm 60 s test, audio was 97% of the folder (16.5 of 17 MB).

### Results (per minute of recording)

| Run | Profile | Max height | Audio | Video MB | Audio MB | CPU % | GPU % | Save s |
|---|---|---|---|---|---|---|---|---|
| baseline r1 | Main | none (2560x1440) | WAV | 3.63 | 16.54 | 4.67 | 83.1 | 0.3 |
| baseline r2 | Main | none | WAV | 3.87 | 16.55 | 4.05 | 71.2 | 0.4 |
| 1080 r1 | Main | 1080 (1920x1080) | AAC | 2.66 | 1.63 | 3.83 | 74.6 | 1.5 |
| 1080 r2 | Main | 1080 | AAC | 2.61 | 1.63 | 4.37 | 76.8 | 1.4 |
| High r1 | High | none | WAV | 3.87 | 16.57 | 3.91 | 70.0 | 0.6 |
| High r2 | High | none | WAV | 3.74 | 16.57 | 4.52 | 78.7 | 0.5 |
| High 1080 r1 | High | 1080 | AAC | 2.72 | 1.63 | 3.84 | 66.4 | 1.0 |
| High 1080 r2 | High | 1080 | AAC | 2.73 | 1.63 | 3.87 | 65.7 | 1.1 |
| quality 50 | Main | 1080 | AAC | 2.04 | 1.63 | 4.84 | 78.4 | 1.1 |
| quality 70 | Main | 1080 | AAC | 2.79 | 1.63 | 4.89 | 74.8 | 1.6 |

Save time (stop until all files are final) for a 180 s recording: 3.7 s with the two audio files converted one
after the other, 5.7 s converting both at once.

### Findings

- **AAC audio:** 16.5 → 1.6 MB/min (about 10x smaller); with it, a recording folder is about 4.3 instead of 20 MB/min.
  Conversion runs after stop and adds about 1–1.5 s of saving per recorded minute. Nothing changes while recording.
- **Scaling to 1080 lines:** video 3.6–3.9 → 2.6–2.7 MB/min (about 28% smaller), with slightly lower CPU and GPU.
  Slide text stays readable when viewed at full size.
- **High profile:** no measurable gain over Main with this GPU encoder in quality mode (3.74–3.87 vs 3.63–3.87 MB/min).
- **Quality:** 50 saves another 23% but shows visible ringing around text; 70 costs 5% more for no visible gain.
- **Parallel audio conversion:** slower than one file after the other.
- Early tests that recorded a live window are not included. Windows Graphics Capture isn't available from the test
  account, so those runs fell back to cropped display capture of a busy desktop, and their numbers varied 4x
  between identical runs.

### Decisions

- `MaxVideoHeight` = 1080 (new setting; 0 turns scaling off).
- `CompressAudio` = true (new setting): AAC 128 kbps stereo (system) and 96 kbps mono (mic), the lowest bitrates the
  Windows AAC encoder offers. The WAV is kept if conversion fails, and is crash-safe while recording.
- Profile stays Main, quality stays 60, frame rate stays 15 (variable).
- The 2 min save timeout now gets 5 s extra per recorded minute, so a long meeting isn't killed while its
  audio is converted.
