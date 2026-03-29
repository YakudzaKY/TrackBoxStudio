using System;
using System.Diagnostics;
using TrackBoxStudio.Models;
using Xunit;
using Xunit.Abstractions;

namespace TrackBoxStudio.Tests;

public class TimelineTrackBenchmark
{
    private readonly ITestOutputHelper _output;

    public TimelineTrackBenchmark(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void BenchmarkTrackBrushGetter()
    {
        var track = new TimelineTrack();

        // Warm up
        var warmup = track.TrackBrush;

        var stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < 1_000_000; i++)
        {
            var brush = track.TrackBrush;
        }
        stopwatch.Stop();

        _output.WriteLine($"TrackBrush 1M accesses took: {stopwatch.ElapsedMilliseconds} ms");
    }
}
