using TrackBoxStudio.Infrastructure;

namespace TrackBoxStudio.Models;

public sealed class BoxKeyframe : BindableBase
{
    private int _frame;
    private bool _enabled;
    private BoxRect? _box;

    public int Frame
    {
        get => _frame;
        set
        {
            if (SetProperty(ref _frame, value))
            {
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(BoxLabel));
            }
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(StateLabel));
            }
        }
    }

    public BoxRect? Box
    {
        get => _box;
        set
        {
            if (SetProperty(ref _box, value))
            {
                OnPropertyChanged(nameof(BoxLabel));
            }
        }
    }

    public string StateLabel => Enabled ? "On" : "Off";

    public string BoxLabel => Box is null ? "No box" : Box.ToString();

    public BoxKeyframe Clone()
    {
        return new BoxKeyframe
        {
            Frame = Frame,
            Enabled = Enabled,
            Box = Box?.Clone(),
        };
    }
}
