using PodereBot.Services.Panel;
using Xunit;

namespace PodereBot.Tests;

public sealed class WeatherServiceTests
{
    private const string payload = """
        {
          "current": { "time": "2026-11-02T22:45", "temperature_2m": 13.2, "weather_code": 3, "relative_humidity_2m": 70, "wind_speed_10m": 9.9, "is_day": 0 },
          "daily": { "time": ["2026-11-02"], "temperature_2m_min": [9.5], "temperature_2m_max": [16.3], "sunset": ["2026-11-02T17:02"], "sunrise": ["2026-11-02T06:41"] }
        }
        """;

    [Fact]
    public void ParsesCurrentConditionsAndTheDay()
    {
        var now = new DateTime(2026, 11, 2, 22, 50, 0);
        var snapshot = WeatherService.Parse(payload, now);

        Assert.NotNull(snapshot);
        Assert.Equal(13.2, snapshot!.Temperature);
        Assert.Equal(3, snapshot.Code);
        Assert.False(snapshot.IsDay);
        Assert.Equal(70, snapshot.Humidity);
        Assert.Equal(9.5, snapshot.Min);
        Assert.Equal(16.3, snapshot.Max);
        Assert.Equal(new DateTime(2026, 11, 2, 17, 2, 0), snapshot.Sunset);
        Assert.Equal(new DateTime(2026, 11, 2, 6, 41, 0), snapshot.Sunrise);
        Assert.Equal(now, snapshot.UpdatedAt);
    }

    [Fact]
    public void MissingCurrentBlockYieldsNothing()
    {
        Assert.Null(WeatherService.Parse("""{ "daily": {} }""", DateTime.Now));
    }
}
