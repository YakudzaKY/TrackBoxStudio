using System.Globalization;
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

    public bool CanResetToDefault => !RepresentsDefaultValue(_valueText);

    public string ValueText
    {
        get => _valueText;
        set
        {
            if (!SetProperty(ref _valueText, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanResetToDefault));
        }
    }

    public void ResetToDefault()
    {
        ValueText = DefaultValueText;
    }

    private bool RepresentsDefaultValue(string? rawValue)
    {
        var currentValue = (rawValue ?? string.Empty).Trim();
        var defaultValue = DefaultValueText.Trim();

        if (IsInteger &&
            int.TryParse(currentValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var currentInt) &&
            int.TryParse(defaultValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var defaultInt))
        {
            return currentInt == defaultInt;
        }

        if (!IsInteger &&
            double.TryParse(currentValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var currentDouble) &&
            double.TryParse(defaultValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var defaultDouble))
        {
            return Math.Abs(currentDouble - defaultDouble) < 0.000_001d;
        }

        return string.Equals(currentValue, defaultValue, StringComparison.Ordinal);
    }
}
