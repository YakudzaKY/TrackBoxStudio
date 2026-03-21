namespace TrackBoxStudio.Models;

public sealed class BoxRect
{
    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int Right => X + Width;

    public int Bottom => Y + Height;

    public BoxRect Clone()
    {
        return new BoxRect
        {
            X = X,
            Y = Y,
            Width = Width,
            Height = Height,
        };
    }

    public override string ToString()
    {
        return $"X:{X} Y:{Y} W:{Width} H:{Height}";
    }
}
