using TrackBoxStudio.Infrastructure;

namespace TrackBoxStudio.Models;

public sealed class InpaintCoverageSettingEntry : BindableBase
{
    private string _valueText;

    public InpaintCoverageSettingEntry(
        string key,
        string groupName,
        string displayName,
        string description,
        bool isInteger,
        string defaultValueText,
        string valueText)
    {
        Key = key;
        GroupName = groupName;
        DisplayName = displayName;
        Description = description;
        IsInteger = isInteger;
        DefaultValueText = defaultValueText;
        _valueText = valueText;
    }

    public string Key { get; }

    public string GroupName { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public bool IsInteger { get; }

    public string DefaultValueText { get; }

    public string ValueText
    {
        get => _valueText;
        set => SetProperty(ref _valueText, value);
    }
}
