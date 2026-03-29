using TrackBoxStudio.Models;
using Xunit;

namespace TrackBoxStudio.Tests;

public class BoxKeyframeTests
{
    [Fact]
    public void Clone_WithNullBox_ReturnsCorrectCopy()
    {
        // Arrange
        var original = new BoxKeyframe
        {
            Frame = 10,
            Enabled = true,
            Box = null
        };

        // Act
        var clone = original.Clone();

        // Assert
        Assert.NotSame(original, clone);
        Assert.Equal(original.Frame, clone.Frame);
        Assert.Equal(original.Enabled, clone.Enabled);
        Assert.Null(clone.Box);
    }

    [Fact]
    public void Clone_WithNonNullBox_ReturnsDeepCopy()
    {
        // Arrange
        var originalBox = new BoxRect { X = 1, Y = 2, Width = 3, Height = 4 };
        var original = new BoxKeyframe
        {
            Frame = 20,
            Enabled = false,
            Box = originalBox
        };

        // Act
        var clone = original.Clone();

        // Assert
        Assert.NotSame(original, clone);
        Assert.Equal(original.Frame, clone.Frame);
        Assert.Equal(original.Enabled, clone.Enabled);

        Assert.NotNull(clone.Box);
        Assert.NotSame(original.Box, clone.Box);
        Assert.Equal(original.Box.X, clone.Box.X);
        Assert.Equal(original.Box.Y, clone.Box.Y);
        Assert.Equal(original.Box.Width, clone.Box.Width);
        Assert.Equal(original.Box.Height, clone.Box.Height);
    }
}
