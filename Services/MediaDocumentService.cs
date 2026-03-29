using System.IO;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace TrackBoxStudio.Services;

public sealed class MediaDocumentService : IDisposable
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4",
        ".avi",
        ".mov",
        ".mkv",
        ".flv",
        ".wmv",
        ".webm",
    };

    private readonly object _sync = new();
    private VideoCapture? _capture;
    private Mat? _cachedImage;

    public string? SourcePath { get; private set; }

    public bool IsVideo { get; private set; }

    public int TotalFrames { get; private set; }

    public double FramesPerSecond { get; private set; }

    public int FrameWidth { get; private set; }

    public int FrameHeight { get; private set; }

    public bool HasMedia => !string.IsNullOrWhiteSpace(SourcePath);

    public static bool IsVideoFile(string path)
    {
        var extension = Path.GetExtension(path);
        return VideoExtensions.Contains(extension);
    }

    public void Open(string path)
    {
        Reset();

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Path cannot be empty.", nameof(path));
        }

        SourcePath = path;
        IsVideo = IsVideoFile(path);
        if (IsVideo)
        {
            _capture = new VideoCapture(path);
            if (!_capture.IsOpened())
            {
                throw new InvalidOperationException($"Failed to open video: {path}");
            }

            TotalFrames = Math.Max(1, (int)_capture.Get(VideoCaptureProperties.FrameCount));
            FramesPerSecond = _capture.Get(VideoCaptureProperties.Fps);
            FrameWidth = (int)_capture.Get(VideoCaptureProperties.FrameWidth);
            FrameHeight = (int)_capture.Get(VideoCaptureProperties.FrameHeight);
            return;
        }

        var mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat.Empty())
        {
            mat.Dispose();
            throw new InvalidOperationException($"Failed to open image: {path}");
        }

        _cachedImage = mat;

        TotalFrames = 1;
        FramesPerSecond = 0;
        FrameWidth = mat.Width;
        FrameHeight = mat.Height;
    }

    public void Reset()
    {
        DisposeCapture();

        _cachedImage?.Dispose();
        _cachedImage = null;

        SourcePath = null;
        IsVideo = false;
        TotalFrames = 0;
        FramesPerSecond = 0;
        FrameWidth = 0;
        FrameHeight = 0;
    }

    public BitmapSource LoadBitmapSource(int frameIndex)
    {
        using var mat = LoadMat(frameIndex);
        return BitmapSourceFactory.ToBitmapSource(mat);
    }

    public Mat LoadMat(int frameIndex)
    {
        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            throw new InvalidOperationException("No media loaded.");
        }

        if (!IsVideo)
        {
            if (_cachedImage is null || _cachedImage.Empty())
            {
                throw new InvalidOperationException($"Cached image is unavailable: {SourcePath}");
            }

            return _cachedImage.Clone();
        }

        lock (_sync)
        {
            if (_capture is null)
            {
                throw new InvalidOperationException("Video capture is not available.");
            }

            frameIndex = Math.Clamp(frameIndex, 0, Math.Max(0, TotalFrames - 1));
            _capture.Set(VideoCaptureProperties.PosFrames, frameIndex);
            var frame = new Mat();
            if (!_capture.Read(frame) || frame.Empty())
            {
                frame.Dispose();
                throw new InvalidOperationException($"Failed to read frame {frameIndex}.");
            }

            return frame;
        }
    }

    public void Dispose()
    {
        Reset();
        GC.SuppressFinalize(this);
    }

    private void DisposeCapture()
    {
        lock (_sync)
        {
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }
    }
}
