using System.Globalization;
using Microsoft.Extensions.Configuration;
using PodereBot.Lib.Common;
using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class HeatingTests
{
    private static HeatingInterval Interval(int fromHour, int fromMinute, int toHour, int toMinute, float temperature) =>
        new()
        {
            HoursFrom = fromHour,
            MinutesFrom = fromMinute,
            HoursTo = toHour,
            MinutesTo = toMinute,
            Temperature = temperature,
        };

    // ===== Antifreeze =====

    [Fact]
    public void AntifreezeEngagesBelowThreshold()
    {
        var options = new AntifreezeOptions(true, 5.0);

        Assert.True(HeatingPolicies.AntifreezeShouldEngage(4.9, options));
        Assert.False(HeatingPolicies.AntifreezeShouldEngage(5.0, options));
        Assert.False(HeatingPolicies.AntifreezeShouldEngage(6.0, options));
    }

    [Fact]
    public void AntifreezeStaysOnWithinTheHysteresisBand()
    {
        var options = new AntifreezeOptions(true, 5.0);

        // Engaged at 4.5 C, it must not release at 5.4 C to avoid toggling around the threshold.
        Assert.False(HeatingPolicies.AntifreezeShouldRelease(5.4, options));
        Assert.True(HeatingPolicies.AntifreezeShouldRelease(5.6, options));
    }

    [Fact]
    public void DisabledAntifreezeNeverEngages()
    {
        var options = new AntifreezeOptions(false, 5.0);

        Assert.False(HeatingPolicies.AntifreezeShouldEngage(-10, options));
        Assert.True(HeatingPolicies.AntifreezeShouldRelease(-10, options));
    }

    [Fact]
    public void AntifreezeDefaultsToFiveDegrees()
    {
        Assert.True(AntifreezeOptions.Default.Enabled);
        Assert.Equal(5.0, AntifreezeOptions.Default.ThresholdCelsius);
    }

    // ===== Temperature band =====

    [Theory]
    [InlineData(4.9, true)]
    [InlineData(5.0, false)]
    [InlineData(20.0, false)]
    [InlineData(30.0, false)]
    [InlineData(30.1, true)]
    public void TemperatureBandDetectsExcursions(double temperature, bool expected)
    {
        Assert.Equal(expected, HeatingPolicies.IsOutOfBand(temperature, new TemperatureBandOptions(5.0, 30.0)));
    }

    // ===== Boiler stuck =====

    [Fact]
    public void BoilerAlertTriggersAfterTheConfiguredHours()
    {
        var options = new BoilerAlertOptions(4.0);

        Assert.False(HeatingPolicies.BoilerRunningTooLong(TimeSpan.FromHours(3.9), options));
        Assert.True(HeatingPolicies.BoilerRunningTooLong(TimeSpan.FromHours(4.0), options));
        Assert.True(HeatingPolicies.BoilerRunningTooLong(TimeSpan.FromHours(10), options));
    }

    // ===== Configuration defaults and overrides =====

    [Fact]
    public void OptionsFallBackToDefaults()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(AntifreezeOptions.Default, OptionsReader.ReadAntifreeze(configuration));
        Assert.Equal(BoilerAlertOptions.Default, OptionsReader.ReadBoilerAlert(configuration));
        Assert.Equal(TemperatureBandOptions.Default, OptionsReader.ReadTemperatureBand(configuration));
        Assert.Equal(GateRateLimitOptions.Default, OptionsReader.ReadGateRateLimit(configuration));
    }

    [Fact]
    public void OptionsAreReadFromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Heating:Antifreeze:Enabled"] = "false",
                    ["Heating:Antifreeze:ThresholdCelsius"] = "2.5",
                    ["Heating:BoilerAlert:MaxContinuousHours"] = "6",
                    ["Heating:TemperatureBand:MinCelsius"] = "3",
                    ["Heating:TemperatureBand:MaxCelsius"] = "28",
                    ["Gates:RateLimit:MaxOpenings"] = "5",
                    ["Gates:RateLimit:WindowMinutes"] = "15",
                }
            )
            .Build();

        Assert.Equal(new AntifreezeOptions(false, 2.5), OptionsReader.ReadAntifreeze(configuration));
        Assert.Equal(new BoilerAlertOptions(6), OptionsReader.ReadBoilerAlert(configuration));
        Assert.Equal(new TemperatureBandOptions(3, 28), OptionsReader.ReadTemperatureBand(configuration));
        Assert.Equal(new GateRateLimitOptions(5, 15), OptionsReader.ReadGateRateLimit(configuration));
    }

    // ===== HeatingProgram =====

    [Fact]
    public void TryBuildAcceptsOrderedNonOverlappingIntervals()
    {
        var built = HeatingProgram.TryBuild([Interval(6, 0, 8, 0, 19f), Interval(18, 0, 22, 0, 20f)], out var program);

        Assert.True(built);
        Assert.Equal(2, program!.Intervals.Count);
    }

    [Fact]
    public void TryBuildRejectsOverlappingIntervals()
    {
        var built = HeatingProgram.TryBuild([Interval(6, 0, 10, 0, 19f), Interval(9, 0, 12, 0, 20f)], out var program);

        Assert.False(built);
        Assert.Null(program);
    }

    [Fact]
    public void TryBuildRejectsAnIntervalThatEndsBeforeItStarts()
    {
        // 23:00-06:00 would cross midnight and never match the schedule, so it is refused
        // instead of being silently accepted.
        var built = HeatingProgram.TryBuild([Interval(23, 0, 6, 0, 19f)], out var program);

        Assert.False(built);
        Assert.Null(program);
    }

    [Fact]
    public void TryBuildRejectsAnEmptyInterval()
    {
        var built = HeatingProgram.TryBuild([Interval(8, 0, 8, 0, 19f)], out var program);

        Assert.False(built);
        Assert.Null(program);
    }

    [Fact]
    public void IntervalTimestampsAndCodeAreConsistent()
    {
        var interval = Interval(6, 30, 8, 15, 19.5f);

        Assert.Equal(6 * 3600 + 30 * 60, interval.FromTimestamp);
        Assert.Equal(8 * 3600 + 15 * 60, interval.ToTimestamp);
        Assert.Equal("06:30-08:15@19,50", interval.ToCodeString().Replace('.', ','));
    }

    [Fact]
    public void ProgramRoundTripsThroughItsCodeString()
    {
        var built = HeatingProgram.TryBuild([Interval(6, 0, 8, 0, 19f)], out var program);

        Assert.True(built);
        Assert.StartsWith("06:00-08:00@19", program!.ToCodeString());
    }

    [Fact]
    public void TimestampUsesSecondsSinceMidnight()
    {
        var interval = Interval(12, 0, 13, 0, 20f);

        Assert.Equal(43200, interval.FromTimestamp);
        Assert.Equal(46800, interval.ToTimestamp);
        Assert.True(
            TimeSpan.FromSeconds(interval.FromTimestamp).TotalHours == 12,
            string.Create(CultureInfo.InvariantCulture, $"unexpected timestamp {interval.FromTimestamp}")
        );
    }
}
