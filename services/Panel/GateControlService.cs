namespace PodereBot.Services.Panel;

/// <summary>
/// Gate actions performed from the panel. The panel is an admin device: it is never rate
/// limited and it opens the gates even while they are locked for everyone else.
/// </summary>
internal sealed class GateControlService(
    GateDriver driver,
    Database db,
    GateAccessService access,
    NotificationLog log,
    ILogger<GateControlService> logger
)
{
    public async Task OpenAsync(GateId gate, string actor)
    {
        await driver.Open(gate);
        access.RecordPanelUse();
        log.RecordPanel($"Cancello {Describe(gate)} aperto", actor);
        logger.LogInformation("gate {g} opened from the panel by {a}", gate, actor);
    }

    public async Task ToggleLightsAsync(string actor)
    {
        await driver.ToggleLights();
        log.RecordPanel("Luci dei cancelli", actor);
    }

    public void OpenWindow(int hours, string actor)
    {
        if (hours is < 1 or > 24)
            throw new PanelValidationException("La finestra deve durare tra 1 e 24 ore.");

        var expiration = DateTime.Now.AddHours(hours);
        db.Edit(d => d.GatesOpenAccessExpirationDate = expiration);
        log.RecordPanel($"Cancelli sbloccati per {hours} h", $"{actor} · fino alle {expiration:HH:mm}");
        logger.LogInformation("gate window opened for {h} h from the panel", hours);
    }

    public void CloseWindow(string actor)
    {
        access.CloseWindow();
        log.RecordPanel("Cancelli bloccati", actor);
    }

    public static string Describe(GateId gate) => gate == GateId.pedestrian ? "pedonale" : "automatico";
}
