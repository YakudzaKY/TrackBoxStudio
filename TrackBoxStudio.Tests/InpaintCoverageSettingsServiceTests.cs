using System.IO;
using System.Text.Json;
using TrackBoxStudio.Services;

namespace TrackBoxStudio.Tests;

public sealed class InpaintCoverageSettingsServiceTests : IDisposable
{
    private readonly string _baseDirectory = Path.Combine(
        Path.GetTempPath(),
        $"trackbox-settings-tests-{Guid.NewGuid():N}");

    [Fact]
    public void EnsureSettingsFileExists_CreatesDefaultConfigWithCurrentBaseline()
    {
        var service = new InpaintCoverageSettingsService(_baseDirectory);

        service.EnsureSettingsFileExists();

        Assert.True(File.Exists(service.SettingsPath));

        var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(service.SettingsPath));
        Assert.NotNull(payload);
        Assert.Equal(0.43, payload["mask_min_whiteness"].GetDouble(), 3);
        Assert.Equal(6, payload["mask_expand_radius"].GetInt32());
        Assert.Equal(1, payload["temporal_blend_enabled"].GetInt32());
        Assert.Equal(0.26, payload["temporal_blend_edge_strength"].GetDouble(), 3);
        Assert.Equal(1.35, payload["temporal_blend_falloff_power"].GetDouble(), 3);
        Assert.True(payload["preserve_audio_on_export"].GetBoolean());
    }

    [Fact]
    public async Task LoadEntriesAsync_MissingConfig_UsesSameDefaults()
    {
        var service = new InpaintCoverageSettingsService(_baseDirectory);

        var entries = await service.LoadEntriesAsync();

        Assert.Contains(entries, entry => entry.Key == "mask_min_whiteness" && entry.ValueText == "0.43");
        Assert.Contains(entries, entry => entry.Key == "mask_expand_radius" && entry.ValueText == "6");
        Assert.Contains(entries, entry => entry.Key == "temporal_blend_enabled" && entry.ValueText == "1");
    }

    [Fact]
    public async Task LoadPreserveAudioOnExportAsync_MissingConfig_DefaultsToTrue()
    {
        var service = new InpaintCoverageSettingsService(_baseDirectory);

        var preserveAudio = await service.LoadPreserveAudioOnExportAsync();

        Assert.True(preserveAudio);
    }

    [Fact]
    public async Task SaveEntriesAsync_PreservesAudioTogglePreference()
    {
        var service = new InpaintCoverageSettingsService(_baseDirectory);

        await service.SavePreserveAudioOnExportAsync(false);
        var entries = await service.LoadEntriesAsync();
        await service.SaveEntriesAsync(entries);

        var preserveAudio = await service.LoadPreserveAudioOnExportAsync();
        Assert.False(preserveAudio);
    }

    public void Dispose()
    {
        if (Directory.Exists(_baseDirectory))
        {
            Directory.Delete(_baseDirectory, recursive: true);
        }
    }
}
