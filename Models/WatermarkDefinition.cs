using TrackBoxStudio.Infrastructure;

namespace TrackBoxStudio.Models;

public sealed class WatermarkDefinition : BindableBase
{
    private string _id = Guid.NewGuid().ToString("N");
    private string _name = string.Empty;

    public string Id
    {
        get => _id;
        set => SetProperty(ref _id, value);
    }

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    public override string ToString()
    {
        return Name;
    }
}
