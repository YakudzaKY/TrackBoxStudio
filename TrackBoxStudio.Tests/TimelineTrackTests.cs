using TrackBoxStudio.Models;
using Xunit;

namespace TrackBoxStudio.Tests;

public class TimelineTrackTests
{
    [Fact]
    public void GetActiveKeyframe_EmptyTrack_ReturnsNull()
    {
        // Arrange
        var track = new TimelineTrack();

        // Act
        var result = track.GetActiveKeyframe(0);

        // Assert
        Assert.Null(result);
    }

    [Theory]
    [InlineData(5, false)]   // Before keyframe
    [InlineData(10, true)]   // At keyframe
    [InlineData(15, true)]   // After keyframe
    public void GetActiveKeyframe_SingleKeyframe_ReturnsCorrectResult(int queryFrame, bool shouldReturnKeyframe)
    {
        // Arrange
        var track = new TimelineTrack();
        var keyframe = new BoxKeyframe { Frame = 10 };
        track.UpsertKeyframe(keyframe);

        // Act
        var result = track.GetActiveKeyframe(queryFrame);

        // Assert
        if (shouldReturnKeyframe)
        {
            Assert.Same(keyframe, result);
        }
        else
        {
            Assert.Null(result);
        }
    }

    [Theory]
    [InlineData(5, null)]    // Before all
    [InlineData(10, 10)]     // Exactly at first
    [InlineData(15, 10)]     // Between first and second
    [InlineData(20, 20)]     // Exactly at second
    [InlineData(25, 20)]     // After all
    public void GetActiveKeyframe_MultipleKeyframes_ReturnsCorrectResult(int queryFrame, int? expectedKeyframeFrame)
    {
        // Arrange
        var track = new TimelineTrack();
        var keyframe1 = new BoxKeyframe { Frame = 10 };
        var keyframe2 = new BoxKeyframe { Frame = 20 };
        track.UpsertKeyframe(keyframe1);
        track.UpsertKeyframe(keyframe2);

        // Act
        var result = track.GetActiveKeyframe(queryFrame);

        // Assert
        if (expectedKeyframeFrame.HasValue)
        {
            Assert.NotNull(result);
            Assert.Equal(expectedKeyframeFrame.Value, result.Frame);
        }
        else
        {
            Assert.Null(result);
        }
    }
}
