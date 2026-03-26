using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using TrackBoxStudio.Dialogs;
using TrackBoxStudio.Models;
using TrackBoxStudio.Services;

namespace TrackBoxStudio;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int MinimumBoxSize = 2;
    private const double ResizeGripSize = 10;

    private readonly WatermarkRegistryService _registryService = new();
    private readonly MediaDocumentService _mediaService = new();
    private readonly InpaintProcessingService _processingService = new();
    private readonly ProjectPersistenceService _projectService = new();
    private readonly DispatcherTimer _frameLoadTimer;
    private readonly string[] _trackColors =
    [
        "#4ADE80",
        "#38BDF8",
        "#F59E0B",
        "#F472B6",
        "#A78BFA",
        "#FB7185",
    ];

    private WatermarkDefinition? _selectedWatermarkDefinition;
    private TimelineTrack? _selectedTrack;
    private BitmapSource? _currentFrameImage;
    private string _inputPath = string.Empty;
    private string _outputPath = string.Empty;
    private string _currentProjectPath = string.Empty;
    private int _currentFrameIndex;
    private int _totalFrames;
    private double _framesPerSecond;
    private int _frameWidth;
    private int _frameHeight;
    private bool _isMediaLoaded;
    private bool _isBusy;
    private double _progressValue;
    private string _statusText = "Open a file to start a new session.";
    private BoxRect? _draftBox;
    private OverlayInteractionMode _overlayInteractionMode;
    private ResizeEdges _activeResizeEdges;
    private BoxRect? _interactionStartBox;
    private bool _overlayInteractionChanged;
    private Point _dragStart;
    private int _pendingFrameIndex;
    private int _frameRequestId;

    private enum OverlayInteractionMode
    {
        None,
        Draw,
        Move,
        Resize,
    }

    private sealed record SegmentMenuContext(TimelineTrack Track, TrackSegmentPreview Segment);

    [Flags]
    private enum ResizeEdges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        WatermarkRegistry = [];
        Tracks = [];
        OverlayBoxes = [];

        _frameLoadTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(70),
        };
        _frameLoadTimer.Tick += FrameLoadTimer_Tick;

        Loaded += MainWindow_Loaded;
        Closed += (_, _) => _mediaService.Dispose();
        Tracks.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanProcess));
            OnPropertyChanged(nameof(CanSaveProject));
            OnPropertyChanged(nameof(CanRemoveKeyframeHere));
            OnPropertyChanged(nameof(SelectedTrackStatus));
            OnPropertyChanged(nameof(SelectedTrackKeyframeStatus));
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<WatermarkDefinition> WatermarkRegistry { get; }

    public ObservableCollection<TimelineTrack> Tracks { get; }

    public ObservableCollection<OverlayBox> OverlayBoxes { get; }

    public WatermarkDefinition? SelectedWatermarkDefinition
    {
        get => _selectedWatermarkDefinition;
        set => SetProperty(ref _selectedWatermarkDefinition, value);
    }

    public TimelineTrack? SelectedTrack
    {
        get => _selectedTrack;
        set
        {
            if (SetProperty(ref _selectedTrack, value))
            {
                SyncDraftBoxFromSelectedTrack();
                RebuildOverlayBoxes();
                OnPropertyChanged(nameof(CanEditTrack));
                OnPropertyChanged(nameof(CanSaveCurrentBox));
                OnPropertyChanged(nameof(CanRemoveKeyframeHere));
                OnPropertyChanged(nameof(CanToggleTrackStateHere));
                OnPropertyChanged(nameof(ToggleTrackHereLabel));
                OnPropertyChanged(nameof(SelectedTrackStatus));
                OnPropertyChanged(nameof(SelectedTrackKeyframeStatus));
            }
        }
    }

    public BitmapSource? CurrentFrameImage
    {
        get => _currentFrameImage;
        private set => SetProperty(ref _currentFrameImage, value);
    }

    public string InputPath
    {
        get => _inputPath;
        private set
        {
            if (SetProperty(ref _inputPath, value))
            {
                OnPropertyChanged(nameof(CanSaveProject));
            }
        }
    }

    public string OutputPath
    {
        get => _outputPath;
        private set
        {
            if (SetProperty(ref _outputPath, value))
            {
                OnPropertyChanged(nameof(CanSaveProject));
            }
        }
    }

    public string CurrentProjectPath
    {
        get => _currentProjectPath;
        private set
        {
            if (SetProperty(ref _currentProjectPath, value))
            {
                OnPropertyChanged(nameof(CurrentProjectDisplay));
                OnPropertyChanged(nameof(CanSaveProject));
            }
        }
    }

    public int CurrentFrameIndex
    {
        get => _currentFrameIndex;
        set
        {
            if (SetProperty(ref _currentFrameIndex, value))
            {
                OnPropertyChanged(nameof(CurrentFrameDisplay));
                OnPropertyChanged(nameof(CurrentTimeDisplay));
                OnPropertyChanged(nameof(CanRemoveKeyframeHere));
                OnPropertyChanged(nameof(CanToggleTrackStateHere));
                OnPropertyChanged(nameof(ToggleTrackHereLabel));
                OnPropertyChanged(nameof(SelectedTrackStatus));
            }
        }
    }

    public int TotalFrames
    {
        get => _totalFrames;
        private set
        {
            if (SetProperty(ref _totalFrames, value))
            {
                OnPropertyChanged(nameof(SliderMaximum));
                OnPropertyChanged(nameof(CurrentFrameDisplay));
            }
        }
    }

    public double FramesPerSecond
    {
        get => _framesPerSecond;
        private set
        {
            if (SetProperty(ref _framesPerSecond, value))
            {
                OnPropertyChanged(nameof(CurrentTimeDisplay));
            }
        }
    }

    public int FrameWidth
    {
        get => _frameWidth;
        private set
        {
            if (SetProperty(ref _frameWidth, value))
            {
                OnPropertyChanged(nameof(FrameCanvasWidth));
            }
        }
    }

    public int FrameHeight
    {
        get => _frameHeight;
        private set
        {
            if (SetProperty(ref _frameHeight, value))
            {
                OnPropertyChanged(nameof(FrameCanvasHeight));
            }
        }
    }

    public bool IsMediaLoaded
    {
        get => _isMediaLoaded;
        private set
        {
            if (SetProperty(ref _isMediaLoaded, value))
            {
                OnPropertyChanged(nameof(CanProcess));
                OnPropertyChanged(nameof(CanEditTrack));
                OnPropertyChanged(nameof(CanSaveCurrentBox));
                OnPropertyChanged(nameof(CanRemoveKeyframeHere));
                OnPropertyChanged(nameof(CanToggleTrackStateHere));
                OnPropertyChanged(nameof(CanSaveProject));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanProcess));
                OnPropertyChanged(nameof(CanEditTrack));
                OnPropertyChanged(nameof(CanSaveCurrentBox));
                OnPropertyChanged(nameof(CanRemoveKeyframeHere));
                OnPropertyChanged(nameof(CanToggleTrackStateHere));
                OnPropertyChanged(nameof(CanSaveProject));
            }
        }
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set
        {
            if (SetProperty(ref _progressValue, value))
            {
                OnPropertyChanged(nameof(ProgressDisplay));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public BoxRect? DraftBox
    {
        get => _draftBox;
        private set
        {
            if (SetProperty(ref _draftBox, value))
            {
                OnPropertyChanged(nameof(DraftBoxSummary));
                OnPropertyChanged(nameof(CanSaveCurrentBox));
                OnPropertyChanged(nameof(CanToggleTrackStateHere));
                OnPropertyChanged(nameof(SelectedTrackStatus));
            }
        }
    }

    public double FrameCanvasWidth => Math.Max(1, FrameWidth);

    public double FrameCanvasHeight => Math.Max(1, FrameHeight);

    public double SliderMaximum => Math.Max(0, TotalFrames - 1);

    public bool CanEditTrack => IsMediaLoaded && !IsBusy && SelectedTrack is not null;

    public bool CanSaveCurrentBox => CanEditTrack && DraftBox is not null;

    public bool CanRemoveKeyframeHere => CanEditTrack && GetRemovableKeyframeAtCurrentFrame(SelectedTrack, CurrentFrameIndex) is not null;

    public bool CanToggleTrackStateHere => CanEditTrack && (IsSelectedTrackEnabledAtCurrentFrame() || HasBoxToEnableHere());

    public bool CanProcess => IsMediaLoaded && !IsBusy && Tracks.Any(track => track.Keyframes.Count > 0);

    public bool CanSaveProject => !IsBusy && (IsMediaLoaded || Tracks.Count > 0 || !string.IsNullOrWhiteSpace(CurrentProjectPath));

    public string CurrentFrameDisplay => !IsMediaLoaded
        ? "Frame -"
        : $"Frame {CurrentFrameIndex} / {Math.Max(0, TotalFrames - 1)}";

    public string CurrentTimeDisplay => !IsMediaLoaded
        ? "-"
        : FramesPerSecond > 0
            ? $"{CurrentFrameIndex / FramesPerSecond:0.00}s"
            : "Single image";

    public string DraftBoxSummary => DraftBox is null
        ? "No draft box. Draw on the frame to create one."
        : DraftBox.ToString();

    public string CurrentProjectDisplay => string.IsNullOrWhiteSpace(CurrentProjectPath)
        ? "Unsaved session"
        : CurrentProjectPath;

    public string SelectedTrackStatus => SelectedTrack is null
        ? "Add or select a track, then draw a box directly on the frame."
        : BuildSelectedTrackStatus();

    public string ToggleTrackHereLabel => IsSelectedTrackEnabledAtCurrentFrame()
        ? "Disable Here"
        : "Enable Here";

    public string SelectedTrackKeyframeStatus => SelectedTrack is null
        ? "No track selected."
        : $"{SelectedTrack.Keyframes.Count} keyframes. Red segments on the track mean the watermark is disabled there.";

    public string ProgressDisplay => $"{ProgressValue:0}%";

    public string RegistryLocation => $"Registry file: {_registryService.RegistryPath}";

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadWatermarkRegistryAsync();
    }

    private async Task LoadWatermarkRegistryAsync()
    {
        WatermarkRegistry.Clear();
        foreach (var definition in await _registryService.LoadAsync())
        {
            WatermarkRegistry.Add(definition);
        }

        StatusText = WatermarkRegistry.Count == 0
            ? "Registry is empty. Create your first named watermark."
            : $"Loaded {WatermarkRegistry.Count} named watermark definitions.";
    }

    private async void OpenInput_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open video or image",
            Filter = "Media files|*.mp4;*.avi;*.mov;*.mkv;*.flv;*.wmv;*.webm;*.png;*.jpg;*.jpeg;*.bmp;*.webp|All files|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _mediaService.Open(dialog.FileName);
            InputPath = dialog.FileName;
            OutputPath = BuildDefaultOutputPath(dialog.FileName);
            IsMediaLoaded = true;
            TotalFrames = _mediaService.TotalFrames;
            FramesPerSecond = _mediaService.FramesPerSecond;
            FrameWidth = _mediaService.FrameWidth;
            FrameHeight = _mediaService.FrameHeight;
            CurrentFrameIndex = 0;
            ProgressValue = 0;

            Tracks.Clear();
            SelectedTrack = null;
            OverlayBoxes.Clear();
            DraftBox = null;

            StatusText = $"Loaded {Path.GetFileName(dialog.FileName)}. Build tracks and keyframes next.";
            await LoadFrameAsync(0);
            CurrentProjectPath = string.Empty;
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Media Open Error");
            MessageBox.Show(this, ex.Message, "Open failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Open failed: {ex.Message}";
        }
    }

    private async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open TrackBoxStudio project",
            Filter = "TrackBoxStudio project|*.trackbox.json|JSON files|*.json|All files|*.*",
            FileName = Path.GetFileName(CurrentProjectPath),
            InitialDirectory = GetProjectDialogDirectory(),
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            IsBusy = true;
            StatusText = $"Loading project {Path.GetFileName(dialog.FileName)}...";

            var document = await _projectService.LoadAsync(dialog.FileName);
            await ApplyProjectAsync(dialog.FileName, document);
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Project Open Error");
            MessageBox.Show(this, ex.Message, "Open project failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Open project failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async void SaveProject_Click(object sender, RoutedEventArgs e)
    {
        if (!CanSaveProject)
        {
            return;
        }

        var projectPath = CurrentProjectPath;
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save TrackBoxStudio project",
                Filter = "TrackBoxStudio project|*.trackbox.json|JSON files|*.json",
                FileName = Path.GetFileName(BuildDefaultProjectPath()),
                InitialDirectory = GetProjectDialogDirectory(),
                AddExtension = true,
                DefaultExt = ".trackbox.json",
            };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            projectPath = dialog.FileName;
        }

        try
        {
            IsBusy = true;
            var document = BuildProjectDocument();
            await _projectService.SaveAsync(projectPath, document);
            CurrentProjectPath = projectPath;
            StatusText = $"Project saved to {projectPath}.";
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Project Save Error");
            MessageBox.Show(this, ex.Message, "Save project failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Save project failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ChooseOutput_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        var saveDialog = new SaveFileDialog
        {
            Title = "Choose output file",
            Filter = MediaDocumentService.IsVideoFile(InputPath)
                ? "MP4 video|*.mp4"
                : "PNG image|*.png|JPG image|*.jpg",
            FileName = Path.GetFileName(OutputPath),
            InitialDirectory = Path.GetDirectoryName(OutputPath),
        };

        if (saveDialog.ShowDialog(this) == true)
        {
            OutputPath = saveDialog.FileName;
        }
    }

    private void OpenInpaintTuning_Click(object sender, RoutedEventArgs e)
    {
        var window = new InpaintCoverageSettingsWindow
        {
            Owner = this,
        };
        window.ShowDialog();
    }

    private async void AddWatermark_Click(object sender, RoutedEventArgs e)
    {
        await CreateWatermarkAsync();
    }

    private async void RenameWatermark_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWatermarkDefinition is null)
        {
            return;
        }

        var prompt = new TextPromptWindow("Rename Watermark", "Enter a new name for the watermark.", SelectedWatermarkDefinition.Name)
        {
            Owner = this,
        };
        if (prompt.ShowDialog() != true)
        {
            return;
        }

        var name = prompt.ResponseText;
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        SelectedWatermarkDefinition.Name = name;
        RefreshTrackWatermarkNames();
        await _registryService.SaveAsync(WatermarkRegistry);
        StatusText = $"Renamed watermark to '{name}'.";
    }

    private async void DeleteWatermark_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedWatermarkDefinition is null)
        {
            return;
        }

        if (Tracks.Any(track => track.WatermarkId == SelectedWatermarkDefinition.Id))
        {
            MessageBox.Show(this, "This watermark is currently used by one or more tracks. Reassign or remove those tracks first.", "Watermark in use", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var removedName = SelectedWatermarkDefinition.Name;
        WatermarkRegistry.Remove(SelectedWatermarkDefinition);
        SelectedWatermarkDefinition = WatermarkRegistry.FirstOrDefault();
        await _registryService.SaveAsync(WatermarkRegistry);
        StatusText = $"Deleted watermark '{removedName}'.";
    }

    private async void AddTrack_Click(object sender, RoutedEventArgs e)
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        var watermark = SelectedWatermarkDefinition ?? WatermarkRegistry.FirstOrDefault();
        if (watermark is null)
        {
            watermark = await CreateWatermarkAsync();
            if (watermark is null)
            {
                return;
            }
        }

        var track = new TimelineTrack
        {
            Name = $"Track {Tracks.Count + 1}",
            WatermarkId = watermark.Id,
            WatermarkName = watermark.Name,
            ColorHex = _trackColors[Tracks.Count % _trackColors.Length],
        };

        track.RebuildSegments(TotalFrames);
        Tracks.Add(track);
        SelectedTrack = track;
        StatusText = $"Added {track.DisplayName}.";
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimelineTrack track })
        {
            return;
        }

        Tracks.Remove(track);
        if (ReferenceEquals(SelectedTrack, track))
        {
            SelectedTrack = Tracks.FirstOrDefault();
        }

        RebuildOverlayBoxes();
        OnPropertyChanged(nameof(CanProcess));
        StatusText = $"Removed {track.Name}.";
    }

    private async void CreateWatermarkForTrack_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: TimelineTrack track })
        {
            return;
        }

        var watermark = await CreateWatermarkAsync();
        if (watermark is null)
        {
            return;
        }

        track.WatermarkId = watermark.Id;
        track.WatermarkName = watermark.Name;
        OnPropertyChanged(nameof(SelectedTrackStatus));
        StatusText = $"Assigned '{watermark.Name}' to {track.Name}.";
    }

    private void TrackWatermarkSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender is not System.Windows.Controls.ComboBox { DataContext: TimelineTrack track })
        {
            return;
        }

        var definition = WatermarkRegistry.FirstOrDefault(item => item.Id == track.WatermarkId);
        track.WatermarkName = definition?.Name ?? "Unassigned";
        if (ReferenceEquals(track, SelectedTrack))
        {
            OnPropertyChanged(nameof(SelectedTrackStatus));
        }
        StatusText = $"{track.Name} now points to {track.WatermarkName}.";
    }

    private void FrameSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsMediaLoaded || IsBusy)
        {
            return;
        }

        _pendingFrameIndex = (int)Math.Round(e.NewValue);
        CurrentFrameIndex = _pendingFrameIndex;
        _frameLoadTimer.Stop();
        _frameLoadTimer.Start();
    }

    private async void FrameLoadTimer_Tick(object? sender, EventArgs e)
    {
        _frameLoadTimer.Stop();
        try
        {
            await LoadFrameAsync(_pendingFrameIndex);
        }
        catch (Exception ex)
        {
            StatusText = $"Frame load failed: {ex.Message}";
        }
    }

    private async Task LoadFrameAsync(int frameIndex)
    {
        if (!IsMediaLoaded)
        {
            return;
        }

        var requestId = ++_frameRequestId;
        try
        {
            var bitmap = await Task.Run(() => _mediaService.LoadBitmapSource(frameIndex));
            if (requestId != _frameRequestId)
            {
                return;
            }

            CurrentFrameImage = bitmap;
            CurrentFrameIndex = frameIndex;
            SyncDraftBoxFromSelectedTrack();
            RebuildOverlayBoxes();
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Frame Load Error");
            throw;
        }
    }

    private void OverlayInputCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!CanEditTrack)
        {
            return;
        }

        _dragStart = e.GetPosition(OverlayInputCanvas);
        _interactionStartBox = DraftBox?.Clone();
        _overlayInteractionChanged = false;
        _activeResizeEdges = ResizeEdges.None;

        if (TryGetOverlayInteraction(_dragStart, _interactionStartBox, out var interactionMode, out var resizeEdges))
        {
            _overlayInteractionMode = interactionMode;
            _activeResizeEdges = resizeEdges;
            OverlayInputCanvas.Cursor = GetCursorForInteraction(interactionMode, resizeEdges);
        }
        else
        {
            _overlayInteractionMode = OverlayInteractionMode.Draw;
            DraftBox = BuildBoxFromPoints(_dragStart, _dragStart);
            OverlayInputCanvas.Cursor = Cursors.Cross;
            RebuildOverlayBoxes();
        }

        OverlayInputCanvas.CaptureMouse();
        e.Handled = true;
    }

    private void OverlayInputCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        var position = e.GetPosition(OverlayInputCanvas);
        if (_overlayInteractionMode == OverlayInteractionMode.None)
        {
            UpdateOverlayCursor(position);
            return;
        }

        var updatedDraft = _overlayInteractionMode switch
        {
            OverlayInteractionMode.Draw => BuildBoxFromPoints(_dragStart, position),
            OverlayInteractionMode.Move or OverlayInteractionMode.Resize when _interactionStartBox is not null
                => TransformDraftBox(_interactionStartBox, position),
            _ => DraftBox,
        };

        if (updatedDraft is null)
        {
            return;
        }

        _overlayInteractionChanged |= _interactionStartBox is not null
            ? !BoxEquals(updatedDraft, _interactionStartBox)
            : updatedDraft.Width > 0 || updatedDraft.Height > 0;

        DraftBox = updatedDraft;
        RebuildOverlayBoxes();
        e.Handled = true;
    }

    private void OverlayInputCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_overlayInteractionMode == OverlayInteractionMode.None)
        {
            return;
        }

        OverlayInputCanvas.ReleaseMouseCapture();
        var completedMode = _overlayInteractionMode;
        DraftBox = NormalizeDraft(DraftBox);

        if ((completedMode == OverlayInteractionMode.Move || completedMode == OverlayInteractionMode.Resize)
            && _overlayInteractionChanged
            && SelectedTrack is not null
            && DraftBox is not null)
        {
            SaveDraftBoxToCurrentFrame(SelectedTrack, $"Updated {SelectedTrack.Name} box on frame {CurrentFrameIndex}.");
        }
        else
        {
            RebuildOverlayBoxes();
        }

        ResetOverlayInteraction();
        UpdateOverlayCursor(e.GetPosition(OverlayInputCanvas));
        e.Handled = true;
    }

    private void OverlayInputCanvas_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_overlayInteractionMode == OverlayInteractionMode.None)
        {
            OverlayInputCanvas.Cursor = Cursors.Arrow;
        }
    }

    private void SaveBoxHere_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null || DraftBox is null)
        {
            return;
        }

        SaveDraftBoxToCurrentFrame(SelectedTrack, $"Saved a box on frame {CurrentFrameIndex}.");
    }

    private void DisableHere_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        if (IsSelectedTrackEnabledAtCurrentFrame())
        {
            SelectedTrack.UpsertKeyframe(new BoxKeyframe
            {
                Frame = CurrentFrameIndex,
                Enabled = false,
                Box = null,
            });
            RefreshTrackAfterEdit(SelectedTrack, $"Disabled {SelectedTrack.Name} on frame {CurrentFrameIndex}.");
            return;
        }

        var boxToEnable = ResolveBoxToEnableHere();
        if (boxToEnable is null)
        {
            MessageBox.Show(
                this,
                "There is no box to restore on this frame yet. Draw a box here first, then save or enable it.",
                "Nothing to enable",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            StatusText = $"Draw a box on frame {CurrentFrameIndex} before enabling {SelectedTrack.Name} here.";
            return;
        }

        SelectedTrack.UpsertKeyframe(new BoxKeyframe
        {
            Frame = CurrentFrameIndex,
            Enabled = true,
            Box = boxToEnable,
        });
        RefreshTrackAfterEdit(SelectedTrack, $"Enabled {SelectedTrack.Name} on frame {CurrentFrameIndex}.");
    }

    private void RemoveKeyframeHere_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null)
        {
            return;
        }

        var keyframe = GetRemovableKeyframeAtCurrentFrame(SelectedTrack, CurrentFrameIndex);
        if (keyframe is null)
        {
            StatusText = $"There is no keyframe controlling frame {CurrentFrameIndex} on {SelectedTrack.Name}.";
            return;
        }

        RemoveKeyframeFromTrack(
            SelectedTrack,
            keyframe.Frame,
            keyframe.Frame == CurrentFrameIndex
                ? $"Removed the keyframe at frame {keyframe.Frame}."
                : $"Removed the active keyframe at frame {keyframe.Frame} while viewing frame {CurrentFrameIndex}.");
    }

    private async void JumpToKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: BoxKeyframe keyframe })
        {
            return;
        }

        try
        {
            CurrentFrameIndex = keyframe.Frame;
            FrameSlider.Value = keyframe.Frame;
            await LoadFrameAsync(keyframe.Frame);
        }
        catch (Exception ex)
        {
            StatusText = $"Frame load failed: {ex.Message}";
        }
    }

    private void DeleteKeyframe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null || sender is not FrameworkElement { Tag: BoxKeyframe keyframe })
        {
            return;
        }

        RemoveKeyframeFromTrack(SelectedTrack, keyframe.Frame, $"Deleted keyframe at frame {keyframe.Frame}.");
    }

    private void TrackSegmentBorder_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (sender is not Border { DataContext: TrackSegmentPreview segment, Tag: TimelineTrack track } border)
        {
            return;
        }

        SelectedTrack = track;

        var keyframe = track.Keyframes.FirstOrDefault(item => item.Frame == segment.StartFrame);
        var removeItem = new MenuItem
        {
            Header = "Remove Keyframe",
            IsEnabled = keyframe is not null,
            Tag = keyframe is null ? null : new SegmentMenuContext(track, segment),
        };
        removeItem.Click += RemoveSegmentKeyframeMenuItem_Click;

        border.ContextMenu = new ContextMenu();
        border.ContextMenu.Items.Add(removeItem);
    }

    private void RemoveSegmentKeyframeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: SegmentMenuContext context })
        {
            return;
        }

        SelectedTrack = context.Track;
        RemoveKeyframeFromTrack(
            context.Track,
            context.Segment.StartFrame,
            $"Removed the keyframe at frame {context.Segment.StartFrame} from {context.Track.Name}.");
    }

    private async void Process_Click(object sender, RoutedEventArgs e)
    {
        if (!CanProcess)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(OutputPath))
        {
            OutputPath = BuildDefaultOutputPath(InputPath);
        }

        try
        {
            IsBusy = true;
            ProgressValue = 0;
            StatusText = $"Processing {Path.GetFileName(InputPath)}...";

            var trackSnapshot = BuildProcessingSnapshot();
            var progress = new Progress<double>(value => ProgressValue = value * 100);
            var status = new Progress<string>(value =>
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    StatusText = value;
                }
            });

            await _processingService.ProcessAsync(InputPath, OutputPath, trackSnapshot, renderMaskOnly: false, progress, status, CancellationToken.None);

            ProgressValue = 100;
            StatusText = $"Done. Output written to {OutputPath}.";
        }
        catch (Exception ex)
        {
            App.LogError(ex, "Processing Error");
            MessageBox.Show(this, ex.Message, "Processing failed", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"Processing failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task<WatermarkDefinition?> CreateWatermarkAsync()
    {
        var prompt = new TextPromptWindow("New Watermark", "Enter a name for the reusable watermark definition.")
        {
            Owner = this,
        };
        if (prompt.ShowDialog() != true)
        {
            return null;
        }

        var name = prompt.ResponseText;
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var existing = WatermarkRegistry.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectedWatermarkDefinition = existing;
            StatusText = $"Watermark '{name}' already exists.";
            return existing;
        }

        var definition = new WatermarkDefinition
        {
            Name = name,
        };
        WatermarkRegistry.Add(definition);
        SelectedWatermarkDefinition = definition;
        await _registryService.SaveAsync(WatermarkRegistry);
        StatusText = $"Created watermark '{name}'.";
        return definition;
    }

    private void RefreshTrackAfterEdit(TimelineTrack track, string statusMessage)
    {
        track.RebuildSegments(TotalFrames);
        SyncDraftBoxFromSelectedTrack();
        RebuildOverlayBoxes();
        OnPropertyChanged(nameof(CanProcess));
        OnPropertyChanged(nameof(CanRemoveKeyframeHere));
        OnPropertyChanged(nameof(CanToggleTrackStateHere));
        OnPropertyChanged(nameof(ToggleTrackHereLabel));
        OnPropertyChanged(nameof(SelectedTrackStatus));
        OnPropertyChanged(nameof(SelectedTrackKeyframeStatus));
        StatusText = statusMessage;
    }

    private void RefreshTrackWatermarkNames()
    {
        foreach (var track in Tracks)
        {
            var definition = WatermarkRegistry.FirstOrDefault(item => item.Id == track.WatermarkId);
            track.WatermarkName = definition?.Name ?? "Unassigned";
        }
        OnPropertyChanged(nameof(SelectedTrackStatus));
    }

    private void SyncDraftBoxFromSelectedTrack()
    {
        if (SelectedTrack is null)
        {
            DraftBox = null;
            return;
        }

        var active = SelectedTrack.GetActiveKeyframe(CurrentFrameIndex);
        DraftBox = active?.Enabled == true && active.Box is not null
            ? active.Box.Clone()
            : null;
    }

    private string BuildSelectedTrackStatus()
    {
        if (SelectedTrack is null)
        {
            return "Add or select a track, then draw a box directly on the frame.";
        }

        if (IsSelectedTrackEnabledAtCurrentFrame())
        {
            return $"Selected: {SelectedTrack.DisplayName}. This watermark is enabled on frame {CurrentFrameIndex}. Drag inside the box to move it, drag an edge to resize it, or disable it from this frame.";
        }

        return HasBoxToEnableHere()
            ? $"Selected: {SelectedTrack.DisplayName}. This watermark is disabled on frame {CurrentFrameIndex}. Draw a new box or click Enable Here to restore the last enabled box."
            : $"Selected: {SelectedTrack.DisplayName}. This watermark is disabled on frame {CurrentFrameIndex}. Draw a box on this frame to enable it here.";
    }

    private bool IsSelectedTrackEnabledAtCurrentFrame()
    {
        var active = SelectedTrack?.GetActiveKeyframe(CurrentFrameIndex);
        return active?.Enabled == true && active.Box is not null;
    }

    private bool HasBoxToEnableHere()
    {
        return DraftBox is not null || FindLastEnabledBox(SelectedTrack, CurrentFrameIndex) is not null;
    }

    private BoxRect? ResolveBoxToEnableHere()
    {
        if (DraftBox is not null)
        {
            return DraftBox.Clone();
        }

        return FindLastEnabledBox(SelectedTrack, CurrentFrameIndex)?.Clone();
    }

    private static BoxRect? FindLastEnabledBox(TimelineTrack? track, int frameIndex)
    {
        if (track is null)
        {
            return null;
        }

        BoxRect? lastEnabledBox = null;
        foreach (var keyframe in track.OrderedKeyframes())
        {
            if (keyframe.Frame > frameIndex)
            {
                break;
            }

            if (keyframe.Enabled && keyframe.Box is not null)
            {
                lastEnabledBox = keyframe.Box;
            }
        }

        return lastEnabledBox;
    }

    private void SaveDraftBoxToCurrentFrame(TimelineTrack track, string statusMessage)
    {
        if (DraftBox is null)
        {
            return;
        }

        track.UpsertKeyframe(new BoxKeyframe
        {
            Frame = CurrentFrameIndex,
            Enabled = true,
            Box = DraftBox.Clone(),
        });
        RefreshTrackAfterEdit(track, statusMessage);
    }

    private static BoxKeyframe? GetRemovableKeyframeAtCurrentFrame(TimelineTrack? track, int frameIndex)
    {
        return track?.GetActiveKeyframe(frameIndex);
    }

    private void RemoveKeyframeFromTrack(TimelineTrack track, int keyframeFrame, string statusMessage)
    {
        track.RemoveKeyframe(keyframeFrame);
        RefreshTrackAfterEdit(track, statusMessage);
    }

    private void RebuildOverlayBoxes()
    {
        OverlayBoxes.Clear();
        foreach (var track in Tracks)
        {
            var active = track.GetActiveKeyframe(CurrentFrameIndex);
            var activeBox = active?.Enabled == true ? active.Box : null;
            if (ReferenceEquals(track, SelectedTrack) && DraftBox is not null)
            {
                activeBox = DraftBox;
            }

            if (activeBox is null)
            {
                continue;
            }

            var stroke = CreateSolidBrush(track.ColorHex, 255);
            var fill = CreateSolidBrush(track.ColorHex, ReferenceEquals(track, SelectedTrack) ? (byte)72 : (byte)44);
            var labelBackground = CreateSolidBrush(track.ColorHex, 180);

            OverlayBoxes.Add(new OverlayBox
            {
                X = activeBox.X,
                Y = activeBox.Y,
                Width = activeBox.Width,
                Height = activeBox.Height,
                Stroke = stroke,
                Fill = fill,
                StrokeThickness = ReferenceEquals(track, SelectedTrack) ? 3 : 2,
                Label = ReferenceEquals(track, SelectedTrack) ? $"{track.Name} (selected)" : track.Name,
                LabelBackground = labelBackground,
            });
        }
    }

    private List<TimelineTrack> BuildProcessingSnapshot()
    {
        var snapshot = new List<TimelineTrack>();
        foreach (var track in Tracks.Where(track => track.Keyframes.Count > 0))
        {
            var clone = new TimelineTrack
            {
                Id = track.Id,
                Name = track.Name,
                WatermarkId = track.WatermarkId,
                WatermarkName = track.WatermarkName,
                ColorHex = track.ColorHex,
            };

            foreach (var keyframe in track.OrderedKeyframes())
            {
                clone.Keyframes.Add(keyframe.Clone());
            }

            snapshot.Add(clone);
        }

        return snapshot;
    }

    private TrackBoxProjectDocument BuildProjectDocument()
    {
        var document = new TrackBoxProjectDocument
        {
            Media = new ProjectMediaState
            {
                InputPath = InputPath,
                OutputPath = OutputPath,
                IsVideo = !string.IsNullOrWhiteSpace(InputPath) && MediaDocumentService.IsVideoFile(InputPath),
                TotalFrames = TotalFrames,
                FramesPerSecond = FramesPerSecond,
                FrameWidth = FrameWidth,
                FrameHeight = FrameHeight,
                CurrentFrameIndex = CurrentFrameIndex,
            },
            Timeline = new ProjectTimelineState
            {
                SelectedTrackId = SelectedTrack?.Id,
                Tracks = Tracks.Select(BuildProjectTrackDocument).ToList(),
            },
            Annotation = new ProjectAnnotationState
            {
                Mode = "manual-keyframes",
                ContainsManualBoxes = Tracks.Any(track => track.Keyframes.Any(keyframe => keyframe.Enabled && keyframe.Box is not null)),
            },
            Learning = new ProjectLearningState
            {
                TrainingAssistEnabled = false,
                Parameters = new Dictionary<string, string>
                {
                    ["annotationSource"] = "user-keyframes",
                },
            },
        };

        return document;
    }

    private async Task ApplyProjectAsync(string projectPath, TrackBoxProjectDocument document)
    {
        var resolvedInputPath = document.Media.ResolvedInputPath;
        var fallbackInputPath = resolvedInputPath
            ?? document.Media.InputPath
            ?? document.Media.InputPathRelative
            ?? string.Empty;
        MediaSnapshot? loadedMedia = null;
        if (!string.IsNullOrWhiteSpace(resolvedInputPath) && File.Exists(resolvedInputPath))
        {
            using var probe = new MediaDocumentService();
            probe.Open(resolvedInputPath);
            loadedMedia = new MediaSnapshot(
                resolvedInputPath,
                probe.TotalFrames,
                probe.FramesPerSecond,
                probe.FrameWidth,
                probe.FrameHeight);
        }

        var timelineFrameCount = GetProjectTimelineFrameCount(document);
        var outputPath = document.Media.ResolvedOutputPath
            ?? document.Media.OutputPath
            ?? document.Media.OutputPathRelative
            ?? string.Empty;

        ClearEditorState();

        if (loadedMedia is not null)
        {
            _mediaService.Open(loadedMedia.Path);
            InputPath = loadedMedia.Path;
            IsMediaLoaded = true;
            TotalFrames = _mediaService.TotalFrames;
            FramesPerSecond = _mediaService.FramesPerSecond;
            FrameWidth = _mediaService.FrameWidth;
            FrameHeight = _mediaService.FrameHeight;
            CurrentFrameIndex = Math.Clamp(document.Media.CurrentFrameIndex, 0, Math.Max(0, TotalFrames - 1));
        }
        else
        {
            InputPath = fallbackInputPath;
            IsMediaLoaded = false;
            TotalFrames = timelineFrameCount;
            FramesPerSecond = document.Media.FramesPerSecond;
            FrameWidth = document.Media.FrameWidth;
            FrameHeight = document.Media.FrameHeight;
            CurrentFrameIndex = Math.Clamp(document.Media.CurrentFrameIndex, 0, Math.Max(0, TotalFrames - 1));
        }

        OutputPath = !string.IsNullOrWhiteSpace(outputPath)
            ? outputPath
            : !string.IsNullOrWhiteSpace(InputPath)
                ? BuildDefaultOutputPath(InputPath)
                : string.Empty;

        foreach (var trackDocument in document.Timeline.Tracks)
        {
            var track = BuildTimelineTrack(trackDocument);
            track.RebuildSegments(TotalFrames);
            Tracks.Add(track);
        }

        SelectedTrack = Tracks.FirstOrDefault(track => track.Id == document.Timeline.SelectedTrackId) ?? Tracks.FirstOrDefault();
        CurrentProjectPath = projectPath;
        ProgressValue = 0;

        if (loadedMedia is not null)
        {
            await LoadFrameAsync(CurrentFrameIndex);
            StatusText = $"Loaded project {Path.GetFileName(projectPath)}.";
            return;
        }

        CurrentFrameImage = null;
        SyncDraftBoxFromSelectedTrack();
        RebuildOverlayBoxes();
        StatusText = string.IsNullOrWhiteSpace(InputPath)
            ? $"Loaded project {Path.GetFileName(projectPath)} without a media file."
            : $"Loaded project {Path.GetFileName(projectPath)}, but the media file was not found: {InputPath}";
    }

    private void ClearEditorState()
    {
        _frameLoadTimer.Stop();
        _mediaService.Reset();
        CurrentFrameImage = null;
        InputPath = string.Empty;
        OutputPath = string.Empty;
        CurrentFrameIndex = 0;
        TotalFrames = 0;
        FramesPerSecond = 0;
        FrameWidth = 0;
        FrameHeight = 0;
        IsMediaLoaded = false;
        ProgressValue = 0;
        DraftBox = null;
        OverlayBoxes.Clear();
        Tracks.Clear();
        SelectedTrack = null;
    }

    private static ProjectTrackDocument BuildProjectTrackDocument(TimelineTrack track)
    {
        return new ProjectTrackDocument
        {
            Id = track.Id,
            Name = track.Name,
            WatermarkId = track.WatermarkId,
            WatermarkName = track.WatermarkName,
            ColorHex = track.ColorHex,
            Keyframes = track
                .OrderedKeyframes()
                .Select(keyframe => new ProjectKeyframeDocument
                {
                    Frame = keyframe.Frame,
                    Enabled = keyframe.Enabled,
                    Box = keyframe.Box?.Clone(),
                })
                .ToList(),
        };
    }

    private static TimelineTrack BuildTimelineTrack(ProjectTrackDocument trackDocument)
    {
        var track = new TimelineTrack
        {
            Id = string.IsNullOrWhiteSpace(trackDocument.Id) ? Guid.NewGuid().ToString("N") : trackDocument.Id,
            Name = trackDocument.Name,
            WatermarkId = trackDocument.WatermarkId,
            WatermarkName = string.IsNullOrWhiteSpace(trackDocument.WatermarkName) ? "Unassigned" : trackDocument.WatermarkName,
            ColorHex = string.IsNullOrWhiteSpace(trackDocument.ColorHex) ? "#4ADE80" : trackDocument.ColorHex,
        };

        foreach (var keyframeDocument in trackDocument.Keyframes.OrderBy(item => item.Frame))
        {
            track.Keyframes.Add(new BoxKeyframe
            {
                Frame = keyframeDocument.Frame,
                Enabled = keyframeDocument.Enabled,
                Box = keyframeDocument.Box?.Clone(),
            });
        }

        return track;
    }

    private static int GetProjectTimelineFrameCount(TrackBoxProjectDocument document)
    {
        var lastKeyframe = document.Timeline.Tracks
            .SelectMany(track => track.Keyframes)
            .Select(keyframe => keyframe.Frame)
            .DefaultIfEmpty(-1)
            .Max();

        return Math.Max(document.Media.TotalFrames, lastKeyframe + 1);
    }

    private string BuildDefaultOutputPath(string inputPath)
    {
        var directory = Path.GetDirectoryName(inputPath) ?? AppContext.BaseDirectory;
        var extension = MediaDocumentService.IsVideoFile(inputPath) ? ".mp4" : Path.GetExtension(inputPath);
        return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(inputPath)}_trackbox{extension}");
    }

    private string BuildDefaultProjectPath()
    {
        if (!string.IsNullOrWhiteSpace(InputPath))
        {
            var directory = Path.GetDirectoryName(InputPath) ?? AppContext.BaseDirectory;
            return Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(InputPath)}.trackbox.json");
        }

        return Path.Combine(GetProjectDialogDirectory(), "session.trackbox.json");
    }

    private string GetProjectDialogDirectory()
    {
        if (!string.IsNullOrWhiteSpace(CurrentProjectPath))
        {
            return Path.GetDirectoryName(CurrentProjectPath) ?? AppContext.BaseDirectory;
        }

        if (!string.IsNullOrWhiteSpace(InputPath))
        {
            return Path.GetDirectoryName(InputPath) ?? AppContext.BaseDirectory;
        }

        return AppContext.BaseDirectory;
    }

    private static SolidColorBrush CreateSolidBrush(string colorHex, byte alpha)
    {
        var color = (Color)ColorConverter.ConvertFromString(colorHex)!;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private BoxRect? NormalizeDraft(BoxRect? box)
    {
        if (box is null || box.Width < MinimumBoxSize || box.Height < MinimumBoxSize)
        {
            return null;
        }

        return box;
    }

    private BoxRect BuildBoxFromPoints(Point start, Point end)
    {
        var x1 = Math.Clamp((int)Math.Round(Math.Min(start.X, end.X)), 0, Math.Max(0, FrameWidth - 1));
        var y1 = Math.Clamp((int)Math.Round(Math.Min(start.Y, end.Y)), 0, Math.Max(0, FrameHeight - 1));
        var x2 = Math.Clamp((int)Math.Round(Math.Max(start.X, end.X)), 0, Math.Max(0, FrameWidth));
        var y2 = Math.Clamp((int)Math.Round(Math.Max(start.Y, end.Y)), 0, Math.Max(0, FrameHeight));

        return new BoxRect
        {
            X = x1,
            Y = y1,
            Width = Math.Max(0, x2 - x1),
            Height = Math.Max(0, y2 - y1),
        };
    }

    private bool TryGetOverlayInteraction(Point position, BoxRect? box, out OverlayInteractionMode interactionMode, out ResizeEdges resizeEdges)
    {
        resizeEdges = HitTestResizeEdges(box, position);
        if (resizeEdges != ResizeEdges.None)
        {
            interactionMode = OverlayInteractionMode.Resize;
            return true;
        }

        if (box is not null && IsPointInsideBox(box, position))
        {
            interactionMode = OverlayInteractionMode.Move;
            return true;
        }

        interactionMode = OverlayInteractionMode.Draw;
        return false;
    }

    private void ResetOverlayInteraction()
    {
        _overlayInteractionMode = OverlayInteractionMode.None;
        _activeResizeEdges = ResizeEdges.None;
        _interactionStartBox = null;
        _overlayInteractionChanged = false;
    }

    private void UpdateOverlayCursor(Point position)
    {
        if (!CanEditTrack)
        {
            OverlayInputCanvas.Cursor = Cursors.Arrow;
            return;
        }

        if (TryGetOverlayInteraction(position, DraftBox, out var interactionMode, out var resizeEdges))
        {
            OverlayInputCanvas.Cursor = GetCursorForInteraction(interactionMode, resizeEdges);
            return;
        }

        OverlayInputCanvas.Cursor = Cursors.Cross;
    }

    private Cursor GetCursorForInteraction(OverlayInteractionMode interactionMode, ResizeEdges resizeEdges)
    {
        if (interactionMode == OverlayInteractionMode.Move)
        {
            return Cursors.SizeAll;
        }

        return resizeEdges switch
        {
            ResizeEdges.Left or ResizeEdges.Right => Cursors.SizeWE,
            ResizeEdges.Top or ResizeEdges.Bottom => Cursors.SizeNS,
            ResizeEdges.Left | ResizeEdges.Top => Cursors.SizeNWSE,
            ResizeEdges.Right | ResizeEdges.Bottom => Cursors.SizeNWSE,
            ResizeEdges.Right | ResizeEdges.Top => Cursors.SizeNESW,
            ResizeEdges.Left | ResizeEdges.Bottom => Cursors.SizeNESW,
            _ => Cursors.Cross,
        };
    }

    private static ResizeEdges HitTestResizeEdges(BoxRect? box, Point position)
    {
        if (box is null)
        {
            return ResizeEdges.None;
        }

        var left = box.X;
        var top = box.Y;
        var right = box.Right;
        var bottom = box.Bottom;
        var withinVerticalGrip = position.Y >= top - ResizeGripSize && position.Y <= bottom + ResizeGripSize;
        var withinHorizontalGrip = position.X >= left - ResizeGripSize && position.X <= right + ResizeGripSize;
        var hitLeft = Math.Abs(position.X - left) <= ResizeGripSize && withinVerticalGrip;
        var hitRight = Math.Abs(position.X - right) <= ResizeGripSize && withinVerticalGrip;
        var hitTop = Math.Abs(position.Y - top) <= ResizeGripSize && withinHorizontalGrip;
        var hitBottom = Math.Abs(position.Y - bottom) <= ResizeGripSize && withinHorizontalGrip;

        var edges = ResizeEdges.None;
        if (hitLeft)
        {
            edges |= ResizeEdges.Left;
        }

        if (hitRight)
        {
            edges |= ResizeEdges.Right;
        }

        if (hitTop)
        {
            edges |= ResizeEdges.Top;
        }

        if (hitBottom)
        {
            edges |= ResizeEdges.Bottom;
        }

        return edges;
    }

    private static bool IsPointInsideBox(BoxRect box, Point position)
    {
        return position.X >= box.X
            && position.X <= box.Right
            && position.Y >= box.Y
            && position.Y <= box.Bottom;
    }

    private BoxRect TransformDraftBox(BoxRect startBox, Point currentPosition)
    {
        if (_overlayInteractionMode == OverlayInteractionMode.Move)
        {
            var movedX = Math.Clamp(
                startBox.X + (int)Math.Round(currentPosition.X - _dragStart.X),
                0,
                Math.Max(0, FrameWidth - startBox.Width));
            var movedY = Math.Clamp(
                startBox.Y + (int)Math.Round(currentPosition.Y - _dragStart.Y),
                0,
                Math.Max(0, FrameHeight - startBox.Height));

            return new BoxRect
            {
                X = movedX,
                Y = movedY,
                Width = startBox.Width,
                Height = startBox.Height,
            };
        }

        var left = startBox.X;
        var top = startBox.Y;
        var right = startBox.Right;
        var bottom = startBox.Bottom;
        var deltaX = (int)Math.Round(currentPosition.X - _dragStart.X);
        var deltaY = (int)Math.Round(currentPosition.Y - _dragStart.Y);

        if (_activeResizeEdges.HasFlag(ResizeEdges.Left))
        {
            left = Math.Clamp(startBox.X + deltaX, 0, right - MinimumBoxSize);
        }

        if (_activeResizeEdges.HasFlag(ResizeEdges.Right))
        {
            right = Math.Clamp(startBox.Right + deltaX, left + MinimumBoxSize, FrameWidth);
        }

        if (_activeResizeEdges.HasFlag(ResizeEdges.Top))
        {
            top = Math.Clamp(startBox.Y + deltaY, 0, bottom - MinimumBoxSize);
        }

        if (_activeResizeEdges.HasFlag(ResizeEdges.Bottom))
        {
            bottom = Math.Clamp(startBox.Bottom + deltaY, top + MinimumBoxSize, FrameHeight);
        }

        return new BoxRect
        {
            X = left,
            Y = top,
            Width = Math.Max(MinimumBoxSize, right - left),
            Height = Math.Max(MinimumBoxSize, bottom - top),
        };
    }

    private static bool BoxEquals(BoxRect? left, BoxRect? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return left.X == right.X
            && left.Y == right.Y
            && left.Width == right.Width
            && left.Height == right.Height;
    }

    private bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private sealed record MediaSnapshot(
        string Path,
        int TotalFrames,
        double FramesPerSecond,
        int FrameWidth,
        int FrameHeight);
}
