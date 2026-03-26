using System.IO;
using System.Text.Json;
using TrackBoxStudio.Models;

namespace TrackBoxStudio.Services;

public sealed class WatermarkRegistryService
{
    private readonly string _registryDirectory;
    private readonly string _registryPath;
    private readonly string _legacyRegistryPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WatermarkRegistryService(string? registryDirectory = null, string? legacyDataDirectory = null)
    {
        _registryDirectory = string.IsNullOrWhiteSpace(registryDirectory)
            ? GetDefaultRegistryDirectory()
            : registryDirectory;

        var resolvedLegacyDataDirectory = string.IsNullOrWhiteSpace(legacyDataDirectory)
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : legacyDataDirectory;

        _registryPath = Path.Combine(_registryDirectory, "watermark-registry.json");
        _legacyRegistryPath = Path.Combine(resolvedLegacyDataDirectory, "watermark-registry.json");
    }

    public string RegistryPath => _registryPath;

    public async Task<IReadOnlyList<WatermarkDefinition>> LoadAsync()
    {
        Directory.CreateDirectory(_registryDirectory);
        if (!File.Exists(_registryPath))
        {
            await TryMigrateLegacyRegistryAsync();
        }

        if (!File.Exists(_registryPath))
        {
            return [];
        }

        await using var stream = File.OpenRead(_registryPath);
        var items = await JsonSerializer.DeserializeAsync<List<WatermarkDefinition>>(stream, _jsonOptions);
        return items ?? [];
    }

    public async Task SaveAsync(IEnumerable<WatermarkDefinition> definitions)
    {
        Directory.CreateDirectory(_registryDirectory);
        await using var stream = File.Create(_registryPath);
        await JsonSerializer.SerializeAsync(stream, definitions.OrderBy(item => item.Name), _jsonOptions);
    }

    private async Task TryMigrateLegacyRegistryAsync()
    {
        if (!File.Exists(_legacyRegistryPath))
        {
            return;
        }

        if (string.Equals(
            Path.GetFullPath(_legacyRegistryPath),
            Path.GetFullPath(_registryPath),
            StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        await using var stream = File.OpenRead(_legacyRegistryPath);
        var items = await JsonSerializer.DeserializeAsync<List<WatermarkDefinition>>(stream, _jsonOptions);
        if (items is null)
        {
            return;
        }

        await SaveAsync(items);
    }

    private static string GetDefaultRegistryDirectory()
    {
        var appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(appDataDirectory))
        {
            appDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (string.IsNullOrWhiteSpace(appDataDirectory))
        {
            appDataDirectory = AppContext.BaseDirectory;
        }

        return Path.Combine(appDataDirectory, "TrackBoxStudio");
    }
}
