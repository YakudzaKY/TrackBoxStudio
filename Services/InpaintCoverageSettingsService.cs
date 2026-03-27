using System.Globalization;
using System.IO;
using System.Text.Json;
using TrackBoxStudio.Models;

namespace TrackBoxStudio.Services;

public sealed class InpaintCoverageSettingsService
{
    private const string PreserveAudioOnExportKey = "preserve_audio_on_export";
    private const bool DefaultPreserveAudioOnExport = true;

    private sealed record SettingDefinition(
        string Key,
        string GroupName,
        string DisplayName,
        string Description,
        object DefaultValue,
        bool IsInteger);

    private static readonly SettingDefinition[] Definitions =
    [
        new("mask_min_whiteness", "Stable Mask", "Min Whiteness", "Lower = dimmer white-ish watermark pixels can enter the stable mask.", 0.43, false),
        new("mask_min_luminance", "Stable Mask", "Min Luminance", "Lower = darker but still bright pixels can enter the stable mask.", 0.50, false),
        new("stable_frame_delta_threshold", "Stable Mask", "Delta Threshold", "Frames above this average luminance delta are treated as outliers for mask building.", 0.12, false),
        new("stable_frame_keep_ratio", "Stable Mask", "Keep Ratio", "If too few frames survive the delta threshold, keep at least this fraction of the calmest ones.", 0.45, false),
        new("stable_mask_presence_ratio", "Stable Mask", "Presence Ratio", "Lower = a pixel only needs to appear in fewer stable frames to make the final segment mask.", 0.35, false),
        new("mask_close_radius", "Stable Mask", "Join Radius", "Morphological close radius to connect tiny gaps inside the stable mask.", 2, true),
        new("mask_expand_radius", "Stable Mask", "Expand Pixels", "Expand the final stable mask by this many pixels before inpaint.", 6, true),
        new("mask_min_component_area", "Stable Mask", "Min Component Area", "Remove tiny islands smaller than this many pixels.", 24, true),
        new("temporal_blend_enabled", "Temporal Blend", "Enable Blend", "1 = blend each segment forward from start using frame (start-1) as anchor; 0 = disable.", 1, true),
        new("temporal_blend_edge_strength", "Temporal Blend", "Start Strength", "Blend weight at the segment start (0..1). Higher values smooth flicker but can pull more source texture.", 0.26, false),
        new("temporal_blend_falloff_power", "Temporal Blend", "Decay Power", "How fast forward blend decays from segment start to end. Higher values keep later frames cleaner.", 1.35, false),
    ];

    private readonly string _dataDirectory;
    private readonly string _settingsPath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
    };

    public InpaintCoverageSettingsService(string? baseDirectory = null)
    {
        var resolvedBaseDirectory = string.IsNullOrWhiteSpace(baseDirectory)
            ? AppContext.BaseDirectory
            : baseDirectory;

        _dataDirectory = Path.Combine(resolvedBaseDirectory, "Data");
        _settingsPath = Path.Combine(_dataDirectory, "lama-coverage-config.json");
    }

    public string SettingsPath => _settingsPath;

    public void EnsureSettingsFileExists()
    {
        Directory.CreateDirectory(_dataDirectory);
        if (File.Exists(_settingsPath))
        {
            return;
        }

        using var stream = File.Create(_settingsPath);
        JsonSerializer.Serialize(stream, BuildDefaultPayload(), _jsonOptions);
    }

    public async Task<IReadOnlyList<InpaintCoverageSettingEntry>> LoadEntriesAsync()
    {
        var storedValues = await LoadStoredValuesAsync();

        var entries = new List<InpaintCoverageSettingEntry>(Definitions.Length);
        foreach (var definition in Definitions)
        {
            var valueText = TryReadStoredValueText(definition, storedValues) ?? FormatValue(definition.DefaultValue, definition.IsInteger);
            entries.Add(new InpaintCoverageSettingEntry(
                definition.Key,
                definition.GroupName,
                definition.DisplayName,
                definition.Description,
                definition.IsInteger,
                FormatValue(definition.DefaultValue, definition.IsInteger),
                valueText));
        }

        return entries;
    }

    public async Task SaveEntriesAsync(IEnumerable<InpaintCoverageSettingEntry> entries)
    {
        var byKey = entries.ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var payload = BuildPayload(await LoadStoredValuesAsync());

        foreach (var definition in Definitions)
        {
            if (!byKey.TryGetValue(definition.Key, out var entry))
            {
                payload[definition.Key] = definition.DefaultValue;
                continue;
            }

            payload[definition.Key] = ParseValue(entry.ValueText, definition);
        }

        await SavePayloadAsync(payload);
    }

    public async Task<bool> LoadPreserveAudioOnExportAsync()
    {
        var storedValues = await LoadStoredValuesAsync();
        return TryReadStoredBoolean(storedValues, PreserveAudioOnExportKey) ?? DefaultPreserveAudioOnExport;
    }

    public async Task SavePreserveAudioOnExportAsync(bool preserveAudioOnExport)
    {
        var payload = BuildPayload(await LoadStoredValuesAsync());
        payload[PreserveAudioOnExportKey] = preserveAudioOnExport;
        await SavePayloadAsync(payload);
    }

    private static string? TryReadStoredValueText(
        SettingDefinition definition,
        IReadOnlyDictionary<string, JsonElement>? storedValues)
    {
        if (storedValues is null || !storedValues.TryGetValue(definition.Key, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when definition.IsInteger => element.TryGetInt32(out var intValue)
                ? intValue.ToString(CultureInfo.InvariantCulture)
                : null,
            JsonValueKind.Number => element.TryGetDouble(out var doubleValue)
                ? doubleValue.ToString("0.###", CultureInfo.InvariantCulture)
                : null,
            JsonValueKind.String => element.GetString(),
            _ => null,
        };
    }

    private static object ParseValue(string rawValue, SettingDefinition definition)
    {
        var trimmed = rawValue.Trim();
        if (definition.IsInteger)
        {
            if (!int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                throw new FormatException($"'{definition.DisplayName}' expects an integer value.");
            }

            return intValue;
        }

        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue))
        {
            throw new FormatException($"'{definition.DisplayName}' expects a decimal value.");
        }

        return doubleValue;
    }

    private static string FormatValue(object value, bool isInteger)
    {
        return value switch
        {
            int intValue => intValue.ToString(CultureInfo.InvariantCulture),
            float floatValue when !isInteger => floatValue.ToString("0.###", CultureInfo.InvariantCulture),
            double doubleValue when !isInteger => doubleValue.ToString("0.###", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
        };
    }

    private static Dictionary<string, object> BuildDefaultPayload()
    {
        var payload = new Dictionary<string, object>(Definitions.Length + 1, StringComparer.Ordinal);
        foreach (var definition in Definitions)
        {
            payload[definition.Key] = definition.DefaultValue;
        }

        payload[PreserveAudioOnExportKey] = DefaultPreserveAudioOnExport;
        return payload;
    }

    private async Task<Dictionary<string, JsonElement>?> LoadStoredValuesAsync()
    {
        EnsureSettingsFileExists();

        await using var stream = File.OpenRead(_settingsPath);
        return await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(stream, _jsonOptions);
    }

    private async Task SavePayloadAsync(Dictionary<string, object> payload)
    {
        Directory.CreateDirectory(_dataDirectory);
        await using var stream = File.Create(_settingsPath);
        await JsonSerializer.SerializeAsync(stream, payload, _jsonOptions);
    }

    private static Dictionary<string, object> BuildPayload(IReadOnlyDictionary<string, JsonElement>? storedValues)
    {
        var payload = new Dictionary<string, object>(Definitions.Length + 1, StringComparer.Ordinal);
        foreach (var definition in Definitions)
        {
            payload[definition.Key] = TryReadStoredValue(definition, storedValues) ?? definition.DefaultValue;
        }

        payload[PreserveAudioOnExportKey] = TryReadStoredBoolean(storedValues, PreserveAudioOnExportKey) ?? DefaultPreserveAudioOnExport;
        return payload;
    }

    private static object? TryReadStoredValue(
        SettingDefinition definition,
        IReadOnlyDictionary<string, JsonElement>? storedValues)
    {
        var valueText = TryReadStoredValueText(definition, storedValues);
        if (string.IsNullOrWhiteSpace(valueText))
        {
            return null;
        }

        try
        {
            return ParseValue(valueText, definition);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool? TryReadStoredBoolean(
        IReadOnlyDictionary<string, JsonElement>? storedValues,
        string key)
    {
        if (storedValues is null || !storedValues.TryGetValue(key, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetInt32(out var intValue) => intValue != 0,
            JsonValueKind.String when bool.TryParse(element.GetString(), out var boolValue) => boolValue,
            JsonValueKind.String when int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numericValue) => numericValue != 0,
            _ => null,
        };
    }
}
