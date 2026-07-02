using Radirec.Core.Ingestion;
using Radirec.Core.Model;

namespace Radirec.Tests;

public sealed class StabilityCheckerTests
{
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 12, 22, 40, 0, TimeSpan.Zero);

    private static RecordingFile FileWrittenSecondsAgo(int seconds, long size = 1024)
        => new()
        {
            FilePath = @"I:\radio\tmp\局\番組\x.m4a",
            FileName = "x.m4a",
            StationFolder = "局",
            ProgramFolder = "番組",
            Extension = ".m4a",
            SizeBytes = size,
            LastWriteTimeUtc = Now.AddSeconds(-seconds),
        };

    [Fact]
    public void IsStable_WhenQuietLongerThanThreshold()
    {
        var sut = new StabilityChecker(new FixedClock(Now), TimeSpan.FromSeconds(15));

        Assert.True(sut.IsStable(FileWrittenSecondsAgo(20)));
    }

    [Fact]
    public void NotStable_WhenRecentlyModified()
    {
        var sut = new StabilityChecker(new FixedClock(Now), TimeSpan.FromSeconds(15));

        Assert.False(sut.IsStable(FileWrittenSecondsAgo(5)));
    }

    [Fact]
    public void NotStable_WhenSizeZero()
    {
        var sut = new StabilityChecker(new FixedClock(Now), TimeSpan.FromSeconds(15));

        Assert.False(sut.IsStable(FileWrittenSecondsAgo(120, size: 0)));
    }
}
