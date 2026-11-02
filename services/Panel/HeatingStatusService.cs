using System.Text.Json;
using PodereBot.Lib.Common;

namespace PodereBot.Services.Panel;

internal sealed record HeatingAlert(string Id, string Title, string Detail, string Target);

/// <summary>Heating state and its warnings, computed with the same policies the daemon uses.</summary>
internal sealed class HeatingStatusService(HeatingDriver driver, Database db, IConfiguration configuration)
{
    private readonly AntifreezeOptions antifreeze = OptionsReader.ReadAntifreeze(configuration);
    private readonly BoilerAlertOptions boilerAlert = OptionsReader.ReadBoilerAlert(configuration);
    private readonly TemperatureBandOptions band = OptionsReader.ReadTemperatureBand(configuration);

    public AntifreezeOptions Antifreeze => antifreeze;
    public TemperatureBandOptions Band => band;

    public async Task<HeatingState> ReadAsync(CancellationToken cancellationToken = default)
    {
        var data = db.Data;
        var temperature = await driver.GetOperationalTemperature(cancellationToken);
        var boiler = driver.IsBoilerActive();
        var onFor = data.BoilerSinceUtc is DateTime since ? DateTime.UtcNow - since : (TimeSpan?)null;
        var program = data.HeatingProgram;
        var alerts = new List<HeatingAlert>();

        if (boiler && onFor != null && HeatingPolicies.BoilerRunningTooLong(onFor.Value, boilerAlert))
            alerts.Add(
                new HeatingAlert(
                    "boiler-long",
                    "Caldaia accesa da troppo",
                    $"{onFor.Value.TotalHours:0.0} h · massimo {boilerAlert.MaxContinuousHours:0.#} h",
                    "heating"
                )
            );

        if (temperature is float measured)
        {
            if (HeatingPolicies.IsOutOfBand(measured, band))
                alerts.Add(
                    new HeatingAlert(
                        "temperature-band",
                        "Temperatura fuori banda",
                        $"{measured:0.0} °C · banda {band.MinCelsius:0.#}–{band.MaxCelsius:0.#} °C",
                        "heating"
                    )
                );

            if (antifreeze.Enabled && HeatingPolicies.AntifreezeShouldEngage(measured, antifreeze))
                alerts.Add(
                    new HeatingAlert(
                        "antifreeze",
                        "Antigelo attivo",
                        $"{measured:0.0} °C sotto la soglia di {antifreeze.ThresholdCelsius:0.#} °C",
                        "heating"
                    )
                );
        }

        if (program?.IsSuspended == true)
            alerts.Add(new HeatingAlert("program-suspended", "Programma sospeso", "Controllo manuale attivo", "heating"));

        return new HeatingState(temperature, boiler, onFor, data.ManualHeatingActive, program, alerts);
    }
}

internal sealed record HeatingState(
    double? Temperature,
    bool BoilerActive,
    TimeSpan? BoilerOnFor,
    bool Manual,
    HeatingProgram? Program,
    IReadOnlyList<HeatingAlert> Alerts
);

/// <summary>Reads the updater state file so the panel can show the running version and the last run.</summary>
internal sealed class UpdateStateStore(IHostEnvironment environment, ILogger<UpdateStateStore> logger)
{
    private readonly string path = Path.Join(environment.ContentRootPath, "update-state.json");

    public PanelUpdateDto Read()
    {
        try
        {
            if (!File.Exists(path))
                return new PanelUpdateDto(null, null, null, null, null);

            var state = JsonSerializer.Deserialize<UpdateStateFile>(File.ReadAllText(path));
            return new PanelUpdateDto(
                state?.Result,
                Short(state?.FromSha),
                Short(state?.ToSha),
                ParseUtc(state?.LastRunUtc),
                state?.Reason
            );
        }
        catch (Exception ex)
        {
            logger.LogDebug("unable to read the update state: {m}", ex.Message);
            return new PanelUpdateDto(null, null, null, null, null);
        }
    }

    public string Version
    {
        get
        {
            var state = Read();
            return state.ToSha ?? "locale";
        }
    }

    private static DateTime? ParseUtc(string? value) =>
        DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

    private static string? Short(string? sha) => string.IsNullOrWhiteSpace(sha) ? null : sha[..Math.Min(7, sha.Length)];

    private sealed class UpdateStateFile
    {
        public string? LastRunUtc { get; set; }
        public string? Result { get; set; }
        public string? FromSha { get; set; }
        public string? ToSha { get; set; }
        public string? FailedSha { get; set; }
        public string? Reason { get; set; }
    }
}
