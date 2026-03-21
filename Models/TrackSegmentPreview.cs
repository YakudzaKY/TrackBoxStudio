using System.Windows.Media;

namespace TrackBoxStudio.Models;

public sealed class TrackSegmentPreview
{
    public required int StartFrame { get; init; }

    public required int EndFrame { get; init; }

    public required bool Enabled { get; init; }

    public required string Label { get; init; }

    public required Brush Fill { get; init; }

    public required Brush Border { get; init; }
}
