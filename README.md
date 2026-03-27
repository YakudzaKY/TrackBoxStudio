# TrackBoxStudio

TrackBoxStudio is a standalone C# / WPF desktop app for manual watermark cleanup.

The project is manual-first: the editor is the source of truth, auto-detection is not required, and only user-authored boxes are processed.

## What it does

- Opens a video or image file
- Lets you create one or more timeline tracks
- Lets you draw a box on a frame and save it as an enabled keyframe
- Lets you write a disabled keyframe when the watermark disappears
- Shows enabled and disabled ranges directly on each track
- Saves and reopens full timeline sessions as `.trackbox.json` project files
- Runs real LaMa inpainting only on the boxes that are enabled on the active frame
- Can preserve video audio on export when `ffmpeg.exe` is available in `PATH`
- Includes stable-mask tuning and preview tools for local inpaint coverage checks

## Current stack

- .NET 10
- WPF
- OpenCvSharp for frame access and media IO
- Python sidecar runner with `iopaint` LaMa for final cleanup quality

## Project layout

- `MainWindow.xaml` and `MainWindow.xaml.cs`: main editor UI and interaction flow
- `Models/`: track, keyframe, project, and overlay models
- `Services/`: media loading, bitmap conversion, project persistence, and processing
- `Scripts/lama_inpaint_runner.py`: manual-timeline LaMa backend used for final processing
- `Dialogs/`: tuning and helper dialogs
- `Data/lama-coverage-config.json`: user-local stable-mask and export preferences
- `*.trackbox.json`: reusable project files with media paths, tracks, keyframes, and future learning metadata

## How to run

Requirements:

- .NET 10 SDK
- A compatible Python environment for `iopaint` / LaMa processing

```powershell
dotnet build
dotnet run
```

## Workflow

1. Open a video or image.
2. Add one or more tracks.
3. Select a track.
4. Move along the timeline.
5. Draw a box on the frame.
6. Save a keyframe.
7. Add "Disable Here" keyframes where the watermark disappears.
8. Save the session as a `.trackbox.json` project whenever you want to come back later.
9. Optionally review the mask / coverage tuning.
10. Start processing.

## Python runtime lookup

TrackBoxStudio resolves the LaMa Python runtime in this order:

1. `TRACKBOX_PYTHON_EXE`
2. `LAMA_PYTHON_EXE`
3. `python\python.exe` next to the built app
4. `python\python.exe` in the repo root when running from source
5. `..\Lama\python\python.exe` as a sibling development checkout when running from source

The last entry is a development convenience fallback, not a required install location. It is resolved relative to the repo location at runtime, so logs or exceptions may show it as an absolute path on the current machine.

## Notes

- Inpaint tuning is user-local: `Data/lama-coverage-config.json` is auto-created on first launch and is not stored in git.
- Track timelines can be saved and reopened as standalone project files.
- Older project files with legacy watermark metadata still load; the editor now works directly with tracks only.
- The `Keep Audio` checkbox next to `Start Processing` controls whether TrackBoxStudio should try to preserve video audio on export, and the choice is remembered in the local tuning config.
- If `ffmpeg.exe` is available in `PATH` and `Keep Audio` is enabled, TrackBoxStudio will try to preserve audio when exporting video.
- If `ffmpeg.exe` is not available, the processed video is still exported, but audio may be dropped.
- Project files already contain a small `learning` block reserved for future ML-assisted workflows, but no training code is enabled in the current build.
- Final inpainting quality depends on a compatible LaMa Python environment. See the lookup order above for how the app resolves `python.exe`.
- Device selection is `CUDA preferred` by default: on systems with working CUDA it will load LaMa on GPU, and only fall back to CPU if CUDA is unavailable.
- The current default processing profile is `Max Quality`: 100 LaMa steps, larger crop margin, higher resize limit, and a small mask padding around the user box.

## Usage policy

This app is not intended for deepfake laundering, identity fraud, deception campaigns, or any attempt to hide synthetic or manipulated media in harmful contexts.

Allowed scope for this project:

- entertainment content
- parody
- memes
- stylized edits
- other lawful creative use

Read [LICENSE.md](LICENSE.md) for the full restriction notice.
