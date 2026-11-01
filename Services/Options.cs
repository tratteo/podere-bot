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

internal static class OptionsReader
{
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
