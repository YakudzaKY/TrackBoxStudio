using System.Diagnostics;
using System.IO;
using OpenCvSharp;
using TrackBoxStudio.Models;

namespace TrackBoxStudio.Services;

public sealed class InpaintProcessingService
{
    private sealed class TrackRuntime
    {
        public required BoxKeyframe[] Keyframes { get; init; }

        public int Cursor { get; set; }

        public BoxKeyframe? Active { get; set; }
    }

    public async Task ProcessAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<TimelineTrack> tracks,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        if (MediaDocumentService.IsVideoFile(inputPath))
        {
            await Task.Run(() => ProcessVideo(inputPath, outputPath, tracks, progress, cancellationToken), cancellationToken);
            return;
        }

        await Task.Run(() => ProcessImage(inputPath, outputPath, tracks), cancellationToken);
        progress?.Report(1.0);
    }

    private void ProcessImage(string inputPath, string outputPath, IReadOnlyList<TimelineTrack> tracks)
    {
        using var image = Cv2.ImRead(inputPath, ImreadModes.Color);
        if (image.Empty())
        {
            throw new InvalidOperationException($"Failed to read image: {inputPath}");
        }

        using var mask = BuildMask(image.Size(), 0, CreateRuntimeTracks(tracks));
        using var result = ApplyInpaintIfNeeded(image, mask);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        Cv2.ImWrite(outputPath, result);
    }

    private void ProcessVideo(
        string inputPath,
        string outputPath,
        IReadOnlyList<TimelineTrack> tracks,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var capture = new VideoCapture(inputPath);
        if (!capture.IsOpened())
        {
            throw new InvalidOperationException($"Failed to open video: {inputPath}");
        }

        var totalFrames = Math.Max(1, (int)capture.Get(VideoCaptureProperties.FrameCount));
        var fps = capture.Get(VideoCaptureProperties.Fps);
        var width = (int)capture.Get(VideoCaptureProperties.FrameWidth);
        var height = (int)capture.Get(VideoCaptureProperties.FrameHeight);
        var outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);

        var tempVideoPath = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(outputPath)}.video-temp.mp4");
        using var writer = new VideoWriter(
            tempVideoPath,
            FourCC.MP4V,
            fps > 0 ? fps : 30,
            new OpenCvSharp.Size(width, height));

        if (!writer.IsOpened())
        {
            throw new InvalidOperationException($"Failed to create output video: {tempVideoPath}");
        }

        var runtimeTracks = CreateRuntimeTracks(tracks);
        using var frame = new Mat();
        for (var frameIndex = 0; frameIndex < totalFrames; frameIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!capture.Read(frame) || frame.Empty())
            {
                break;
            }

            using var mask = BuildMask(frame.Size(), frameIndex, runtimeTracks);
            using var processed = ApplyInpaintIfNeeded(frame, mask);
            writer.Write(processed);
            progress?.Report((frameIndex + 1d) / totalFrames);
        }

        writer.Release();
        CopyAudioIfPossible(inputPath, outputPath, tempVideoPath);
    }

    private static List<TrackRuntime> CreateRuntimeTracks(IReadOnlyList<TimelineTrack> tracks)
    {
        return tracks
            .Select(track => new TrackRuntime
            {
                Keyframes = track.OrderedKeyframes().Select(keyframe => keyframe.Clone()).ToArray(),
                Cursor = 0,
                Active = null,
            })
            .ToList();
    }

    private static Mat BuildMask(OpenCvSharp.Size size, int frameIndex, List<TrackRuntime> runtimeTracks)
    {
        var mask = new Mat(size, MatType.CV_8UC1, Scalar.Black);
        foreach (var runtime in runtimeTracks)
        {
            var active = AdvanceRuntime(runtime, frameIndex);
            if (active?.Enabled != true || active.Box is null)
            {
                continue;
            }

            var rect = NormalizeRect(active.Box, size);
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                continue;
            }

            Cv2.Rectangle(mask, rect, Scalar.White, -1);
        }

        return mask;
    }

    private static BoxKeyframe? AdvanceRuntime(TrackRuntime runtime, int frameIndex)
    {
        while (runtime.Cursor < runtime.Keyframes.Length && runtime.Keyframes[runtime.Cursor].Frame <= frameIndex)
        {
            runtime.Active = runtime.Keyframes[runtime.Cursor];
            runtime.Cursor++;
        }

        return runtime.Active;
    }

    private static Rect NormalizeRect(BoxRect box, OpenCvSharp.Size bounds)
    {
        var x = Math.Clamp(box.X, 0, Math.Max(0, bounds.Width - 1));
        var y = Math.Clamp(box.Y, 0, Math.Max(0, bounds.Height - 1));
        var width = Math.Clamp(box.Width, 0, bounds.Width - x);
        var height = Math.Clamp(box.Height, 0, bounds.Height - y);
        return new Rect(x, y, width, height);
    }

    private static Mat ApplyInpaintIfNeeded(Mat frame, Mat mask)
    {
        if (Cv2.CountNonZero(mask) == 0)
        {
            return frame.Clone();
        }

        var result = new Mat();
        Cv2.Inpaint(frame, mask, result, 3, InpaintTypes.Telea);
        return result;
    }

    private static void CopyAudioIfPossible(string inputPath, string outputPath, string tempVideoPath)
    {
        var ffmpeg = ResolveFfmpeg();
        if (ffmpeg is null)
        {
            MoveTempToOutput(tempVideoPath, outputPath);
            return;
        }

        var arguments =
            $"-y -i \"{tempVideoPath}\" -i \"{inputPath}\" -map 0:v:0 -map 1:a? -c:v copy -c:a aac -shortest \"{outputPath}\"";
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        try
        {
            using var process = Process.Start(startInfo);
            process?.WaitForExit();
            if (process is not null && process.ExitCode == 0 && File.Exists(outputPath))
            {
                File.Delete(tempVideoPath);
                return;
            }
        }
        catch
        {
            // Fall back to the silent video if ffmpeg is missing or fails.
        }

        MoveTempToOutput(tempVideoPath, outputPath);
    }

    private static void MoveTempToOutput(string tempVideoPath, string outputPath)
    {
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }

        File.Move(tempVideoPath, outputPath);
    }

    private static string? ResolveFfmpeg()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
