namespace PodereBot.Services.Panel;

/// <summary>
/// Samples the operational temperature into hourly buckets and prunes the retained data.
/// One sample every few minutes keeps a year of history tiny while preserving min/max.
/// </summary>
internal sealed class TemperatureHistoryRecorder(
    IConfiguration configuration,
    HeatingDriver heatingDriver,
    ITemperatureDriver temperatureReader,
    PanelStore store,
    NotificationLog log,
    ILogger<TemperatureHistoryRecorder> logger
) : BackgroundService
{
    private readonly PanelHistoryOptions options = OptionsReader.ReadPanelHistory(configuration);
    private readonly TimeSpan sensorTolerance = TimeSpan.FromSeconds(
        Math.Max(10, configuration.GetValue<int?>("Heating:TempSensorsToleranceSeconds") ?? 60)
    );
    private DateTime nextPruneUtc = DateTime.UtcNow.AddDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Prune();

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.SampleMinutes));
        do
        {
            try
            {
                if (DateTime.UtcNow >= nextPruneUtc)
                    Prune();

                var now = DateTime.UtcNow;
                var temperature = await heatingDriver.GetOperationalTemperature(stoppingToken);
                if (temperature != null)
                    store.RecordTemperature(now, temperature.Value);

                var samples = new List<SensorSample>();
                var probe = await temperatureReader.ProbeLocalTemperature(stoppingToken);
                if (probe.Status == LocalTemperatureStatus.Ok && probe.Temperature is float local)
                    samples.Add(new SensorSample("local", "Locale", local));
                foreach (var reading in temperatureReader.GetExternalTemperatureReadings())
                {
                    if (now - reading.Timestamp.ToUniversalTime() > sensorTolerance)
                        continue;
                    samples.Add(new SensorSample(reading.Id, reading.Location, reading.Temperature));
                }
                store.RecordSensorTemperatures(now, samples);
            }
            catch (Exception ex)
            {
                logger.LogWarning("temperature sampling failed: {m}", ex.Message);
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void Prune()
    {
        nextPruneUtc = DateTime.UtcNow.AddDays(1);
        try
        {
            log.Prune();
        }
        catch (Exception ex)
        {
            logger.LogWarning("panel pruning failed: {m}", ex.Message);
        }
    }
}
