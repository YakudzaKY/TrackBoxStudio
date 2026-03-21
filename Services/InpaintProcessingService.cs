using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using TrackBoxStudio.Models;

namespace TrackBoxStudio.Services;

public sealed class InpaintProcessingService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task ProcessAsync(
        string inputPath,
        string outputPath,
        IReadOnlyList<TimelineTrack> tracks,
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            throw new ArgumentException("Input path cannot be empty.", nameof(inputPath));
        }

        if (string.IsNullOrWhiteSpace(outputPath))
        {
            throw new ArgumentException("Output path cannot be empty.", nameof(outputPath));
        }

        if (tracks.Count == 0 || tracks.All(track => track.Keyframes.Count == 0))
        {
            throw new InvalidOperationException("There are no timeline keyframes to process.");
        }

        var job = BuildJob(inputPath, outputPath, tracks);
        var payloadPath = Path.Combine(Path.GetTempPath(), $"trackbox-lama-{Guid.NewGuid():N}.json");

        try
        {
            await using (var stream = File.Create(payloadPath))
            {
                await JsonSerializer.SerializeAsync(stream, job, _jsonOptions, cancellationToken);
            }

            await RunLamaProcessAsync(payloadPath, progress, status, cancellationToken);
        }
        finally
        {
            if (File.Exists(payloadPath))
            {
                File.Delete(payloadPath);
            }
        }
    }

    private static LamaProcessingJobDocument BuildJob(string inputPath, string outputPath, IReadOnlyList<TimelineTrack> tracks)
    {
        return new LamaProcessingJobDocument
        {
            InputPath = inputPath,
            OutputPath = outputPath,
            DevicePreference = "cuda-preferred",
            MaskPadding = 0,
            Tracks = tracks
                .Where(track => track.Keyframes.Count > 0)
                .Select(track => new LamaProcessingTrackDocument
                {
                    Id = track.Id,
                    Name = track.Name,
                    Keyframes = track
                        .OrderedKeyframes()
                        .Select(keyframe => new LamaProcessingKeyframeDocument
                        {
                            Frame = keyframe.Frame,
                            Enabled = keyframe.Enabled,
                            Box = keyframe.Box?.Clone(),
                        })
                        .ToList(),
                })
                .ToList(),
        };
    }

    private async Task RunLamaProcessAsync(
        string payloadPath,
        IProgress<double>? progress,
        IProgress<string>? status,
        CancellationToken cancellationToken)
    {
        var pythonExecutable = ResolvePythonExecutable();
        var runnerScript = ResolveRunnerScriptPath();
        status?.Report($"Launching LaMa backend: {Path.GetFileName(pythonExecutable)}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = pythonExecutable,
                Arguments = $"\"{runnerScript}\" \"{payloadPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(runnerScript) ?? AppContext.BaseDirectory,
            },
            EnableRaisingEvents = true,
        };

        var errorLines = new List<string>();
        var stdoutTaskCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrTaskCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                stdoutTaskCompletion.TrySetResult();
                return;
            }

            HandleOutputLine(args.Data, progress, status);
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null)
            {
                stderrTaskCompletion.TrySetResult();
                return;
            }

            lock (errorLines)
            {
                errorLines.Add(args.Data);
                if (errorLines.Count > 40)
                {
                    errorLines.RemoveAt(0);
                }
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the LaMa backend process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAll(stdoutTaskCompletion.Task, stderrTaskCompletion.Task);
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        if (process.ExitCode != 0)
        {
            var details = errorLines.Count == 0
                ? "The LaMa backend exited without detailed stderr output."
                : string.Join(Environment.NewLine, errorLines);

            throw new InvalidOperationException(
                $"LaMa backend failed with exit code {process.ExitCode}.{Environment.NewLine}{details}");
        }
    }

    private static void HandleOutputLine(string line, IProgress<double>? progress, IProgress<string>? status)
    {
        if (line.StartsWith("PROGRESS ", StringComparison.OrdinalIgnoreCase))
        {
            var rawValue = line["PROGRESS ".Length..].Trim();
            if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                progress?.Report(value);
            }
            return;
        }

        if (line.StartsWith("STATUS ", StringComparison.OrdinalIgnoreCase))
        {
            status?.Report(line["STATUS ".Length..].Trim());
            return;
        }

        if (!string.IsNullOrWhiteSpace(line))
        {
            status?.Report(line.Trim());
        }
    }

    private static string ResolvePythonExecutable()
    {
        var candidates = new List<string>();

        AppendIfPresent(candidates, Environment.GetEnvironmentVariable("TRACKBOX_PYTHON_EXE"));
        AppendIfPresent(candidates, Environment.GetEnvironmentVariable("LAMA_PYTHON_EXE"));
        AppendIfPresent(candidates, Path.Combine(AppContext.BaseDirectory, "python", "python.exe"));

        var projectRoot = FindProjectRoot();
        if (projectRoot is not null)
        {
            AppendIfPresent(candidates, Path.Combine(projectRoot, "python", "python.exe"));
            var parentDirectory = Directory.GetParent(projectRoot)?.FullName;
            if (!string.IsNullOrWhiteSpace(parentDirectory))
            {
                AppendIfPresent(candidates, Path.Combine(parentDirectory, "Lama", "python", "python.exe"));
            }
        }

        foreach (var candidate in candidates.Where(File.Exists))
        {
            return candidate;
        }

        throw new FileNotFoundException(
            "LaMa Python runtime was not found. Set TRACKBOX_PYTHON_EXE or place a compatible python.exe beside the app.");
    }

    private static string ResolveRunnerScriptPath()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Scripts", "lama_inpaint_runner.py"),
        };

        var projectRoot = FindProjectRoot();
        if (projectRoot is not null)
        {
            candidates.Add(Path.Combine(projectRoot, "Scripts", "lama_inpaint_runner.py"));
        }

        foreach (var candidate in candidates.Where(File.Exists))
        {
            return candidate;
        }

        throw new FileNotFoundException("LaMa runner script was not found in the TrackBoxStudio Scripts folder.");
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

    private static void AppendIfPresent(List<string> candidates, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            candidates.Add(path);
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
