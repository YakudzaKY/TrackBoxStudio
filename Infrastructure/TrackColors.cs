namespace TrackBoxStudio.Infrastructure;

public static class TrackColors
{
    public static readonly string[] DefaultColors =
    [
        "#4ADE80",
        "#38BDF8",
        "#F59E0B",
        "#F472B6",
        "#A78BFA",
        "#FB7185",
    ];

    public static string GetColorForTrackIndex(int index)
    {
        return DefaultColors[index % DefaultColors.Length];
    }
}
