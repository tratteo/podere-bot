using System.Text.Json;
using PodereBot.Services;
using Xunit;

namespace PodereBot.Tests;

public sealed class DatabaseSchemaTests
{
    // Shape produced by the previous Newtonsoft-based serializer. It must stay readable.
    private const string legacyJson = """
        {
          "GatesOpenAccessExpirationDate": "2026-11-02T10:00:00",
          "HeatingProgram": {
            "IsSuspended": false,
            "Intervals": [
              {
                "HoursFrom": 6,
                "MinutesFrom": 0,
                "HoursTo": 8,
                "MinutesTo": 30,
                "Temperature": 19.5,
                "FromTimestamp": 21600,
                "ToTimestamp": 30600
              }
            ]
          },
          "TradingAlertsSubscriptions": []
        }
        """;

    [Fact]
    public void LegacyDatabaseFileStaysReadable()
    {
        var schema = JsonSerializer.Deserialize<DatabaseSchema>(legacyJson);

        Assert.NotNull(schema);
        Assert.Equal(new DateTime(2026, 11, 2, 10, 0, 0), schema!.GatesOpenAccessExpirationDate);
        Assert.NotNull(schema.HeatingProgram);
        Assert.Single(schema.HeatingProgram!.Intervals);

        var interval = schema.HeatingProgram.Intervals[0];
        Assert.Equal(6, interval.HoursFrom);
        Assert.Equal(30, interval.MinutesTo);
        Assert.Equal(19.5f, interval.Temperature);
        Assert.Equal(21600, interval.FromTimestamp);
    }

    [Fact]
    public void HeatingProgramSurvivesARoundTrip()
    {
        var schema = JsonSerializer.Deserialize<DatabaseSchema>(legacyJson)!;

        var json = JsonSerializer.Serialize(schema);
        var again = JsonSerializer.Deserialize<DatabaseSchema>(json)!;

        Assert.Single(again.HeatingProgram!.Intervals);
        Assert.Equal(30600, again.HeatingProgram.Intervals[0].ToTimestamp);
    }
}
