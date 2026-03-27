using System.Text.Json.Serialization;

namespace TrackBoxStudio.Models;

public sealed class TrackBoxProjectDocument
{
    public int ProjectVersion { get; set; } = 1;

    public string ApplicationName { get; set; } = "TrackBoxStudio";

    public ProjectMediaState Media { get; set; } = new();

    public ProjectTimelineState Timeline { get; set; } = new();

    public ProjectAnnotationState Annotation { get; set; } = new();

    public ProjectLearningState Learning { get; set; } = new();
}

public sealed class ProjectMediaState
{
    public string? InputPath { get; set; }

    public string? InputPathRelative { get; set; }

    public string? OutputPath { get; set; }

    public string? OutputPathRelative { get; set; }

    public bool IsVideo { get; set; }

    public int TotalFrames { get; set; }

    public double FramesPerSecond { get; set; }

    public int FrameWidth { get; set; }

    public int FrameHeight { get; set; }

    public int CurrentFrameIndex { get; set; }

    [JsonIgnore]
    public string? ResolvedInputPath { get; set; }

    [JsonIgnore]
    public string? ResolvedOutputPath { get; set; }
}

public sealed class ProjectTimelineState
{
    public string? SelectedTrackId { get; set; }

    public List<ProjectTrackDocument> Tracks { get; set; } = [];
}

public sealed class ProjectTrackDocument
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatermarkId { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? WatermarkName { get; set; }

    public string ColorHex { get; set; } = "#4ADE80";

    public List<ProjectKeyframeDocument> Keyframes { get; set; } = [];
}

public sealed class ProjectKeyframeDocument
{
    public int Frame { get; set; }

    public bool Enabled { get; set; }

    public BoxRect? Box { get; set; }
}

public sealed class ProjectAnnotationState
{
    public string Mode { get; set; } = "manual-keyframes";

    public bool ContainsManualBoxes { get; set; }
}

public sealed class ProjectLearningState
{
    public bool TrainingAssistEnabled { get; set; }

    public string? PlannedBackend { get; set; }

    public Dictionary<string, string> Parameters { get; set; } = [];
}
