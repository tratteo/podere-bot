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
    BoilerNotifier notifier,
    StartupGate startup,
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

    private readonly TimeSpan antifreezeNotifyCooldown = TimeSpan.FromMinutes(
        Math.Max(0, configuration.GetValue<int?>("Heating:Antifreeze:NotifyCooldownMinutes") ?? 15)
    );

    private bool? lastBoilerState;
    private bool? lastLocalSensorOk;
    private bool boilerInitialized;
    private bool temperatureInitialized;
    private bool localSensorInitialized;
    private bool sensorsInitialized;
    private bool suppressNextBoilerNotify;
    private DateTime? boilerOnSinceUtc;
    private DateTime lastAntifreezeNotifyUtc = DateTime.MinValue;
    private bool boilerStuckNotified;
    private bool outOfBandNotified;
    private bool antifreezeNotified;
    private readonly HashSet<string> staleSensors = [];
    private readonly HashSet<string> failedSensors = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(pollInterval);
        do
        {
            try
            {
                await CheckTemperatureAsync(stoppingToken);
                await CheckBoilerAsync();
                await CheckLocalSensorAsync(stoppingToken);
                await CheckSensorsAsync();
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
        var suppress = suppressNextBoilerNotify;
        suppressNextBoilerNotify = false;

        if (!boilerInitialized)
        {
            boilerInitialized = true;
            lastBoilerState = active;
            boilerOnSinceUtc = active ? DateTime.UtcNow : null;
            return;
        }

        if (lastBoilerState != active)
        {
            lastBoilerState = active;
            if (active)
            {
                boilerOnSinceUtc = DateTime.UtcNow;
                boilerStuckNotified = false;
                if (startup.Ready && !suppress && !notifier.WasJustNotified(true))
                    await bot.NotifyTopicAsync(NotificationTopic.Heating, "🔥 <b>Caldaia accesa</b>.");
            }
            else
            {
                boilerOnSinceUtc = null;
                boilerStuckNotified = false;
                if (startup.Ready && !suppress && !notifier.WasJustNotified(false))
                    await bot.NotifyTopicAsync(NotificationTopic.Heating, "❄️ <b>Caldaia spenta</b>.");
            }
        }

        if (!startup.Ready)
            return;

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

        if (!temperatureInitialized)
        {
            temperatureInitialized = true;
            antifreezeNotified = antifreeze.Enabled && HeatingPolicies.AntifreezeShouldEngage(value, antifreeze);
            outOfBandNotified = HeatingPolicies.IsOutOfBand(value, band);
            if (antifreezeNotified)
                lastAntifreezeNotifyUtc = DateTime.UtcNow;
            return;
        }

        if (!startup.Ready)
        {
            antifreezeNotified = antifreeze.Enabled && HeatingPolicies.AntifreezeShouldEngage(value, antifreeze);
            outOfBandNotified = HeatingPolicies.IsOutOfBand(value, band);
            return;
        }

        if (antifreeze.Enabled)
        {
            var freezing = HeatingPolicies.AntifreezeShouldEngage(value, antifreeze);
            if (freezing && !antifreezeNotified)
            {
                antifreezeNotified = true;
                suppressNextBoilerNotify = true;
                if (DateTime.UtcNow - lastAntifreezeNotifyUtc >= antifreezeNotifyCooldown)
                {
                    lastAntifreezeNotifyUtc = DateTime.UtcNow;
                    await bot.NotifyTopicAsync(
                        NotificationTopic.Heating,
                        $"🥶 <b>Antigelo attivo</b>: {value:0.0} °C, sotto la soglia di {antifreeze.ThresholdCelsius:0.0} °C. La caldaia resta accesa a prescindere dal programma."
                    );
                }
            }
            else if (!freezing && antifreezeNotified && HeatingPolicies.AntifreezeShouldRelease(value, antifreeze))
            {
                antifreezeNotified = false;
                suppressNextBoilerNotify = true;
                if (DateTime.UtcNow - lastAntifreezeNotifyUtc >= antifreezeNotifyCooldown)
                {
                    lastAntifreezeNotifyUtc = DateTime.UtcNow;
                    await bot.NotifyTopicAsync(
                        NotificationTopic.Heating,
                        $"🌡️ <b>Antigelo disattivato</b>: {value:0.0} °C, sopra la soglia di {antifreeze.ThresholdCelsius:0.0} °C."
                    );
                }
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

    private async Task CheckSensorsAsync()
    {
        // Only operational sensors raise control alerts: informational readings are not used for control.
        var all = temperatureDriver.GetExternalTemperatureReadings();
        var known = all.Where(r => !r.Informational && !r.HasError).ToList();
        var fresh = temperatureDriver.GetExternalTemperatureReadings(sensorTolerance).Where(r => !r.Informational).ToList();
        var freshIds = fresh.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var failed = all.Where(r => r.HasError).ToList();
        var failedIds = failed.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!sensorsInitialized)
        {
            sensorsInitialized = true;
            staleSensors.Clear();
            failedSensors.Clear();
            foreach (var reading in known)
                if (!freshIds.Contains(reading.Id))
                    staleSensors.Add(reading.Id);
            foreach (var reading in failed)
                failedSensors.Add(reading.Id);
            return;
        }

        if (!startup.Ready)
        {
            staleSensors.Clear();
            failedSensors.Clear();
            foreach (var reading in known)
                if (!freshIds.Contains(reading.Id))
                    staleSensors.Add(reading.Id);
            foreach (var reading in failed)
                failedSensors.Add(reading.Id);
            return;
        }

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

        foreach (var reading in failed)
        {
            if (!failedSensors.Add(reading.Id))
                continue;

            await bot.NotifyTopicAsync(
                NotificationTopic.Technical,
                $"⚠️ Il sensore <b>{Encode(reading.Location)}</b> segnala un problema: <i>{Encode(reading.Error ?? reading.Status ?? "errore")}</i>."
            );
        }

        foreach (var id in failedSensors.Where(id => !failedIds.Contains(id)).ToList())
        {
            failedSensors.Remove(id);
            var reading = all.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));
            if (reading == null)
                continue;
            await bot.NotifyTopicAsync(
                NotificationTopic.Technical,
                $"✅ Il sensore <b>{Encode(reading.Location)}</b> è tornato operativo."
            );
        }
    }

    private async Task CheckLocalSensorAsync(CancellationToken stoppingToken)
    {
        var probe = await temperatureDriver.ProbeLocalTemperature(stoppingToken);
        var ok = probe.Status == LocalTemperatureStatus.Ok;

        if (!localSensorInitialized)
        {
            localSensorInitialized = true;
            lastLocalSensorOk = ok;
            return;
        }

        if (lastLocalSensorOk == ok)
            return;
        lastLocalSensorOk = ok;

        if (!startup.Ready)
            return;

        if (ok)
            await bot.NotifyTopicAsync(NotificationTopic.Technical, "✅ <b>Sensore locale tornato disponibile</b>.");
        else
            await bot.NotifyTopicAsync(
                NotificationTopic.Technical,
                $"⚠️ <b>Sensore locale non disponibile</b> · {TemperatureCause(probe.Status)}."
            );
    }

    private static string TemperatureCause(LocalTemperatureStatus status) =>
        status switch
        {
            LocalTemperatureStatus.DevicesRootMissing => "configurazione",
            LocalTemperatureStatus.BusMasterMissing => "configurazione",
            LocalTemperatureStatus.NoSensors => "hardware",
            LocalTemperatureStatus.CrcError => "segnale",
            LocalTemperatureStatus.ReadError => "hardware",
            LocalTemperatureStatus.ParseError => "firmware",
            _ => "ok",
        };

    private static string Encode(string value) => System.Net.WebUtility.HtmlEncode(value);
}
