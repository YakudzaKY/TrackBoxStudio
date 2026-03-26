using System.IO;
using System.Text.Json;
using TrackBoxStudio.Models;
using TrackBoxStudio.Services;

namespace TrackBoxStudio.Tests;

public sealed class WatermarkRegistryServiceTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(
        Path.GetTempPath(),
        $"trackbox-registry-tests-{Guid.NewGuid():N}");

    private string RegistryDirectory => Path.Combine(_rootDirectory, "config");

    private string LegacyDataDirectory => Path.Combine(_rootDirectory, "legacy", "Data");

    [Fact]
    public async Task SaveAsync_WritesRegistryToConfiguredGlobalDirectory()
    {
        var service = new WatermarkRegistryService(RegistryDirectory, LegacyDataDirectory);
        List<WatermarkDefinition> definitions =
        [
            new WatermarkDefinition { Id = "b", Name = "Beta" },
            new WatermarkDefinition { Id = "a", Name = "Alpha" },
        ];

        await service.SaveAsync(definitions);

        Assert.Equal(Path.Combine(RegistryDirectory, "watermark-registry.json"), service.RegistryPath);
        Assert.True(File.Exists(service.RegistryPath));
        Assert.False(File.Exists(Path.Combine(LegacyDataDirectory, "watermark-registry.json")));

        var payload = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(File.ReadAllText(service.RegistryPath));
        Assert.NotNull(payload);
        Assert.Collection(
            payload,
            item => Assert.Equal("Alpha", item["name"].GetString()),
            item => Assert.Equal("Beta", item["name"].GetString()));
    }

    [Fact]
    public async Task LoadAsync_MigratesLegacyRegistryWhenGlobalFileIsMissing()
    {
        Directory.CreateDirectory(LegacyDataDirectory);
        var legacyPath = Path.Combine(LegacyDataDirectory, "watermark-registry.json");
        await File.WriteAllTextAsync(
            legacyPath,
            """
            [
              {
                "id": "legacy-1",
                "name": "Corner Bug"
              }
            ]
            """);

        var service = new WatermarkRegistryService(RegistryDirectory, LegacyDataDirectory);

        var items = await service.LoadAsync();

        Assert.Single(items);
        Assert.Equal("Corner Bug", items[0].Name);
        Assert.True(File.Exists(service.RegistryPath));

        var migratedPayload = JsonSerializer.Deserialize<List<Dictionary<string, JsonElement>>>(File.ReadAllText(service.RegistryPath));
        Assert.NotNull(migratedPayload);
        Assert.Single(migratedPayload);
        Assert.Equal("legacy-1", migratedPayload[0]["id"].GetString());
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }
}
