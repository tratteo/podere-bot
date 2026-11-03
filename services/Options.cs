namespace PodereBot.Services;

/// <summary>Antifreeze protection: keeps the boiler on regardless of the schedule.</summary>
internal sealed record AntifreezeOptions(bool Enabled, double ThresholdCelsius)
{
    public static readonly AntifreezeOptions Default = new(true, 5.0);
}

internal sealed record BoilerAlertOptions(double MaxContinuousHours)
{
    public static readonly BoilerAlertOptions Default = new(4.0);
}

internal sealed record TemperatureBandOptions(double MinCelsius, double MaxCelsius)
{
    public static readonly TemperatureBandOptions Default = new(5.0, 30.0);
}

internal sealed record GateRateLimitOptions(int MaxOpenings, int WindowMinutes)
{
    public static readonly GateRateLimitOptions Default = new(3, 10);
}

/// <summary>Local panel hosting: kiosk port is fixed, the LAN port and its policy are configurable.</summary>
internal sealed record PanelOptions(
    int LanPort,
    string? LanHost,
    bool AllowLan,
    int PairingMinutes,
    int SessionDays
)
{
    public const int KioskPort = 5051;

    public static readonly PanelOptions Default = new(5052, null, true, 10, 30);
}

internal sealed record PanelHistoryOptions(int SampleMinutes, int RetentionDays)
{
    public static readonly PanelHistoryOptions Default = new(5, 365);
}

internal sealed record PanelEventOptions(int RetentionDays, int MaxEntries)
{
    public static readonly PanelEventOptions Default = new(30, 500);
}

/// <summary>Where the house is: shared by /sendpos and the outdoor weather.</summary>
internal sealed record HomeOptions(double Latitude, double Longitude, string Name, string Address)
{
    public static readonly HomeOptions Default = new(41.4986180363405, 12.797875896442752, "Podere 739 (canne libere 🪴)", "Via del Valloncello 16");
}

/// <summary>Outdoor weather from Open-Meteo, used until a real outdoor sensor exists.</summary>
internal sealed record WeatherOptions(bool Enabled, int RefreshMinutes)
{
    public static readonly WeatherOptions Default = new(true, 15);
}

internal static class OptionsReader
{
    public static HomeOptions ReadHome(IConfiguration configuration)
    {
        var section = configuration.GetSection("Home");
        return new HomeOptions(
            section.GetValue<double?>("Latitude") ?? HomeOptions.Default.Latitude,
            section.GetValue<double?>("Longitude") ?? HomeOptions.Default.Longitude,
            string.IsNullOrWhiteSpace(section.GetValue<string>("Name")) ? HomeOptions.Default.Name : section.GetValue<string>("Name")!,
            string.IsNullOrWhiteSpace(section.GetValue<string>("Address")) ? HomeOptions.Default.Address : section.GetValue<string>("Address")!
        );
    }

    public static WeatherOptions ReadWeather(IConfiguration configuration)
    {
        var section = configuration.GetSection("Weather");
        return new WeatherOptions(
            section.GetValue<bool?>("Enabled") ?? WeatherOptions.Default.Enabled,
            Math.Max(5, section.GetValue<int?>("RefreshMinutes") ?? WeatherOptions.Default.RefreshMinutes)
        );
    }

    public static AntifreezeOptions ReadAntifreeze(IConfiguration configuration)
    {
        var section = configuration.GetSection("Heating:Antifreeze");
        return new AntifreezeOptions(
            section.GetValue<bool?>("Enabled") ?? AntifreezeOptions.Default.Enabled,
            section.GetValue<double?>("ThresholdCelsius") ?? AntifreezeOptions.Default.ThresholdCelsius
        );
    }

    public static BoilerAlertOptions ReadBoilerAlert(IConfiguration configuration)
    {
        var section = configuration.GetSection("Heating:BoilerAlert");
        return new BoilerAlertOptions(
            section.GetValue<double?>("MaxContinuousHours") ?? BoilerAlertOptions.Default.MaxContinuousHours
        );
    }

    public static TemperatureBandOptions ReadTemperatureBand(IConfiguration configuration)
    {
        var section = configuration.GetSection("Heating:TemperatureBand");
        return new TemperatureBandOptions(
            section.GetValue<double?>("MinCelsius") ?? TemperatureBandOptions.Default.MinCelsius,
            section.GetValue<double?>("MaxCelsius") ?? TemperatureBandOptions.Default.MaxCelsius
        );
    }

    public static GateRateLimitOptions ReadGateRateLimit(IConfiguration configuration)
    {
        var section = configuration.GetSection("Gates:RateLimit");
        return new GateRateLimitOptions(
            section.GetValue<int?>("MaxOpenings") ?? GateRateLimitOptions.Default.MaxOpenings,
            section.GetValue<int?>("WindowMinutes") ?? GateRateLimitOptions.Default.WindowMinutes
        );
    }

    public static PanelOptions ReadPanel(IConfiguration configuration)
    {
        var section = configuration.GetSection("Panel");
        return new PanelOptions(
            section.GetValue<int?>("LanPort") ?? PanelOptions.Default.LanPort,
            string.IsNullOrWhiteSpace(section.GetValue<string>("LanHost")) ? null : section.GetValue<string>("LanHost")!.Trim(),
            section.GetValue<bool?>("AllowLan") ?? PanelOptions.Default.AllowLan,
            Math.Max(1, section.GetValue<int?>("PairingMinutes") ?? PanelOptions.Default.PairingMinutes),
            Math.Max(1, section.GetValue<int?>("SessionDays") ?? PanelOptions.Default.SessionDays)
        );
    }

    public static PanelHistoryOptions ReadPanelHistory(IConfiguration configuration)
    {
        var section = configuration.GetSection("Panel:History");
        return new PanelHistoryOptions(
            Math.Max(1, section.GetValue<int?>("SampleMinutes") ?? PanelHistoryOptions.Default.SampleMinutes),
            Math.Max(1, section.GetValue<int?>("RetentionDays") ?? PanelHistoryOptions.Default.RetentionDays)
        );
    }

    public static PanelEventOptions ReadPanelEvents(IConfiguration configuration)
    {
        var section = configuration.GetSection("Panel:Events");
        return new PanelEventOptions(
            Math.Max(1, section.GetValue<int?>("RetentionDays") ?? PanelEventOptions.Default.RetentionDays),
            Math.Max(1, section.GetValue<int?>("MaxEntries") ?? PanelEventOptions.Default.MaxEntries)
        );
    }
}

/// <summary>Pure decisions shared by the heating daemon, the monitor and the tests.</summary>
internal static class HeatingPolicies
{
    /// <summary>Dead band used to avoid toggling around a threshold.</summary>
    public const double HysteresisCelsius = 0.5;

    public static bool AntifreezeShouldEngage(double temperature, AntifreezeOptions options) =>
        options.Enabled && temperature < options.ThresholdCelsius;

    public static bool AntifreezeShouldRelease(double temperature, AntifreezeOptions options) =>
        !options.Enabled || temperature > options.ThresholdCelsius + HysteresisCelsius;

    public static bool IsOutOfBand(double temperature, TemperatureBandOptions options) =>
        temperature < options.MinCelsius || temperature > options.MaxCelsius;

    public static bool BoilerRunningTooLong(TimeSpan onDuration, BoilerAlertOptions options) =>
        onDuration.TotalHours >= options.MaxContinuousHours;
}
