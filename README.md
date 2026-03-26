# TrackBoxStudio

TrackBoxStudio is a fresh C# / WPF desktop app for manual watermark cleanup.

The project is built around one idea: stop trusting weak auto-detection, and let the editor drive everything.

## What it does

- Opens a video or image file
- Stores named watermark definitions in a user-global registry under `%AppData%\TrackBoxStudio`
- Lets you create one or more timeline tracks
- Lets each track point to a named watermark from the registry
- Lets you draw a box on a frame and save it as a keyframe
- Lets you disable a watermark on any frame by writing an "off" keyframe
- Shows enabled and disabled ranges directly on each track
- Saves and reopens full timeline sessions as `.trackbox.json` project files
- Processes the file by running real LaMa inpainting only on the boxes that are enabled on the active frame

## Current stack

- .NET 10
- WPF
- OpenCvSharp for frame access and media IO
- Python sidecar runner with `iopaint` LaMa for final cleanup quality

## Project layout

- `MainWindow.xaml` and `MainWindow.xaml.cs`: main editor UI and interaction flow
- `Models/`: watermark, track, keyframe, and overlay models
- `Services/`: registry storage, media loading, bitmap conversion, and processing
- `Scripts/lama_inpaint_runner.py`: manual-timeline LaMa backend used for final processing
- `Dialogs/`: small prompt dialog for creating and renaming watermark definitions
- `%AppData%\TrackBoxStudio\watermark-registry.json`: named watermark registry shared across local projects for the current user
- `*.trackbox.json`: reusable project files with media paths, tracks, keyframes, and future learning metadata

## How to run

```powershell
dotnet build
dotnet run
```

## Workflow

1. Open a video.
2. Create or pick a watermark from the registry.
3. Add one or more tracks.
4. Select a track.
5. Move along the timeline.
6. Draw a box on the frame.
7. Save a keyframe.
8. Add "Disable Here" keyframes where the watermark disappears.
9. Save the session as a `.trackbox.json` project whenever you want to come back later.
10. Start processing.

## Notes

- Registry entries are reusable across videos.
- The watermark registry is user-global and auto-migrates from the older `Data/watermark-registry.json` location on first load.
- Inpaint tuning is user-local: `Data/lama-coverage-config.json` is auto-created on first launch and is not stored in git.
- Track timelines can be saved and reopened as standalone project files.
- The `Keep Audio` checkbox next to `Start Processing` controls whether TrackBoxStudio should try to preserve video audio on export, and the choice is remembered in the local tuning config.
- If `ffmpeg.exe` is available in `PATH` and `Keep Audio` is enabled, TrackBoxStudio will try to preserve audio when exporting video.
- If `ffmpeg.exe` is not available, the processed video is still exported, but audio may be dropped.
- Project files already contain a small `learning` block reserved for future ML-assisted workflows, but no training code is enabled in the current build.
- Final inpainting quality depends on a compatible LaMa Python environment. By default the app looks for `TRACKBOX_PYTHON_EXE`, then a bundled `python\python.exe`, then the sibling dev environment at `D:\git\Lama\python\python.exe`.
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
