using System.Windows;
using System.Windows.Media;

namespace TrackBoxStudio.Models;

public sealed class OverlayBox
{
    public required double X { get; init; }

    public required double Y { get; init; }

    public required double Width { get; init; }

    public required double Height { get; init; }

    public required Brush Stroke { get; init; }

    public required Brush Fill { get; init; }

    public required double StrokeThickness { get; init; }

    public required string Label { get; init; }

    public required Brush LabelBackground { get; init; }

    public required Visibility LabelVisibility { get; init; }
}
