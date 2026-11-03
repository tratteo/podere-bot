using Microsoft.Extensions.Logging.Abstractions;
using PodereBot.Services.Panel;
using Xunit;

namespace PodereBot.Tests;

public sealed class PanelStoreTests : IDisposable
{
    private readonly string directory = Path.Join(Path.GetTempPath(), $"poderebot-store-{Guid.NewGuid():N}");
    private readonly PanelStore store;

    public PanelStoreTests()
    {
        Directory.CreateDirectory(directory);
        store = new PanelStore(Path.Join(directory, "panel.db"), NullLogger<PanelStore>.Instance);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(directory, true);
        }
        catch (IOException)
        {
            // The WAL files may still be held briefly; the temp folder is disposable anyway.
        }
    }

    [Fact]
    public void StoreIsAvailable()
    {
        Assert.True(store.Available);
    }

    [Fact]
    public void UpsertFoldsAverageMinMaxAndSamples()
    {
        var at = new DateTime(2026, 3, 4, 10, 5, 0, DateTimeKind.Utc);
        store.RecordTemperature(at, 18);
        store.RecordTemperature(at.AddMinutes(5), 22);
        store.RecordTemperature(at.AddMinutes(10), 20);

        var bucket = Assert.Single(store.Hourly(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 4, 11, 0, 0, DateTimeKind.Utc)));

        Assert.Equal(20, bucket.Avg, 3);
        Assert.Equal(18, bucket.Min, 3);
        Assert.Equal(22, bucket.Max, 3);
        Assert.Equal(3, bucket.Samples);
    }

    [Fact]
    public void SamplesInDifferentHoursStayInDifferentBuckets()
    {
        store.RecordTemperature(new DateTime(2026, 3, 4, 10, 30, 0, DateTimeKind.Utc), 19);
        store.RecordTemperature(new DateTime(2026, 3, 4, 11, 30, 0, DateTimeKind.Utc), 21);

        var buckets = store.Hourly(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(2, buckets.Count);
        Assert.Equal(19, buckets[0].Avg, 3);
        Assert.Equal(21, buckets[1].Avg, 3);
    }

    [Fact]
    public void HistorySurvivesAReopen()
    {
        store.RecordTemperature(new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc), 20);

        var reopened = new PanelStore(store.Path, NullLogger<PanelStore>.Instance);
        var buckets = reopened.Hourly(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 3, 5, 0, 0, 0, DateTimeKind.Utc));

        Assert.Single(buckets);
    }

    [Fact]
    public void SensorSamplesFoldPerSensorAndHour()
    {
        var at = new DateTime(2026, 3, 4, 10, 5, 0, DateTimeKind.Utc);
        store.RecordSensorTemperatures(
            at,
            [new SensorSample("local", "Locale", 20), new SensorSample("aa", "Camera", 12)]
        );
        store.RecordSensorTemperatures(
            at.AddMinutes(5),
            [new SensorSample("local", "Locale", 22), new SensorSample("aa", "Camera", 10)]
        );

        var buckets = store.SensorHourly(
            new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 4, 11, 0, 0, DateTimeKind.Utc)
        );

        Assert.Equal(2, buckets.Count);
        var local = buckets.Single(b => b.Id == "local");
        Assert.Equal(21, local.Avg, 3);
        Assert.Equal(20, local.Min, 3);
        Assert.Equal(22, local.Max, 3);
        Assert.Equal(2, local.Samples);
        var camera = buckets.Single(b => b.Id == "aa");
        Assert.Equal(11, camera.Avg, 3);
        Assert.Equal("Camera", camera.Label);
    }

    [Fact]
    public void EventsAreListedNewestFirstAndFilteredByTopic()
    {
        store.AddEvent("technical", "Sensore fermo", null, new DateTime(2026, 3, 4, 9, 0, 0, DateTimeKind.Utc));
        store.AddEvent("gates", "Cancello aperto", "Pannello", new DateTime(2026, 3, 4, 10, 0, 0, DateTimeKind.Utc));

        var all = store.Events(10);
        Assert.Equal(2, all.Count);
        Assert.Equal("Cancello aperto", all[0].Title);

        var gates = store.Events(10, "gates");
        Assert.Single(gates);
        Assert.Equal("Pannello", gates[0].Detail);
    }

    [Fact]
    public void PruneDropsOldHistoryAndKeepsTheNewestEvents()
    {
        store.RecordTemperature(DateTime.UtcNow.AddDays(-500), 20);
        store.RecordTemperature(DateTime.UtcNow, 21);
        store.RecordSensorTemperatures(DateTime.UtcNow.AddDays(-500), [new SensorSample("local", "Locale", 20)]);
        store.RecordSensorTemperatures(DateTime.UtcNow, [new SensorSample("local", "Locale", 21)]);
        for (var i = 0; i < 5; i++)
            store.AddEvent("technical", $"Evento {i}", null, DateTime.UtcNow.AddMinutes(-i));

        store.Prune(historyRetentionDays: 365, eventRetentionDays: 30, maxEvents: 2);

        var buckets = store.Hourly(DateTime.UtcNow.AddDays(-600), DateTime.UtcNow.AddDays(1));
        Assert.Single(buckets);
        var sensors = store.SensorHourly(DateTime.UtcNow.AddDays(-600), DateTime.UtcNow.AddDays(1));
        Assert.Single(sensors);
        Assert.Equal(2, store.Events(10).Count);
    }
}
