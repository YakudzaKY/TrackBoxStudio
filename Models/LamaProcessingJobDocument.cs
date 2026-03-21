namespace TrackBoxStudio.Models;

public sealed class LamaProcessingJobDocument
{
    public string InputPath { get; set; } = string.Empty;

    public string OutputPath { get; set; } = string.Empty;

    public string DevicePreference { get; set; } = "cuda-preferred";

    public int MaskPadding { get; set; }

    public List<LamaProcessingTrackDocument> Tracks { get; set; } = [];
}

public sealed class LamaProcessingTrackDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public List<LamaProcessingKeyframeDocument> Keyframes { get; set; } = [];
}

public sealed class LamaProcessingKeyframeDocument
{
    public int Frame { get; set; }

    public bool Enabled { get; set; }

    public BoxRect? Box { get; set; }
}
