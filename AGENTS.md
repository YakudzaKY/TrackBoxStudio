# TrackBoxStudio Agent Guide

## What this repo is

TrackBoxStudio is a standalone C# / WPF desktop app for manual watermark cleanup.

The core idea is:
- no mandatory auto-detection
- the user draws boxes manually on video frames
- boxes are stored as keyframes on one or more timeline tracks
- processing inpaints only the boxes that are enabled on the active frame

This repo is meant to stay simple, local-first, and editor-driven.

## Main stack

- .NET 10
- WPF
- OpenCvSharp

## Primary user workflow

1. Open a video or image.
2. Create or choose a named watermark from the registry.
3. Add one or more tracks.
4. Draw a box on a frame.
5. Save an enabled keyframe or write a disabled keyframe.
6. Save the session as a `.trackbox.json` project.
7. Run processing to inpaint enabled boxes.

## Repo map

- `MainWindow.xaml`
  Main editor layout, toolbar, timeline slider, overlay area, track list, keyframe list.

- `MainWindow.xaml.cs`
  Main application state and editor behavior.
  Change this when you need to adjust selection flow, drawing behavior, save/load actions, track editing, or processing triggers.

- `Models/`
  Runtime data models used by the editor.
  Important files:
  - `TimelineTrack.cs`: track state, ordered keyframes, segment preview rebuild
  - `BoxKeyframe.cs`: one keyframe, can be enabled or disabled
  - `BoxRect.cs`: plain rectangle used for overlays and processing
  - `TrackBoxProjectDocument.cs`: persisted `.trackbox.json` schema, including future `learning` metadata

- `Services/`
  Backend logic that should stay UI-independent where possible.
  Important files:
  - `MediaDocumentService.cs`: media open/reset/frame extraction
  - `InpaintProcessingService.cs`: launches the Python LaMa backend and translates progress/status back into the app
  - `ProjectPersistenceService.cs`: save/load `.trackbox.json`
  - `WatermarkRegistryService.cs`: save/load named watermark registry in `Data/`
  - `BitmapSourceFactory.cs`: OpenCV to WPF bitmap conversion

- `Scripts/`
  Python sidecar utilities shipped with the app output.
  Important files:
  - `lama_inpaint_runner.py`: real LaMa processing backend for manual timeline jobs

- `Dialogs/`
  Small reusable modal dialogs like text prompts for naming watermarks.

- `Infrastructure/`
  Generic support code such as `BindableBase`.

- `Data/`
  App-local data stored next to the executable and repo.
  Right now this mainly contains `watermark-registry.json`.

- `README.md`
  User-facing overview and usage notes.

- `LICENSE.md`
  Repo-specific usage restriction notice.

## Where to change what

### If you want to change the editor UI

Start with:
- `MainWindow.xaml`
- `MainWindow.xaml.cs`

Examples:
- toolbar buttons
- track list presentation
- timeline slider behavior
- overlay drawing interactions
- selected track / selected keyframe UX

### If you want to change how projects are saved

Start with:
- `Models/TrackBoxProjectDocument.cs`
- `Services/ProjectPersistenceService.cs`
- `MainWindow.xaml.cs`

Rules:
- keep the JSON schema backward-friendly when possible
- do not remove existing fields casually
- prefer adding optional fields for future compatibility

### If you want to change video or image loading

Start with:
- `Services/MediaDocumentService.cs`

This is the place for:
- open/reset behavior
- frame count / fps handling
- per-frame extraction

### If you want to change inpaint processing

Start with:
- `Services/InpaintProcessingService.cs`
- `Scripts/lama_inpaint_runner.py`

This is the place for:
- mask generation
- active box resolution per frame
- frame loop behavior
- export behavior
- ffmpeg audio preservation flow
- LaMa model loading and device choice

### If you want to change timeline semantics

Start with:
- `Models/TimelineTrack.cs`
- `Models/BoxKeyframe.cs`
- `MainWindow.xaml.cs`

Important:
- `TimelineTrack.RebuildSegments(...)` drives the on/off preview shown in the UI
- if keyframe logic changes, refresh segment rebuilding and overlay refresh paths together

### If you want to change the watermark registry

Start with:
- `Models/WatermarkDefinition.cs`
- `Services/WatermarkRegistryService.cs`
- `MainWindow.xaml.cs`

## Editing notes

- The app is intentionally manual-first. Do not make auto-detection a hard dependency.
- Keep project save/load stable. Future ML work is expected, so prefer additive schema changes.
- The `learning` block in project JSON is only a placeholder right now. Do not pretend training exists unless it is actually implemented.
- Final cleanup quality now depends on the Python LaMa sidecar. If processing quality changes, inspect both the C# launcher and `Scripts/lama_inpaint_runner.py`.
- Default device policy is `cuda-preferred`: prefer GPU when CUDA is available, otherwise fall back to CPU.
- Current default processing preset is `max`: 100 LaMa steps, 128px crop margin, 2048 resize limit, and 16px mask padding.
- When changing track or keyframe state in `MainWindow.xaml.cs`, also check:
  - `SyncDraftBoxFromSelectedTrack()`
  - `RebuildOverlayBoxes()`
  - `RefreshTrackAfterEdit(...)`
- When changing persisted paths, keep both absolute and relative path handling in mind.

## Build and run

```powershell
dotnet build
dotnet run
```

## Validation

For code changes, prefer at least:
- `dotnet build`

If you change processing or persistence logic, also verify the relevant workflow manually:
- open file
- create track and keyframes
- save project
- reopen project
- run processing

## Near-term roadmap context

Planned future direction includes ML-assisted workflows based on user-authored boxes.

That means:
- manual annotations are the source of truth
- project files should remain extensible
- any future model-training feature should be optional and layered on top of the current manual flow
