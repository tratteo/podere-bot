using Microsoft.Extensions.Configuration;
using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class SensorAlertWindowTests
{
    [Fact]
    public void DefaultsToTenMinutes()
    {
        var configuration = new ConfigurationBuilder().Build();

        Assert.Equal(TimeSpan.FromMinutes(10), OptionsReader.ReadSensorAlertWindow(configuration));
    }

    [Fact]
    public void UsesTheConfiguredMinutes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Heating:SensorAlertMinutes"] = "5" })
            .Build();

        Assert.Equal(TimeSpan.FromMinutes(5), OptionsReader.ReadSensorAlertWindow(configuration));
    }

    [Fact]
    public void NeverGoesBelowOneMinute()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Heating:SensorAlertMinutes"] = "0" })
            .Build();

        Assert.Equal(TimeSpan.FromMinutes(1), OptionsReader.ReadSensorAlertWindow(configuration));
    }
}
