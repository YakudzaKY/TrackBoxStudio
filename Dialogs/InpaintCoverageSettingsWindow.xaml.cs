using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media.Imaging;
using TrackBoxStudio.Infrastructure;
using TrackBoxStudio.Models;
using TrackBoxStudio.Services;

namespace TrackBoxStudio.Dialogs;

public partial class InpaintCoverageSettingsWindow : Window, INotifyPropertyChanged
{
    public sealed class PreviewCard : BindableBase
    {
        private BitmapSource? _image;

        public required string Title { get; init; }

        public BitmapSource? Image
        {
            get => _image;
            set => SetProperty(ref _image, value);
        }
    }

    private readonly InpaintCoverageSettingsService _settingsService = new();
    private readonly InpaintCoveragePreviewService _previewService = new();
    private bool _isInitialized;
    private bool _isBusy;
    private string _statusText = "Loading stable-mask tuning...";
    private string? _previewTempDirectory;
    private string _selectedInpaintStrategy = "lama";

    public InpaintCoverageSettingsWindow()
    {
        InitializeComponent();
        DataContext = this;

        Loaded += Window_Loaded;
        Closed += (_, _) => CleanupPreviewTempDirectory();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<InpaintCoverageSettingEntry> CoverageSettings { get; } = [];

    public ObservableCollection<PreviewCard> ReferenceSamples { get; } = [];

    public ObservableCollection<PreviewCard> PreviewSamples { get; } = [];

    public IReadOnlyList<string> InpaintStrategyOptions { get; } =
    [
        "lama",
        "whiteness-delta",
        "opencv-telea",
        "opencv-ns",
    ];

    public string SelectedInpaintStrategy
    {
        get => _selectedInpaintStrategy;
        set => SetProperty(ref _selectedInpaintStrategy, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_isInitialized)
        {
            return;
        }

        _isInitialized = true;
        try
        {
            await LoadStateAsync();
            StatusText = "Adjust values and press Try.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load tuning window: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Inpaint tuning failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task LoadStateAsync()
    {
        CoverageSettings.Clear();
        foreach (var entry in await _settingsService.LoadEntriesAsync())
        {
            CoverageSettings.Add(entry);
        }

        ReferenceSamples.Clear();
        PreviewSamples.Clear();
        foreach (var sample in _previewService.GetReferenceSamples())
        {
            ReferenceSamples.Add(new PreviewCard
            {
                Title = sample.Title,
                Image = LoadBitmap(sample.CropAssetPath),
            });

            PreviewSamples.Add(new PreviewCard
            {
                Title = sample.Title,
                Image = null,
            });
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _isBusy = true;
            StatusText = "Saving stable-mask config...";
            await SaveSettingsCoreAsync();
            StatusText = "Stable-mask config saved.";
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void ResetToDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        foreach (var entry in CoverageSettings)
        {
            entry.ValueText = entry.DefaultValueText;
        }

        StatusText = "Default values restored in the form.";
    }

    private void ResetEntryToDefault_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        if (sender is not FrameworkElement { DataContext: InpaintCoverageSettingEntry entry })
        {
            return;
        }

        entry.ResetToDefault();
        StatusText = $"'{entry.DisplayName}' restored to default.";
    }

    private async void Try_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy)
        {
            return;
        }

        try
        {
            _isBusy = true;
            StatusText = "Saving stable-mask config before preview...";
            await SaveSettingsCoreAsync();

            var previewStatus = new Progress<string>(message =>
            {
                if (!string.IsNullOrWhiteSpace(message))
                {
                    StatusText = message;
                }
            });

            var result = await _previewService.RenderPreviewAsync(
                previewStatus,
                CancellationToken.None,
                SelectedInpaintStrategy);
            ApplyPreviewResult(result);
            StatusText = "Preview refreshed.";
        }
        catch (Exception ex)
        {
            StatusText = $"Preview failed: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Preview failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isBusy = false;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async Task SaveSettingsCoreAsync()
    {
        await _settingsService.SaveEntriesAsync(CoverageSettings);
    }

    private void ApplyPreviewResult(InpaintCoveragePreviewService.PreviewRenderResult result)
    {
        CleanupPreviewTempDirectory();
        _previewTempDirectory = result.TempDirectory;

        for (var index = 0; index < ReferenceSamples.Count; index++)
        {
            var maskImage = index < result.MaskCropPaths.Count
                ? LoadBitmap(result.MaskCropPaths[index])
                : null;
            if (maskImage != null)
            {
                ReferenceSamples[index].Image = maskImage;
            }
        }

        for (var index = 0; index < PreviewSamples.Count; index++)
        {
            var image = index < result.OutputCropPaths.Count
                ? LoadBitmap(result.OutputCropPaths[index])
                : null;
            PreviewSamples[index].Image = image;
        }
    }

    private void CleanupPreviewTempDirectory()
    {
        if (string.IsNullOrWhiteSpace(_previewTempDirectory) || !Directory.Exists(_previewTempDirectory))
        {
            _previewTempDirectory = null;
            return;
        }

        try
        {
            Directory.Delete(_previewTempDirectory, recursive: true);
        }
        catch
        {
            // Best-effort preview cleanup.
        }
        finally
        {
            _previewTempDirectory = null;
        }
    }

    private static BitmapSource? LoadBitmap(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
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
}
