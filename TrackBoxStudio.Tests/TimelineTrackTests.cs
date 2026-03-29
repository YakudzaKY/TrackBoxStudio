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

    [Fact]
    public void UpsertKeyframe_NewKeyframe_AddsToCollection()
    {
        // Arrange
        var track = new TimelineTrack();
        var keyframe = new BoxKeyframe { Frame = 10, Enabled = true, Box = new BoxRect { X = 1, Y = 2, Width = 3, Height = 4 } };

        // Act
        track.UpsertKeyframe(keyframe);

        // Assert
        Assert.Single(track.Keyframes);
        Assert.Same(keyframe, track.Keyframes[0]);
    }

    [Fact]
    public void UpsertKeyframe_ExistingKeyframe_UpdatesProperties()
    {
        // Arrange
        var track = new TimelineTrack();
        var originalKeyframe = new BoxKeyframe { Frame = 10, Enabled = true, Box = new BoxRect { X = 1, Y = 2, Width = 3, Height = 4 } };
        track.UpsertKeyframe(originalKeyframe);

        var updatedKeyframe = new BoxKeyframe { Frame = 10, Enabled = false, Box = new BoxRect { X = 5, Y = 6, Width = 7, Height = 8 } };

        // Act
        track.UpsertKeyframe(updatedKeyframe);

        // Assert
        Assert.Single(track.Keyframes);
        var actual = track.Keyframes[0];
        Assert.Same(originalKeyframe, actual); // Should update existing, not replace
        Assert.False(actual.Enabled);
        Assert.NotNull(actual.Box);
        Assert.Equal(5, actual.Box.X);
        Assert.Equal(6, actual.Box.Y);
        Assert.Equal(7, actual.Box.Width);
        Assert.Equal(8, actual.Box.Height);
    }

    [Fact]
    public void UpsertKeyframe_ExistingKeyframeWithNullBox_UpdatesProperties()
    {
        // Arrange
        var track = new TimelineTrack();
        var originalKeyframe = new BoxKeyframe { Frame = 10, Enabled = true, Box = new BoxRect { X = 1, Y = 2, Width = 3, Height = 4 } };
        track.UpsertKeyframe(originalKeyframe);

        var updatedKeyframe = new BoxKeyframe { Frame = 10, Enabled = true, Box = null };

        // Act
        track.UpsertKeyframe(updatedKeyframe);

        // Assert
        Assert.Single(track.Keyframes);
        var actual = track.Keyframes[0];
        Assert.Same(originalKeyframe, actual);
        Assert.True(actual.Enabled);
        Assert.Null(actual.Box);
    }

    [Fact]
    public void UpsertKeyframe_MultipleKeyframes_SortsByFrame()
    {
        // Arrange
        var track = new TimelineTrack();
        var kf1 = new BoxKeyframe { Frame = 20 };
        var kf2 = new BoxKeyframe { Frame = 10 };
        var kf3 = new BoxKeyframe { Frame = 30 };

        // Act
        track.UpsertKeyframe(kf1);
        track.UpsertKeyframe(kf2);
        track.UpsertKeyframe(kf3);

        // Assert
        Assert.Equal(3, track.Keyframes.Count);
        Assert.Equal(10, track.Keyframes[0].Frame);
        Assert.Equal(20, track.Keyframes[1].Frame);
        Assert.Equal(30, track.Keyframes[2].Frame);
    }
}
