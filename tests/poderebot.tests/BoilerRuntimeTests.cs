using System;
using System.Linq;
using PodereBot.Services.Panel;
using Xunit;

namespace PodereBot.Tests;

public sealed class BoilerRuntimeTests
{
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.CreateCustomTimeZone(
        "test-utc",
        TimeSpan.Zero,
        "test",
        "test"
    );

    private static long Unix(DateTime utc) => new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeSeconds();

    [Fact]
    public void RuntimeIsSummedPerLocalDay()
    {
        var from = new DateTime(2026, 10, 20);
        var to = new DateTime(2026, 11, 3, 12, 0, 0);
        var rows = new[]
        {
            new BoilerBucket(Unix(new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc)), 1800),
            new BoilerBucket(Unix(new DateTime(2026, 11, 1, 11, 0, 0, DateTimeKind.Utc)), 900),
            new BoilerBucket(Unix(new DateTime(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc)), 3600),
        };

        var result = TemperatureHistory.Build([], [], rows, "30d", from, to, Utc);

        Assert.Equal("day", result.Bucket);
        Assert.Equal(15, result.Boiler.Count);
        Assert.Equal(2700, result.Boiler[12].Seconds);
        Assert.Equal(3600, result.Boiler[13].Seconds);
        Assert.All(result.Boiler.Where((_, i) => i != 12 && i != 13), b => Assert.Equal(0, b.Seconds));
    }

    [Fact]
    public void RuntimeKeepsTheHourlyBucketsInAShortRange()
    {
        var from = new DateTime(2026, 11, 2, 0, 0, 0);
        var to = new DateTime(2026, 11, 2, 12, 0, 0);
        var rows = new[]
        {
            new BoilerBucket(Unix(new DateTime(2026, 11, 2, 6, 0, 0, DateTimeKind.Utc)), 1200),
            new BoilerBucket(Unix(new DateTime(2026, 11, 2, 7, 0, 0, DateTimeKind.Utc)), 60),
        };

        var result = TemperatureHistory.Build([], [], rows, "today", from, to, Utc);

        Assert.Equal("hour", result.Bucket);
        Assert.Equal(13, result.Boiler.Count);
        Assert.Equal(1200, result.Boiler[6].Seconds);
        Assert.Equal(60, result.Boiler[7].Seconds);
    }
}
