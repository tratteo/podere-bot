using PodereBot.Services.Panel;
using Xunit;

namespace PodereBot.Tests;

public sealed class PanelHistoryTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.CreateCustomTimeZone("test-utc", TimeSpan.Zero, "test", "test");

    [Fact]
    public void TodayStartsAtLocalMidnight()
    {
        var now = new DateTime(2026, 11, 2, 15, 30, 0);
        var (from, to) = TemperatureHistory.Resolve("today", null, null, now);

        Assert.Equal(new DateTime(2026, 11, 2, 0, 0, 0), from);
        Assert.Equal(now, to);
    }

    [Theory]
    [InlineData("7d", 7)]
    [InlineData("30d", 30)]
    [InlineData("1y", 365)]
    public void RelativeRangesUseDays(string range, int days)
    {
        var now = new DateTime(2026, 11, 2, 15, 30, 0);
        var (from, to) = TemperatureHistory.Resolve(range, null, null, now);

        Assert.Equal(days, (to - from).TotalDays, 3);
    }

    [Fact]
    public void CustomRangeMustEndAfterItStarts()
    {
        var now = new DateTime(2026, 11, 2, 15, 30, 0);
        Assert.Throws<PanelValidationException>(() =>
            TemperatureHistory.Resolve("custom", now, now.AddHours(-1), now)
        );
    }

    [Fact]
    public void CustomRangeCannotBeInTheFutureOrLongerThanAYear()
    {
        var now = new DateTime(2026, 11, 2, 15, 30, 0);

        Assert.Throws<PanelValidationException>(() =>
            TemperatureHistory.Resolve("custom", now.AddDays(-1), now.AddDays(1), now)
        );
        Assert.Throws<PanelValidationException>(() =>
            TemperatureHistory.Resolve("custom", now.AddDays(-400), now, now)
        );
    }

    [Fact]
    public void CustomRangeIsAcceptedWithinAYear()
    {
        var now = new DateTime(2026, 11, 2, 15, 30, 0);
        var (from, to) = TemperatureHistory.Resolve("custom", now.AddDays(-40), now.AddDays(-1), now);

        Assert.Equal(39, (to - from).TotalDays, 3);
    }

    [Fact]
    public void BuildUsesHourlyBucketsUpToAWeek()
    {
        var rows = new[]
        {
            new TemperatureBucket(ToUnix(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc)), 20, 19, 21, 12),
        };
        var from = new DateTime(2026, 3, 4, 0, 0, 0);
        var to = from.AddDays(1);

        var result = TemperatureHistory.Build(rows, "today", from, to, Utc);

        Assert.Equal("hour", result.Bucket);
        var point = Assert.Single(result.Points);
        Assert.Equal(20, point.Avg, 3);
        Assert.Equal(10, point.At.Hour);
    }

    [Fact]
    public void BuildAggregatesByDayWithAWeightedAverage()
    {
        var dayOne = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new TemperatureBucket(ToUnix(dayOne.AddHours(8)), 10, 8, 12, 1),
            new TemperatureBucket(ToUnix(dayOne.AddHours(9)), 20, 18, 22, 3),
            new TemperatureBucket(ToUnix(dayOne.AddDays(1).AddHours(9)), 15, 14, 16, 2),
        };
        var from = new DateTime(2026, 3, 1, 0, 0, 0);
        var to = from.AddDays(10);

        var result = TemperatureHistory.Build(rows, "30d", from, to, Utc);

        Assert.Equal("day", result.Bucket);
        Assert.Equal(2, result.Points.Count);

        var first = result.Points[0];
        Assert.Equal(17.5, first.Avg, 3);
        Assert.Equal(8, first.Min, 3);
        Assert.Equal(22, first.Max, 3);
        Assert.Equal(4, first.Samples);
        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0), first.At);
    }

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
