using System.IO;
using System.Text.Json;
using TrackBoxStudio.Models;

namespace TrackBoxStudio.Services;

public sealed class WatermarkRegistryService
{
    private readonly string _dataDirectory;
    private readonly string _registryPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public WatermarkRegistryService()
    {
        _dataDirectory = Path.Combine(AppContext.BaseDirectory, "Data");
        _registryPath = Path.Combine(_dataDirectory, "watermark-registry.json");
    }

    public string RegistryPath => _registryPath;

    public async Task<IReadOnlyList<WatermarkDefinition>> LoadAsync()
    {
        Directory.CreateDirectory(_dataDirectory);
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
        Directory.CreateDirectory(_dataDirectory);
        await using var stream = File.Create(_registryPath);
        await JsonSerializer.SerializeAsync(stream, definitions.OrderBy(item => item.Name), _jsonOptions);
    }
}
