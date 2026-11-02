using System.Diagnostics;

namespace PodereBot.Services.Panel;

/// <summary>
/// Composes everything the panel shows in one snapshot. Nothing secret is exposed: no Telegram
/// token, no API token, no device key and no raw chat ids beyond the configured user ids.
/// </summary>
internal sealed class PanelSnapshotService(
    HealthService health,
    Database db,
    HeatingStatusService heatingStatus,
    ITemperatureDriver temperatureReader,
    GateAccessService gateAccess,
    UserDirectory users,
    PanelUserNames userNames,
    UsageTracker usage,
    PanelStore store,
    PanelDeviceService devices,
    NotificationLog log,
    UpdateStateStore updates,
    IConfiguration configuration,
    IHostEnvironment environment
)
{
    private readonly TimeSpan sensorTolerance = TimeSpan.FromSeconds(
        Math.Max(10, configuration.GetValue<int?>("Heating:TempSensorsToleranceSeconds") ?? 60)
    );

    public async Task<PanelOverviewDto> BuildAsync(
        string mode,
        PanelDeviceSession? session,
        CancellationToken cancellationToken = default
    )
    {
        var version = updates.Version;
        var report = health.Check();
        var heating = await heatingStatus.ReadAsync(cancellationToken);
        var probe = await temperatureReader.ProbeLocalTemperature(cancellationToken);
        var external = temperatureReader.GetExternalTemperatureReadings();
        var fresh = temperatureReader
            .GetExternalTemperatureReadings(sensorTolerance)
            .Select(r => r.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sensors = external
            .Select(r => new PanelSensorDto(
                r.Id,
                r.Location,
                r.Temperature,
                r.Timestamp,
                r.Informational,
                fresh.Contains(r.Id)
            ))
            .ToList();

        var operational = external.Where(r => !r.Informational && fresh.Contains(r.Id)).Select(r => (double)r.Temperature).ToList();
        if (probe.Temperature.HasValue)
            operational.Add(probe.Temperature.Value);

        var data = db.Data;
        var expiration = data.GatesOpenAccessExpirationDate;
        var alerts = BuildAlerts(heating, sensors, probe.Status, report);
        var userDtos = await BuildUsersAsync(cancellationToken);

        return new PanelOverviewDto(
            DateTime.Now,
            new PanelSessionDto(mode, session?.Name, session?.Id, version),
            new PanelHealthDto(report.Ok, report.CoreOk, report.TelegramOk, report.DatabaseOk, store.Available, report.Throttled, report.CpuTemperatureCelsius),
            new PanelHeatingDto(
                heating.Temperature,
                heating.BoilerActive,
                heating.BoilerOnFor?.TotalMinutes,
                heating.Manual,
                new PanelProgramDto(
                    heating.Program?.IsSuspended ?? false,
                    heating.Program != null,
                    heating.Program?.Intervals
                        .Select(i => new PanelIntervalDto(i.FromTimestamp / 60, i.ToTimestamp / 60, i.Temperature))
                        .ToList()
                    ?? []
                ),
                heatingStatus.Antifreeze.Enabled,
                heatingStatus.Antifreeze.ThresholdCelsius,
                heatingStatus.Band.MinCelsius,
                heatingStatus.Band.MaxCelsius
            ),
            new PanelGatesDto(
                GateAccessPolicy.IsOpen(expiration, DateTime.Now),
                expiration,
                gateAccess.Options.MaxOpenings,
                gateAccess.Options.WindowMinutes,
                data.GateAccessUsers,
                log.List(5, NotificationTopic.Gates)
                    .Select(e => new PanelGateEventDto(e.Title, e.Detail, e.At))
                    .ToList()
            ),
            new PanelTemperaturesDto(
                probe.Temperature,
                probe.Status.ToString(),
                operational.Count > 0 ? Math.Round(operational.Average(), 1) : null,
                DateTime.Now,
                sensors,
                Trend()
            ),
            userDtos,
            devices
                .List()
                .Select(d => new PanelDeviceDto(
                    d.Id,
                    d.Name,
                    d.State,
                    d.CreatedAt,
                    d.LastSeenAt,
                    d.CreatedBy,
                    d.PairingExpiresAt
                ))
                .ToList(),
            Usage(),
            alerts,
            log.List(3).Select(e => new PanelEventDto(e.Id, e.Topic, e.Title, e.Detail, e.At)).ToList(),
            updates.Read(),
            SystemInfo(version)
        );
    }

    public IReadOnlyList<double> Trend()
    {
        var now = DateTime.UtcNow;
        return store.Hourly(now.AddHours(-12), now.AddHours(1)).Select(b => Math.Round(b.Avg, 1)).ToList();
    }

    private async Task<IReadOnlyList<PanelUserDto>> BuildUsersAsync(CancellationToken cancellationToken)
    {
        var entries = users.All();
        var resolved = await Task.WhenAll(
            entries.Select(async u =>
                new PanelUserDto(
                    u.Id,
                    u.Name ?? await userNames.ResolveAsync(u.Id, cancellationToken),
                    u.Entitlements,
                    u.Topics,
                    u.IsAdmin,
                    u.CanControlGates,
                    users.IsRuntimeOnly(u.Id)
                )
            )
        );
        return resolved;
    }

    private List<PanelAlertDto> BuildAlerts(
        HeatingState heating,
        IReadOnlyList<PanelSensorDto> sensors,
        LocalTemperatureStatus localStatus,
        HealthReport report
    )
    {
        var alerts = heating
            .Alerts.Select(a => new PanelAlertDto(a.Id, a.Title, a.Detail, a.Target))
            .ToList();

        foreach (var sensor in sensors.Where(s => !s.Informational && !s.Fresh))
            alerts.Add(
                new PanelAlertDto(
                    $"sensor-{sensor.Id}",
                    $"Sensore {sensor.Location} in ritardo",
                    $"Ultima lettura {FormatAge(DateTime.UtcNow - sensor.Timestamp)}",
                    "system"
                )
            );

        if (localStatus != LocalTemperatureStatus.Ok)
            alerts.Add(new PanelAlertDto("local-sensor", "Sensore locale non disponibile", localStatus.ToString(), "system"));

        if (!report.TelegramOk)
            alerts.Add(new PanelAlertDto("telegram", "Telegram non connesso", "Il bot continua a funzionare in locale", "system"));

        if (!store.Available)
            alerts.Add(new PanelAlertDto("panel-db", "Registro non disponibile", "Storico ed eventi non vengono salvati", "system"));

        return alerts;
    }

    private PanelUsageDto Usage()
    {
        var summary = usage.GetLastDays(7);
        var budget = configuration.GetValue<double?>("AI:WeeklyBudgetUsd") ?? 0;
        return new PanelUsageDto(
            Math.Round(summary.CostUsd, 2),
            budget,
            summary.Calls,
            summary.InputTokens + summary.OutputTokens,
            summary.CachedInputTokens
        );
    }

    private PanelSystemDto SystemInfo(string version)
    {
        using var process = Process.GetCurrentProcess();
        return new PanelSystemDto(
            (long)(DateTime.Now - process.StartTime).TotalSeconds,
            process.PrivateMemorySize64 / (1024 * 1024),
            environment.EnvironmentName,
            version
        );
    }

    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 90)
            return $"{age.TotalMinutes:0} min";
        if (age.TotalHours < 36)
            return $"{age.TotalHours:0} h";
        return $"{age.TotalDays:0} g";
    }
}
