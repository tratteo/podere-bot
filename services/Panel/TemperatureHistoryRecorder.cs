namespace PodereBot.Services.Panel;

/// <summary>
/// Samples the operational temperature into hourly buckets and prunes the retained data.
/// One sample every few minutes keeps a year of history tiny while preserving min/max.
/// </summary>
internal sealed class TemperatureHistoryRecorder(
    IConfiguration configuration,
    HeatingDriver heatingDriver,
    PanelStore store,
    NotificationLog log,
    ILogger<TemperatureHistoryRecorder> logger
) : BackgroundService
{
    private readonly PanelHistoryOptions options = OptionsReader.ReadPanelHistory(configuration);
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

                var temperature = await heatingDriver.GetOperationalTemperature(stoppingToken);
                if (temperature != null)
                    store.RecordTemperature(DateTime.UtcNow, temperature.Value);
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
