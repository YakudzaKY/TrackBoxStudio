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
        document.Media.ResolvedInputPath = ResolvePath(baseDirectory, document.Media.InputPathRelative, document.Media.InputPath);
        document.Media.ResolvedOutputPath = ResolvePath(baseDirectory, document.Media.OutputPathRelative, document.Media.OutputPath);
    }

    private static void PopulateRelativePaths(TrackBoxProjectDocument document, string projectPath)
    {
        var baseDirectory = Path.GetDirectoryName(projectPath) ?? AppContext.BaseDirectory;
        document.Media.InputPathRelative = BuildRelativePath(baseDirectory, document.Media.InputPath);
        document.Media.OutputPathRelative = BuildRelativePath(baseDirectory, document.Media.OutputPath);
        document.Media.ResolvedInputPath = document.Media.InputPath;
        document.Media.ResolvedOutputPath = document.Media.OutputPath;
    }

    private static string? ResolvePath(string baseDirectory, string? relativePath, string? absolutePath)
    {
        if (!string.IsNullOrWhiteSpace(relativePath))
        {
            var combined = Path.GetFullPath(Path.Combine(baseDirectory, relativePath));
            if (File.Exists(combined) || Directory.Exists(Path.GetDirectoryName(combined) ?? string.Empty))
            {
                return combined;
            }
        }

        return string.IsNullOrWhiteSpace(absolutePath) ? null : absolutePath;
    }

    private static string? BuildRelativePath(string baseDirectory, string? absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathRooted(absolutePath))
        {
            return null;
        }

        return Path.GetRelativePath(baseDirectory, absolutePath);
    }
}
