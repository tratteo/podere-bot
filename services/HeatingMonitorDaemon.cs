using PodereBot.Services.Hosted;
namespace PodereBot.Services;

/// <summary>
/// Observes heating and sensors and sends the alerts. It never actuates anything: control stays
/// in <see cref="HeatingProgramDaemon"/> so there is a single writer for the boiler pin.
/// </summary>
internal sealed class HeatingMonitorDaemon(
    IConfiguration configuration,
    HeatingDriver heatingDriver,
    ITemperatureDriver temperatureDriver,
    BotHostedService bot,
    ILogger<HeatingMonitorDaemon> logger
) : BackgroundService
{
    private readonly AntifreezeOptions antifreeze = OptionsReader.ReadAntifreeze(configuration);
    private readonly BoilerAlertOptions boilerAlert = OptionsReader.ReadBoilerAlert(configuration);
    private readonly TemperatureBandOptions band = OptionsReader.ReadTemperatureBand(configuration);
    private readonly TimeSpan pollInterval = TimeSpan.FromSeconds(
        Math.Max(5, configuration.GetValue<int?>("Heating:MonitorIntervalSeconds") ?? 30)
    );
    private readonly TimeSpan sensorTolerance = TimeSpan.FromSeconds(
        Math.Max(10, configuration.GetValue<int?>("Heating:TempSensorsToleranceSeconds") ?? 60)
    );

    private bool? lastBoilerState;
    private DateTime? boilerOnSinceUtc;
    private bool boilerStuckNotified;
    private bool outOfBandNotified;
    private bool antifreezeNotified;
    private readonly HashSet<string> staleSensors = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(pollInterval);
        do
        {
            try
            {
                await CheckBoilerAsync();
                await CheckTemperatureAsync(stoppingToken);
                CheckSensors();
            }
            catch (Exception ex)
            {
                logger.LogWarning("heating monitor failed: {m}", ex.Message);
            }
        } while (!stoppingToken.IsCancellationRequested && await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task CheckBoilerAsync()
    {
        var active = heatingDriver.IsBoilerActive();

        if (lastBoilerState != active)
        {
            lastBoilerState = active;
            if (active)
            {
                boilerOnSinceUtc = DateTime.UtcNow;
                boilerStuckNotified = false;
                await bot.NotifyTopicAsync(NotificationTopic.Heating, "🔥 <b>Caldaia accesa</b>.");
            }
            else
            {
                boilerOnSinceUtc = null;
                boilerStuckNotified = false;
                await bot.NotifyTopicAsync(NotificationTopic.Heating, "❄️ <b>Caldaia spenta</b>.");
            }
        }

        if (!active || boilerOnSinceUtc == null || boilerStuckNotified)
            return;

        var running = DateTime.UtcNow - boilerOnSinceUtc.Value;
        if (!HeatingPolicies.BoilerRunningTooLong(running, boilerAlert))
            return;

        boilerStuckNotified = true;
        await bot.NotifyTopicAsync(
            NotificationTopic.Heating,
            $"⚠️ La caldaia è accesa da <b>{running.TotalHours:0.0} ore</b> senza interruzioni. Controlla che non sia un contatto bloccato."
        );
    }

    private async Task CheckTemperatureAsync(CancellationToken stoppingToken)
    {
        var temperature = await heatingDriver.GetOperationalTemperature(stoppingToken);
        if (temperature == null)
            return;

        var value = temperature.Value;

        if (antifreeze.Enabled)
        {
            var freezing = HeatingPolicies.AntifreezeShouldEngage(value, antifreeze);
            if (freezing && !antifreezeNotified)
            {
                antifreezeNotified = true;
                await bot.NotifyTopicAsync(
                    NotificationTopic.Heating,
                    $"🥶 <b>Antigelo attivo</b>: {value:0.0} °C, sotto la soglia di {antifreeze.ThresholdCelsius:0.0} °C. La caldaia resta accesa a prescindere dal programma."
                );
            }
            else if (!freezing && antifreezeNotified && HeatingPolicies.AntifreezeShouldRelease(value, antifreeze))
            {
                antifreezeNotified = false;
                await bot.NotifyTopicAsync(
                    NotificationTopic.Heating,
                    $"🌡️ <b>Antigelo disattivato</b>: {value:0.0} °C, sopra la soglia di {antifreeze.ThresholdCelsius:0.0} °C."
                );
            }
        }

        var outOfBand = HeatingPolicies.IsOutOfBand(value, band);
        if (outOfBand && !outOfBandNotified)
        {
            outOfBandNotified = true;
            await bot.NotifyTopicAsync(
                NotificationTopic.Heating,
                $"⚠️ Temperatura operativa <b>{value:0.0} °C</b> fuori dalla banda {band.MinCelsius:0.0}–{band.MaxCelsius:0.0} °C."
            );
        }
        else if (!outOfBand && outOfBandNotified)
        {
            outOfBandNotified = false;
            await bot.NotifyTopicAsync(
                NotificationTopic.Heating,
                $"✅ Temperatura operativa rientrata: <b>{value:0.0} °C</b>."
            );
        }
    }

    private void CheckSensors() => _ = ReportStaleSensorsAsync();

    private async Task ReportStaleSensorsAsync()
    {
        var known = temperatureDriver.GetExternalTemperatureReadings();
        var fresh = temperatureDriver.GetExternalTemperatureReadings(sensorTolerance);
        var freshIds = fresh.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var reading in known)
        {
            if (freshIds.Contains(reading.Id))
                continue;

            if (!staleSensors.Add(reading.Id))
                continue;

            var age = DateTime.UtcNow - reading.Timestamp;
            await bot.NotifyTopicAsync(
                NotificationTopic.Technical,
                $"📡 Il sensore <b>{Encode(reading.Location)}</b> non invia letture da <b>{age.TotalMinutes:0} minuti</b>."
            );
        }

        foreach (var id in staleSensors.Where(id => freshIds.Contains(id)).ToList())
        {
            staleSensors.Remove(id);
            var reading = fresh.First(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            await bot.NotifyTopicAsync(
                NotificationTopic.Technical,
                $"✅ Il sensore <b>{Encode(reading.Location)}</b> è tornato a inviare letture."
            );
        }
    }

    private static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);
}
