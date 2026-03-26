using System.IO;
using OpenCvSharp;
using TrackBoxStudio.Models;

namespace TrackBoxStudio.Services;

public sealed class InpaintCoveragePreviewService
{
    private sealed record PreviewSampleDefinition(string Title, string CropAssetPath, string FullFrameAssetPath);

    public sealed record PreviewRenderResult(string TempDirectory, IReadOnlyList<string> OutputCropPaths);

    private static readonly BoxRect PreviewBox = new()
    {
        X = 463,
        Y = 598,
        Width = 197,
        Height = 77,
    };

    private readonly InpaintProcessingService _processingService = new();

    public IReadOnlyList<(string Title, string CropAssetPath)> GetReferenceSamples()
    {
        return BuildSampleDefinitions()
            .Select(sample => (sample.Title, sample.CropAssetPath))
            .ToList();
    }

    public async Task<PreviewRenderResult> RenderPreviewAsync(
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var samples = BuildSampleDefinitions();
        var tempDirectory = Path.Combine(Path.GetTempPath(), $"trackbox-inpaint-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var inputVideoPath = Path.Combine(tempDirectory, "preview-input.mp4");
            var outputVideoPath = Path.Combine(tempDirectory, "preview-output.mp4");
            BuildPreviewVideo(samples, inputVideoPath);

            status?.Report("Running preview on the sample set...");
            await _processingService.ProcessAsync(
                inputVideoPath,
                outputVideoPath,
                BuildPreviewTracks(),
                renderMaskOnly: true,
                progress: null,
                status,
                cancellationToken);

            var outputCropPaths = ExtractPreviewCrops(outputVideoPath, tempDirectory, samples.Count);
            return new PreviewRenderResult(tempDirectory, outputCropPaths);
        }
        catch
        {
            Directory.Delete(tempDirectory, recursive: true);
            throw;
        }
    }

    private static void BuildPreviewVideo(IReadOnlyList<PreviewSampleDefinition> samples, string outputVideoPath)
    {
        using var firstFrame = Cv2.ImRead(samples[0].FullFrameAssetPath, ImreadModes.Color);
        if (firstFrame.Empty())
        {
            throw new InvalidOperationException($"Failed to load preview frame: {samples[0].FullFrameAssetPath}");
        }

        using var writer = new VideoWriter(
            outputVideoPath,
            FourCC.MP4V,
            1.0,
            new OpenCvSharp.Size(firstFrame.Width, firstFrame.Height));
        if (!writer.IsOpened())
        {
            throw new InvalidOperationException($"Failed to create preview video: {outputVideoPath}");
        }

        writer.Write(firstFrame);
        foreach (var sample in samples.Skip(1))
        {
            using var frame = Cv2.ImRead(sample.FullFrameAssetPath, ImreadModes.Color);
            if (frame.Empty())
            {
                throw new InvalidOperationException($"Failed to load preview frame: {sample.FullFrameAssetPath}");
            }

            if (frame.Width != firstFrame.Width || frame.Height != firstFrame.Height)
            {
                throw new InvalidOperationException("Preview frames must all share the same size.");
            }

            writer.Write(frame);
        }
    }

    private static IReadOnlyList<TimelineTrack> BuildPreviewTracks()
    {
        var track = new TimelineTrack
        {
            Name = "Preview Track",
            WatermarkName = "Preview Watermark",
            ColorHex = "#38BDF8",
        };

        track.Keyframes.Add(new BoxKeyframe
        {
            Frame = 0,
            Enabled = true,
            Box = PreviewBox.Clone(),
        });

        return [track];
    }

    private static IReadOnlyList<string> ExtractPreviewCrops(string outputVideoPath, string tempDirectory, int expectedFrameCount)
    {
        using var capture = new VideoCapture(outputVideoPath);
        if (!capture.IsOpened())
        {
            throw new InvalidOperationException($"Failed to open preview output video: {outputVideoPath}");
        }

        var outputPaths = new List<string>(expectedFrameCount);
        for (var frameIndex = 0; frameIndex < expectedFrameCount; frameIndex++)
        {
            using var frame = new Mat();
            if (!capture.Read(frame) || frame.Empty())
            {
                throw new InvalidOperationException($"Failed to read preview output frame {frameIndex}.");
            }

            var cropRect = BuildClampedCropRect(frame.Width, frame.Height);
            using var crop = new Mat(frame, cropRect);

            var outputPath = Path.Combine(tempDirectory, $"preview-crop-{frameIndex}.png");
            Cv2.ImWrite(outputPath, crop);
            outputPaths.Add(outputPath);
        }

        return outputPaths;
    }

    private static Rect BuildClampedCropRect(int frameWidth, int frameHeight)
    {
        var x = Math.Clamp(PreviewBox.X, 0, Math.Max(0, frameWidth - 1));
        var y = Math.Clamp(PreviewBox.Y, 0, Math.Max(0, frameHeight - 1));
        var width = Math.Max(1, Math.Min(PreviewBox.Width, frameWidth - x));
        var height = Math.Max(1, Math.Min(PreviewBox.Height, frameHeight - y));
        return new Rect(x, y, width, height);
    }

    private static IReadOnlyList<PreviewSampleDefinition> BuildSampleDefinitions()
    {
        return
        [
            new PreviewSampleDefinition("Start", ResolveAssetPath("ris-frame-115-box.png"), ResolveAssetPath("ris-frame-115-full.png")),
            new PreviewSampleDefinition("Middle", ResolveAssetPath("ris-frame-130-box.png"), ResolveAssetPath("ris-frame-130-full.png")),
            new PreviewSampleDefinition("End", ResolveAssetPath("ris-frame-227-box.png"), ResolveAssetPath("ris-frame-227-full.png")),
        ];
    }

    private static string ResolveAssetPath(string fileName)
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "InpaintConfigSamples", fileName),
        };

        var projectRoot = FindProjectRoot();
        if (!string.IsNullOrWhiteSpace(projectRoot))
        {
            candidates.Add(Path.Combine(projectRoot, "Assets", "InpaintConfigSamples", fileName));
        }

        foreach (var candidate in candidates.Where(File.Exists))
        {
            return candidate;
        }

        throw new FileNotFoundException($"Preview asset was not found: {fileName}");
    }

    private static string? FindProjectRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TrackBoxStudio.csproj")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
