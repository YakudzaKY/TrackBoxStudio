using System.IO;
using System.Text.Json;
using TrackBoxStudio.Models;

namespace TrackBoxStudio.Services;

public sealed class ProjectPersistenceService
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public async Task<TrackBoxProjectDocument> LoadAsync(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));
        }

        await using var stream = File.OpenRead(projectPath);
        var document = await JsonSerializer.DeserializeAsync<TrackBoxProjectDocument>(stream, _jsonOptions);
        if (document is null)
        {
            throw new InvalidOperationException("Project file is empty or invalid.");
        }

        ResolvePaths(document, projectPath);
        return document;
    }

    public async Task SaveAsync(string projectPath, TrackBoxProjectDocument document)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            throw new ArgumentException("Project path cannot be empty.", nameof(projectPath));
        }

        if (document is null)
        {
            throw new ArgumentNullException(nameof(document));
        }

        PopulateRelativePaths(document, projectPath);
        Directory.CreateDirectory(Path.GetDirectoryName(projectPath) ?? AppContext.BaseDirectory);

        await using var stream = File.Create(projectPath);
        await JsonSerializer.SerializeAsync(stream, document, _jsonOptions);
    }

    private static void ResolvePaths(TrackBoxProjectDocument document, string projectPath)
    {
        var baseDirectory = Path.GetDirectoryName(projectPath) ?? AppContext.BaseDirectory;
        document.Media.ResolvedInputPath = ResolveInputPath(baseDirectory, document.Media.InputPath, document.Media.InputPathRelative);
        document.Media.ResolvedOutputPath = ResolveOutputPath(baseDirectory, document.Media.OutputPath, document.Media.OutputPathRelative);
    }

    private static void PopulateRelativePaths(TrackBoxProjectDocument document, string projectPath)
    {
        var baseDirectory = Path.GetDirectoryName(projectPath) ?? AppContext.BaseDirectory;
        document.Media.InputPath = NormalizeAbsolutePath(document.Media.InputPath) ?? document.Media.InputPath;
        document.Media.OutputPath = NormalizeAbsolutePath(document.Media.OutputPath) ?? document.Media.OutputPath;
        document.Media.InputPathRelative = BuildRelativePath(baseDirectory, document.Media.InputPath);
        document.Media.OutputPathRelative = BuildRelativePath(baseDirectory, document.Media.OutputPath);
        document.Media.ResolvedInputPath = document.Media.InputPath;
        document.Media.ResolvedOutputPath = document.Media.OutputPath;
    }

    private static string? ResolveInputPath(string baseDirectory, string? absolutePath, string? relativePath)
    {
        var normalizedAbsolutePath = NormalizeAbsolutePath(absolutePath);
        var normalizedRelativePath = NormalizeRelativePath(baseDirectory, relativePath);

        if (PathPointsToExistingFile(normalizedAbsolutePath))
        {
            return normalizedAbsolutePath;
        }

        if (PathPointsToExistingFile(normalizedRelativePath))
        {
            return normalizedRelativePath;
        }

        return normalizedAbsolutePath ?? normalizedRelativePath;
    }

    private static string? ResolveOutputPath(string baseDirectory, string? absolutePath, string? relativePath)
    {
        var normalizedAbsolutePath = NormalizeAbsolutePath(absolutePath);
        var normalizedRelativePath = NormalizeRelativePath(baseDirectory, relativePath);

        if (PathPointsToExistingFile(normalizedAbsolutePath) || PathHasExistingParentDirectory(normalizedAbsolutePath))
        {
            return normalizedAbsolutePath;
        }

        if (PathPointsToExistingFile(normalizedRelativePath) || PathHasExistingParentDirectory(normalizedRelativePath))
        {
            return normalizedRelativePath;
        }

        return normalizedAbsolutePath ?? normalizedRelativePath;
    }

    private static string? BuildRelativePath(string baseDirectory, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath))
        {
            return null;
        }

        return Path.GetRelativePath(baseDirectory, absolutePath);
    }

    private static string? NormalizeAbsolutePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
        {
            return null;
        }

        return Path.GetFullPath(path);
    }

    private static string? NormalizeRelativePath(string baseDirectory, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return null;
        }

        var candidatePath = Path.IsPathRooted(relativePath)
            ? relativePath
            : Path.Combine(baseDirectory, relativePath);

        return Path.GetFullPath(candidatePath);
    }

    private static bool PathPointsToExistingFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path);
    }

    private static bool PathHasExistingParentDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(path);
        return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory);
    }
}
