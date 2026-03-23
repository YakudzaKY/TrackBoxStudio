using System.Collections.ObjectModel;
using System.Windows.Media;
using TrackBoxStudio.Infrastructure;

namespace TrackBoxStudio.Models;

public sealed class TimelineTrack : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;
    private string? _watermarkId;
    private string _watermarkName = "Unassigned";
    private string _colorHex = "#4ADE80";

    public TimelineTrack()
    {
        Keyframes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Keyframes));
    }

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string? WatermarkId
    {
        get => _watermarkId;
        set => SetProperty(ref _watermarkId, value);
    }

    public string WatermarkName
    {
        get => _watermarkName;
        set
        {
            if (SetProperty(ref _watermarkName, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string ColorHex
    {
        get => _colorHex;
        set
        {
            if (SetProperty(ref _colorHex, value))
            {
                OnPropertyChanged(nameof(TrackBrush));
            }
        }
    }

    public ObservableCollection<BoxKeyframe> Keyframes { get; } = [];

    public ObservableCollection<TrackSegmentPreview> Segments { get; } = [];

    public string DisplayName => $"{Name} - {WatermarkName}";

    public Brush TrackBrush => (SolidColorBrush)new BrushConverter().ConvertFromString(ColorHex)!;

    public IEnumerable<BoxKeyframe> OrderedKeyframes()
    {
        return Keyframes;
    }

    public BoxKeyframe? GetActiveKeyframe(int frameIndex)
    {
        BoxKeyframe? active = null;
        foreach (var keyframe in OrderedKeyframes())
        {
            if (keyframe.Frame <= frameIndex)
            {
                active = keyframe;
                continue;
            }

            break;
        }

        return active;
    }

    public void UpsertKeyframe(BoxKeyframe keyframe)
    {
        var existing = Keyframes.FirstOrDefault(item => item.Frame == keyframe.Frame);
        if (existing is null)
        {
            Keyframes.Add(keyframe);
            SortKeyframes();
            return;
        }

        existing.Enabled = keyframe.Enabled;
        existing.Box = keyframe.Box?.Clone();
        SortKeyframes();
    }

    public void RemoveKeyframe(int frameIndex)
    {
        var existing = Keyframes.FirstOrDefault(item => item.Frame == frameIndex);
        if (existing is not null)
        {
            Keyframes.Remove(existing);
            SortKeyframes();
        }
    }

    public void RebuildSegments(int totalFrames)
    {
        Segments.Clear();
        if (totalFrames <= 0)
        {
            return;
        }

        var ordered = OrderedKeyframes().ToList();
        if (ordered.Count == 0)
        {
            Segments.Add(new TrackSegmentPreview
            {
                StartFrame = 0,
                EndFrame = totalFrames - 1,
                Enabled = false,
                Label = "Off",
                Fill = new SolidColorBrush(Color.FromArgb(70, 190, 24, 93)),
                Border = new SolidColorBrush(Color.FromArgb(255, 244, 63, 94)),
            });
            return;
        }

        var segmentStarts = new List<(int StartFrame, BoxKeyframe? Keyframe)>
        {
            (0, null),
        };
        segmentStarts.AddRange(ordered.Select(keyframe => (keyframe.Frame, (BoxKeyframe?)keyframe)));

        var compact = segmentStarts
            .OrderBy(item => item.StartFrame)
            .GroupBy(item => item.StartFrame)
            .Select(group => group.Last())
            .ToList();

        BoxKeyframe? active = null;
        for (var index = 0; index < compact.Count; index++)
        {
            var item = compact[index];
            if (item.Keyframe is not null)
            {
                active = item.Keyframe;
            }

            var nextStart = index < compact.Count - 1 ? compact[index + 1].StartFrame : totalFrames;
            var endFrame = Math.Max(item.StartFrame, Math.Min(totalFrames - 1, nextStart - 1));
            var isEnabled = active?.Enabled == true && active.Box is not null;
            var label = isEnabled
                ? $"{item.StartFrame}-{endFrame} On"
                : $"{item.StartFrame}-{endFrame} Off";

            Segments.Add(new TrackSegmentPreview
            {
                StartFrame = item.StartFrame,
                EndFrame = endFrame,
                Enabled = isEnabled,
                Label = label,
                Fill = isEnabled
                    ? new SolidColorBrush(Color.FromArgb(85, 24, 196, 93))
                    : new SolidColorBrush(Color.FromArgb(70, 190, 24, 93)),
                Border = isEnabled
                    ? new SolidColorBrush(Color.FromArgb(255, 74, 222, 128))
                    : new SolidColorBrush(Color.FromArgb(255, 244, 63, 94)),
            });
        }
    }

    private void SortKeyframes()
    {
        var ordered = Keyframes.OrderBy(item => item.Frame).ToList();
        Keyframes.Clear();
        foreach (var keyframe in ordered)
        {
            Keyframes.Add(keyframe);
        }
    }
}
