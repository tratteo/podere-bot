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
    public void RelativeRangesCoverWholeCalendarDaysEndingToday(string range, int days)
    {
        var now = new DateTime(2026, 11, 2, 15, 30, 0);
        var (from, to) = TemperatureHistory.Resolve(range, null, null, now);

        Assert.Equal(TimeSpan.Zero, from.TimeOfDay);
        Assert.Equal(now, to);
        Assert.Equal(days, (to.Date - from.Date).Days + 1);
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

        Assert.Equal(TimeSpan.Zero, from.TimeOfDay);
        Assert.Equal(now.AddDays(-1), to);
        Assert.Equal(39, (to.Date - from.Date).Days);
    }

    [Fact]
    public void CustomHourlyRangeStartsOnTheContainingHour()
    {
        var now = new DateTime(2026, 11, 2, 15, 30, 0);
        var (from, to) = TemperatureHistory.Resolve("custom", now.AddDays(-2).AddMinutes(-25), now, now);

        Assert.Equal(new DateTime(2026, 10, 31, 15, 0, 0), from);
        Assert.Equal(now, to);
    }

    [Fact]
    public void BuildUsesHourlyBucketsUpToAWeek()
    {
        var rows = new[]
        {
            new TemperatureBucket(ToUnix(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc)), 20, 19, 21, 12),
        };
        var from = new DateTime(2026, 3, 4, 0, 0, 0);
        var to = from.AddHours(23).AddMinutes(59);

        var result = TemperatureHistory.Build(rows, "today", from, to, Utc);

        Assert.Equal("hour", result.Bucket);
        Assert.Equal(24, result.Points.Count);
        var point = Assert.Single(result.Points, p => p.Samples > 0);
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
        Assert.Equal(11, result.Points.Count);
        Assert.Equal(2, result.Points.Count(p => p.Samples > 0));

        var first = result.Points.First(p => p.Samples > 0);
        Assert.Equal(17.5, first.Avg, 3);
        Assert.Equal(8, first.Min, 3);
        Assert.Equal(22, first.Max, 3);
        Assert.Equal(4, first.Samples);
        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0), first.At);
    }

    [Fact]
    public void BuildKeepsEveryDayOfTheRangeSoGapsStayVisible()
    {
        var rows = new[]
        {
            new TemperatureBucket(ToUnix(new DateTime(2026, 3, 2, 9, 0, 0, DateTimeKind.Utc)), 10, 9, 11, 4),
            new TemperatureBucket(ToUnix(new DateTime(2026, 3, 8, 9, 0, 0, DateTimeKind.Utc)), 12, 11, 13, 4),
        };
        var from = new DateTime(2026, 3, 1, 0, 0, 0);
        var to = from.AddDays(10);

        var result = TemperatureHistory.Build(rows, "30d", from, to, Utc);

        Assert.Equal(11, result.Points.Count);
        Assert.Equal(new DateTime(2026, 3, 1, 0, 0, 0), result.Points[0].At);
        Assert.Equal(new DateTime(2026, 3, 11, 0, 0, 0), result.Points[^1].At);
        Assert.Equal(0, result.Points[0].Samples);
        Assert.True(result.Points[1].Samples > 0);
        Assert.Equal(0, result.Points[2].Samples);
        Assert.Equal(0, result.Points[6].Samples);
        Assert.True(result.Points[7].Samples > 0);
    }

    [Fact]
    public void BoilerBucketsAreFilledWithZeroAcrossTheRange()
    {
        var at = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);
        var boiler = new[] { new BoilerBucket(ToUnix(at), 1800) };
        var from = new DateTime(2026, 3, 1, 0, 0, 0);
        var to = from.AddDays(10);

        var result = TemperatureHistory.Build([], [], boiler, "30d", from, to, Utc);

        Assert.Equal(11, result.Boiler.Count);
        Assert.Equal(1800, result.Boiler[3].Seconds, 3);
        Assert.All(result.Boiler.Where((_, i) => i != 3), b => Assert.Equal(0, b.Seconds));
    }

    [Fact]
    public void BuildReturnsOneSeriesPerSensorWithTheLocalProbeFirst()
    {
        var at = new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc);
        var rows = new[] { new TemperatureBucket(ToUnix(at), 20, 19, 21, 12) };
        var sensors = new[]
        {
            new SensorBucket("aa", "Camera", ToUnix(at), 12, 12, 12, 12),
            new SensorBucket("local", "Locale", ToUnix(at), 20, 20, 20, 12),
        };
        var from = new DateTime(2026, 3, 4, 0, 0, 0);
        var to = from.AddDays(1);

        var result = TemperatureHistory.Build(rows, sensors, "today", from, to, Utc);

        Assert.Equal(2, result.Series.Count);
        Assert.Equal("local", result.Series[0].Id);
        Assert.Equal("Camera", result.Series[1].Label);
        var camera = Assert.Single(result.Series[1].Points);
        Assert.Equal(12, camera.Value, 3);
        Assert.Equal(10, camera.At.Hour);
    }

    [Fact]
    public void SensorSeriesAggregateByDayWithSamplesAsWeights()
    {
        var day = new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc);
        var sensors = new[]
        {
            new SensorBucket("aa", "Camera", ToUnix(day.AddHours(8)), 10, 10, 10, 1),
            new SensorBucket("aa", "Camera", ToUnix(day.AddHours(9)), 20, 20, 20, 3),
        };
        var from = new DateTime(2026, 3, 1, 0, 0, 0);
        var to = from.AddDays(10);

        var result = TemperatureHistory.Build([], sensors, "30d", from, to, Utc);

        var series = Assert.Single(result.Series);
        var point = Assert.Single(series.Points);
        Assert.Equal(17.5, point.Value, 3);
        Assert.Equal(new DateTime(2026, 3, 4, 0, 0, 0), point.At);
    }

    private static long ToUnix(DateTime utc) => new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds();
}
