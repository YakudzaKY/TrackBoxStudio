using TrackBoxStudio.Services;
using Xunit;

namespace TrackBoxStudio.Tests;

public class InpaintProcessingServiceTests
{
    [Fact]
    public void AppendIfValidated_ShouldAddRootedPythonPath()
    {
        // Arrange
        var candidates = new List<string>();
        var path = OperatingSystem.IsWindows() ? @"C:\Python39\python.exe" : "/usr/bin/python3";

        // Act
        InpaintProcessingService.AppendIfValidated(candidates, path);

        // Assert
        Assert.Single(candidates);
        Assert.Equal(path, candidates[0]);
    }

    [Fact]
    public void AppendIfValidated_ShouldRejectNonRootedPath()
    {
        // Arrange
        var candidates = new List<string>();
        var path = "python.exe";

        // Act
        InpaintProcessingService.AppendIfValidated(candidates, path);

        // Assert
        Assert.Empty(candidates);
    }

    [Fact]
    public void AppendIfValidated_ShouldRejectNonPythonFilename()
    {
        // Arrange
        var candidates = new List<string>();
        var path = OperatingSystem.IsWindows() ? @"C:\Windows\System32\cmd.exe" : "/bin/bash";

        // Act
        InpaintProcessingService.AppendIfValidated(candidates, path);

        // Assert
        Assert.Empty(candidates);
    }

    [Fact]
    public void AppendIfValidated_ShouldHandleNullOrEmpty()
    {
        // Arrange
        var candidates = new List<string>();

        // Act
        InpaintProcessingService.AppendIfValidated(candidates, null);
        InpaintProcessingService.AppendIfValidated(candidates, "");
        InpaintProcessingService.AppendIfValidated(candidates, "   ");

        // Assert
        Assert.Empty(candidates);
    }
}
