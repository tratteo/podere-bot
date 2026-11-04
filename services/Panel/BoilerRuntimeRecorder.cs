namespace PodereBot.Services.Panel;

/// <summary>
/// Adds the boiler running time to the hourly buckets. It only observes: a manual switch, the
/// program and the antifreeze all end up in <c>BoilerActive</c>, so every one of them counts.
/// A ten second sample keeps the buckets within a few seconds of the real runtime, while the
/// database is written once a minute at most so the SD card is not kept busy.
/// </summary>
internal sealed class BoilerRuntimeRecorder(
    Database db,
    PanelStore store,
    ILogger<BoilerRuntimeRecorder> logger
) : BackgroundService
{
    private static readonly TimeSpan interval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan flushAfter = TimeSpan.FromSeconds(60);

    private DateTime? lastUtc;
    private DateTime? pendingFromUtc;
    private DateTime? pendingToUtc;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                Sample(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogWarning("boiler runtime sampling failed: {m}", ex.Message);
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));

        Flush();
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        Flush();
    }

    private void Sample(DateTime nowUtc)
    {
        var previous = lastUtc;
        lastUtc = nowUtc;
        if (previous == null)
            return;

        var data = db.Data;
        if (!data.BoilerActive)
        {
            Flush();
            return;
        }

        var from = previous.Value;
        if (data.BoilerSinceUtc is DateTime sinceUtc)
        {
            var started = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
            if (started > from)
                from = started;
        }
        if (from >= nowUtc)
        {
            Flush();
            return;
        }

        pendingFromUtc ??= from;
        pendingToUtc = nowUtc;

        var span = pendingToUtc.Value - pendingFromUtc.Value;
        if (span >= flushAfter || pendingFromUtc.Value.Hour != nowUtc.Hour)
            Flush();
    }

    private void Flush()
    {
        if (pendingFromUtc is not DateTime from || pendingToUtc is not DateTime to || to <= from)
        {
            pendingFromUtc = null;
            pendingToUtc = null;
            return;
        }

        store.AddBoilerSpan(from, to);
        pendingFromUtc = null;
        pendingToUtc = null;
    }
}
